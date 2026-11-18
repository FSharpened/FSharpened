//! File watching and fast text search for the FSharpened desktop IDE (PLAN P7.8).
//!
//! The boundary is the C ABI, with the conventions of `fsh-interop-example` (ADR-0019, docs/packages/Interop.Rust.md):
//!
//! * Every function returns an `i32` status (0 = OK) and writes results through out-pointers.
//! * After a failure `fsh_watch_last_error` returns a message for the calling thread.
//! * Strings are NUL-terminated UTF-8. A string the library returns is owned by it and goes back to
//!   `fsh_watch_free_string`; a string the caller passes in is only borrowed. Results are JSON documents.
//! * A panic never crosses the boundary: it is caught and reported as `FSH_PANIC`.
//!
//! Two features share the library: a recursive folder watcher that reports debounced batches
//! (`fsh_watch_start`, `fsh_watch_poll`, `fsh_watch_stop`) and a search over the files of a folder (`fsh_search`).
#![deny(unsafe_op_in_unsafe_fn)]

mod glob;
mod json;
mod search;
#[cfg(test)]
mod testutil;
mod watch;

use std::cell::RefCell;
use std::collections::HashMap;
use std::ffi::{CStr, CString, c_char};
use std::panic::{AssertUnwindSafe, catch_unwind};
use std::path::Path;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex, OnceLock};
use std::time::Duration;

pub const FSH_OK: i32 = 0;
pub const FSH_NULL_POINTER: i32 = 1;
pub const FSH_INVALID_UTF8: i32 = 2;
pub const FSH_INVALID_ARGUMENT: i32 = 3;
pub const FSH_PANIC: i32 = 4;
pub const FSH_FAILED: i32 = 5;

/// Bumped whenever a function is removed or its signature changes. Callers check it once at load time.
pub const FSH_WATCH_ABI_VERSION: u32 = 1;

/// Folders that are dependencies or build output; neither the watcher nor the search looks inside them. The caller may
/// pass its own list instead (the IDE passes the list the explorer uses).
pub const DEFAULT_IGNORED: [&str; 5] = [".git", "node_modules", "bin", "obj", "target"];

/// Search flags (`fsh_search`).
pub const FLAG_CASE_SENSITIVE: u32 = 1;
pub const FLAG_WHOLE_WORD: u32 = 2;
pub const FLAG_REGEX: u32 = 4;
pub const FLAG_IGNORE_FILES: u32 = 8;

thread_local! {
    static LAST_ERROR: RefCell<Option<CString>> = const { RefCell::new(None) };
}

type Failure = (i32, String);

fn set_last_error(message: &str) {
    let text = CString::new(message.replace('\0', "?")).unwrap_or_default();
    LAST_ERROR.with(|slot| *slot.borrow_mut() = Some(text));
}

fn clear_last_error() {
    LAST_ERROR.with(|slot| *slot.borrow_mut() = None);
}

/// Runs `body` so that neither an error nor a panic unwinds into the caller.
fn guard(body: impl FnOnce() -> Result<(), Failure>) -> i32 {
    clear_last_error();
    match catch_unwind(AssertUnwindSafe(body)) {
        Ok(Ok(())) => FSH_OK,
        Ok(Err((code, message))) => {
            set_last_error(&message);
            code
        }
        Err(payload) => {
            let message = payload
                .downcast_ref::<&str>()
                .map(|s| (*s).to_string())
                .or_else(|| payload.downcast_ref::<String>().cloned())
                .unwrap_or_else(|| "unknown panic".to_string());
            set_last_error(&format!("panic: {message}"));
            FSH_PANIC
        }
    }
}

/// Reads a required string argument.
///
/// # Safety
/// `p` must be null or point to a NUL-terminated string that stays valid for the call.
unsafe fn required<'a>(p: *const c_char, what: &str) -> Result<&'a str, Failure> {
    if p.is_null() {
        return Err((FSH_NULL_POINTER, format!("{what} must not be null")));
    }
    unsafe { CStr::from_ptr(p) }
        .to_str()
        .map_err(|_| (FSH_INVALID_UTF8, format!("{what} is not valid UTF-8")))
}

/// Reads an optional string argument; null is "not given".
///
/// # Safety
/// As for `required`.
unsafe fn optional<'a>(p: *const c_char, what: &str) -> Result<Option<&'a str>, Failure> {
    if p.is_null() {
        Ok(None)
    } else {
        unsafe { required(p, what) }.map(Some)
    }
}

fn ignore_list(csv: Option<&str>) -> Vec<String> {
    match csv {
        None => DEFAULT_IGNORED.iter().map(|s| s.to_string()).collect(),
        Some(list) => list
            .split(',')
            .map(str::trim)
            .filter(|s| !s.is_empty())
            .map(str::to_string)
            .collect(),
    }
}

/// Hands a JSON document to the caller.
///
/// # Safety
/// `out` must be a valid pointer to write one pointer to.
unsafe fn give(out: *mut *mut c_char, text: String) -> Result<(), Failure> {
    let c = CString::new(text.replace('\0', "?"))
        .map_err(|_| (FSH_FAILED, "result holds a NUL".to_string()))?;
    unsafe { *out = c.into_raw() };
    Ok(())
}

fn registry() -> &'static Mutex<HashMap<u64, Arc<watch::Watch>>> {
    static REGISTRY: OnceLock<Mutex<HashMap<u64, Arc<watch::Watch>>>> = OnceLock::new();
    REGISTRY.get_or_init(|| Mutex::new(HashMap::new()))
}

static NEXT_HANDLE: AtomicU64 = AtomicU64::new(1);

fn find_watch(handle: u64) -> Result<Arc<watch::Watch>, Failure> {
    registry()
        .lock()
        .map_err(|_| (FSH_FAILED, "watch registry is poisoned".to_string()))?
        .get(&handle)
        .cloned()
        .ok_or((
            FSH_INVALID_ARGUMENT,
            format!("no watch with handle {handle}"),
        ))
}

/// The ABI version this library implements.
#[unsafe(no_mangle)]
pub extern "C" fn fsh_watch_abi_version() -> u32 {
    FSH_WATCH_ABI_VERSION
}

/// Starts watching a folder and everything below it. `ignore_csv` is a comma-separated list of folder names to leave
/// out (null: the default list). Changes are collected for `debounce_ms` of quiet before they are reported.
/// Writes a handle for the other watch functions.
///
/// # Safety
/// `root` and `ignore_csv` (when not null) must be NUL-terminated strings; `out_handle` must be a valid pointer.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fsh_watch_start(
    root: *const c_char,
    ignore_csv: *const c_char,
    debounce_ms: u32,
    out_handle: *mut u64,
) -> i32 {
    guard(|| {
        if out_handle.is_null() {
            return Err((FSH_NULL_POINTER, "out_handle must not be null".to_string()));
        }
        let root = unsafe { required(root, "root") }?;
        let ignore = ignore_list(unsafe { optional(ignore_csv, "ignore_csv") }?);
        let debounce = Duration::from_millis(u64::from(debounce_ms.clamp(10, 10_000)));
        let started =
            watch::Watch::start(Path::new(root), ignore, debounce).map_err(|e| (FSH_FAILED, e))?;
        let handle = NEXT_HANDLE.fetch_add(1, Ordering::Relaxed);
        registry()
            .lock()
            .map_err(|_| (FSH_FAILED, "watch registry is poisoned".to_string()))?
            .insert(handle, Arc::new(started));
        unsafe { *out_handle = handle };
        Ok(())
    })
}

/// Waits up to `timeout_ms` for the next batch of changes. Writes a JSON document
/// `{"overflow":bool,"events":[{"kind":"created|changed|removed|renamed","path":"...","from":"...","dir":bool}]}`,
/// or null when nothing happened in that time. Paths are relative to the watched folder with forward slashes.
/// After the watch was stopped the status is `FSH_FAILED` with the message "watch closed".
///
/// # Safety
/// `out_json` must be a valid pointer to write one pointer to; a non-null result goes to `fsh_watch_free_string`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fsh_watch_poll(
    handle: u64,
    timeout_ms: u32,
    out_json: *mut *mut c_char,
) -> i32 {
    guard(|| {
        if out_json.is_null() {
            return Err((FSH_NULL_POINTER, "out_json must not be null".to_string()));
        }
        unsafe { *out_json = std::ptr::null_mut() };
        let w = find_watch(handle)?;
        match w.poll(Duration::from_millis(u64::from(timeout_ms.min(60_000)))) {
            watch::Poll::Batch(b) => unsafe { give(out_json, b.to_json()) },
            watch::Poll::Idle => Ok(()),
            watch::Poll::Closed => Err((FSH_FAILED, "watch closed".to_string())),
        }
    })
}

/// Stops a watch and releases its handle. A poll that is waiting on it returns.
#[unsafe(no_mangle)]
pub extern "C" fn fsh_watch_stop(handle: u64) -> i32 {
    guard(|| {
        let removed = registry()
            .lock()
            .map_err(|_| (FSH_FAILED, "watch registry is poisoned".to_string()))?
            .remove(&handle);
        match removed {
            Some(w) => {
                w.stop();
                Ok(())
            }
            None => Err((
                FSH_INVALID_ARGUMENT,
                format!("no watch with handle {handle}"),
            )),
        }
    })
}

/// Searches the files below `root` for `query`. `flags` is a bit set (`FLAG_*`); `include` and `exclude` are
/// comma-separated file patterns (null: none); `ignore_csv` as for `fsh_watch_start`. The result is a JSON document
/// `{"files":[{"path","more","hits":[{"line","column","length","previewStart","preview"}]}],"total","filesSearched",
/// "truncated","error"}`; a pattern that cannot be compiled is reported in `error` with status `FSH_OK`.
///
/// # Safety
/// The string arguments must be NUL-terminated (`include`, `exclude`, `ignore_csv` may be null); `out_json` must be a
/// valid pointer; the result goes to `fsh_watch_free_string`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fsh_search(
    root: *const c_char,
    query: *const c_char,
    flags: u32,
    include: *const c_char,
    exclude: *const c_char,
    ignore_csv: *const c_char,
    max_hits: u32,
    max_per_file: u32,
    max_file_bytes: u64,
    out_json: *mut *mut c_char,
) -> i32 {
    guard(|| {
        if out_json.is_null() {
            return Err((FSH_NULL_POINTER, "out_json must not be null".to_string()));
        }
        unsafe { *out_json = std::ptr::null_mut() };
        let root = unsafe { required(root, "root") }?;
        let query = unsafe { required(query, "query") }?;
        if max_hits == 0 || max_per_file == 0 {
            return Err((
                FSH_INVALID_ARGUMENT,
                "max_hits and max_per_file must be at least 1".to_string(),
            ));
        }
        if !Path::new(root).is_dir() {
            return Err((FSH_FAILED, format!("not a folder: {root}")));
        }
        let mut o = search::Options::new(query);
        o.case_sensitive = flags & FLAG_CASE_SENSITIVE != 0;
        o.whole_word = flags & FLAG_WHOLE_WORD != 0;
        o.regex = flags & FLAG_REGEX != 0;
        o.use_ignore_files = flags & FLAG_IGNORE_FILES != 0;
        o.include = unsafe { optional(include, "include") }?
            .unwrap_or("")
            .to_string();
        o.exclude = unsafe { optional(exclude, "exclude") }?
            .unwrap_or("")
            .to_string();
        o.ignore_dirs = ignore_list(unsafe { optional(ignore_csv, "ignore_csv") }?);
        o.max_hits = max_hits as usize;
        o.max_per_file = max_per_file as usize;
        o.max_file_bytes = max_file_bytes;
        let outcome = search::run(Path::new(root), &o);
        unsafe { give(out_json, outcome.to_json()) }
    })
}

/// The message for the last failure on this thread, or null. Release with `fsh_watch_free_string`.
#[unsafe(no_mangle)]
pub extern "C" fn fsh_watch_last_error() -> *mut c_char {
    LAST_ERROR.with(|slot| match slot.borrow().as_ref() {
        Some(text) => text.clone().into_raw(),
        None => std::ptr::null_mut(),
    })
}

/// Releases a string this library returned. Null is accepted and ignored.
///
/// # Safety
/// `text` must be null or a pointer this library returned that has not been released yet.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fsh_watch_free_string(text: *mut c_char) {
    if !text.is_null() {
        drop(unsafe { CString::from_raw(text) });
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::testutil::TempDir;
    use std::ptr;
    use std::time::Instant;

    fn c(s: &str) -> CString {
        CString::new(s).unwrap()
    }

    fn take(p: *mut c_char) -> Option<String> {
        if p.is_null() {
            return None;
        }
        let s = unsafe { CStr::from_ptr(p) }.to_string_lossy().into_owned();
        unsafe { fsh_watch_free_string(p) };
        Some(s)
    }

    fn last_error() -> String {
        take(fsh_watch_last_error()).unwrap_or_default()
    }

    #[allow(clippy::too_many_arguments)]
    fn search(root: &Path, q: &str, flags: u32, include: Option<&str>) -> (i32, Option<String>) {
        let (r, q) = (c(root.to_str().unwrap()), c(q));
        let inc = include.map(c);
        let mut out: *mut c_char = ptr::null_mut();
        let status = unsafe {
            fsh_search(
                r.as_ptr(),
                q.as_ptr(),
                flags,
                inc.as_ref().map_or(ptr::null(), |s| s.as_ptr()),
                ptr::null(),
                ptr::null(),
                2000,
                200,
                5 * 1024 * 1024,
                &mut out,
            )
        };
        (status, take(out))
    }

    #[test]
    fn abi_version_is_one() {
        assert_eq!(fsh_watch_abi_version(), 1);
    }

    #[test]
    fn search_returns_json_through_the_c_abi() {
        let d = TempDir::new("ffi-search");
        d.write("a.txt", "one needle\n");
        let (status, json) = search(d.path(), "needle", FLAG_IGNORE_FILES, Some("*.txt"));
        assert_eq!(status, FSH_OK);
        let j = json.unwrap();
        assert!(j.contains("\"path\":\"a.txt\""), "{j}");
        assert!(j.contains("\"total\":1"), "{j}");
    }

    #[test]
    fn search_reports_a_bad_pattern_in_the_json_not_as_a_failure() {
        let d = TempDir::new("ffi-badre");
        let (status, json) = search(d.path(), "(", FLAG_REGEX, None);
        assert_eq!(status, FSH_OK);
        assert!(json.unwrap().contains("not a valid regular expression"));
    }

    #[test]
    fn search_rejects_null_and_missing_arguments_with_a_message() {
        let mut out: *mut c_char = ptr::null_mut();
        let q = c("x");
        let status = unsafe {
            fsh_search(
                ptr::null(),
                q.as_ptr(),
                0,
                ptr::null(),
                ptr::null(),
                ptr::null(),
                10,
                10,
                10,
                &mut out,
            )
        };
        assert_eq!(status, FSH_NULL_POINTER);
        assert!(last_error().contains("root"));

        let missing = c("this-folder-does-not-exist-fsh");
        let status = unsafe {
            fsh_search(
                missing.as_ptr(),
                q.as_ptr(),
                0,
                ptr::null(),
                ptr::null(),
                ptr::null(),
                10,
                10,
                10,
                &mut out,
            )
        };
        assert_eq!(status, FSH_FAILED);
        assert!(last_error().contains("not a folder"));

        let status = unsafe {
            fsh_search(
                missing.as_ptr(),
                q.as_ptr(),
                0,
                ptr::null(),
                ptr::null(),
                ptr::null(),
                0,
                10,
                10,
                &mut out,
            )
        };
        assert_eq!(status, FSH_INVALID_ARGUMENT);
    }

    #[test]
    fn search_rejects_invalid_utf8() {
        let bad = CString::new(vec![0xFFu8, b'a']).unwrap();
        let root = c(".");
        let mut out: *mut c_char = ptr::null_mut();
        let status = unsafe {
            fsh_search(
                root.as_ptr(),
                bad.as_ptr(),
                0,
                ptr::null(),
                ptr::null(),
                ptr::null(),
                10,
                10,
                10,
                &mut out,
            )
        };
        assert_eq!(status, FSH_INVALID_UTF8);
    }

    #[test]
    fn watch_lifecycle_through_the_c_abi() {
        let d = TempDir::new("ffi-watch");
        let root = c(d.path().to_str().unwrap());
        let mut handle = 0u64;
        assert_eq!(
            unsafe { fsh_watch_start(root.as_ptr(), ptr::null(), 50, &mut handle) },
            FSH_OK
        );
        assert!(handle > 0);

        d.write("hello.txt", "x");
        let deadline = Instant::now() + Duration::from_secs(15);
        let mut seen = false;
        while !seen && Instant::now() < deadline {
            let mut out: *mut c_char = ptr::null_mut();
            assert_eq!(unsafe { fsh_watch_poll(handle, 200, &mut out) }, FSH_OK);
            if let Some(j) = take(out) {
                seen = j.contains("\"path\":\"hello.txt\"") || j.contains("\"overflow\":true");
            }
        }
        assert!(seen, "no batch for hello.txt");

        assert_eq!(fsh_watch_stop(handle), FSH_OK);
        let mut out: *mut c_char = ptr::null_mut();
        assert_eq!(
            unsafe { fsh_watch_poll(handle, 10, &mut out) },
            FSH_INVALID_ARGUMENT
        );
        assert_eq!(fsh_watch_stop(handle), FSH_INVALID_ARGUMENT);
    }

    #[test]
    fn watch_start_on_a_missing_folder_fails_with_a_message() {
        let root = c("this-folder-does-not-exist-fsh");
        let mut handle = 0u64;
        assert_eq!(
            unsafe { fsh_watch_start(root.as_ptr(), ptr::null(), 50, &mut handle) },
            FSH_FAILED
        );
        assert!(last_error().contains("not a folder"));
        assert_eq!(
            unsafe { fsh_watch_start(ptr::null(), ptr::null(), 50, &mut handle) },
            FSH_NULL_POINTER
        );
    }

    #[test]
    fn a_panic_becomes_a_status_not_an_abort() {
        let status = guard(|| panic!("boom"));
        assert_eq!(status, FSH_PANIC);
        assert!(last_error().contains("boom"));
    }

    #[test]
    fn free_string_accepts_null() {
        unsafe { fsh_watch_free_string(ptr::null_mut()) };
    }

    #[test]
    fn ignore_list_parses_and_defaults() {
        assert_eq!(ignore_list(None).len(), 5);
        assert_eq!(ignore_list(Some(" a, b ,,c")), vec!["a", "b", "c"]);
    }
}
