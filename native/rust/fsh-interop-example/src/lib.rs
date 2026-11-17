//! Reference library for `FSharpened.Interop.Rust` (ADR-0019).
//!
//! Conventions every FSharpened-bound Rust library follows (docs/packages/Interop.Rust.md):
//!
//! * The boundary is the C ABI: `extern "C"`, `#[unsafe(no_mangle)]`, `cdylib`.
//! * Fallible functions return an `i32` status (0 = OK) and write results through out-pointers.
//! * After a failure, `fsh_last_error` returns a message for the calling thread.
//! * Strings are NUL-terminated UTF-8. A string the library returns is owned by the library and
//!   must be handed back to `fsh_free_string`; a string the caller passes in is only borrowed.
//! * A panic never crosses the boundary: it is caught and reported as `FSH_PANIC`.
#![deny(unsafe_op_in_unsafe_fn)]

use std::cell::RefCell;
use std::ffi::{CStr, CString, c_char};
use std::panic::{AssertUnwindSafe, catch_unwind};

pub const FSH_OK: i32 = 0;
pub const FSH_NULL_POINTER: i32 = 1;
pub const FSH_INVALID_UTF8: i32 = 2;
pub const FSH_INVALID_ARGUMENT: i32 = 3;
pub const FSH_PANIC: i32 = 4;
pub const FSH_FAILED: i32 = 5;

/// Bumped whenever a function is removed or its signature changes. Callers check it once at load time.
pub const FSH_ABI_VERSION: u32 = 1;

thread_local! {
    static LAST_ERROR: RefCell<Option<CString>> = const { RefCell::new(None) };
}

type Failure = (i32, String);

fn set_last_error(message: &str) {
    // An interior NUL would make CString::new fail; the message is for humans, so replace it.
    let text = CString::new(message.replace('\0', "?")).unwrap_or_default();
    LAST_ERROR.with(|slot| *slot.borrow_mut() = Some(text));
}

fn clear_last_error() {
    LAST_ERROR.with(|slot| *slot.borrow_mut() = None);
}

/// Runs `body` so that neither an error nor a panic unwinds into the caller.
/// Unwinding out of an `extern "C"` function aborts the whole process, so every export goes through here.
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

fn null_pointer(what: &str) -> Failure {
    (FSH_NULL_POINTER, format!("{what} must not be null"))
}

/// The ABI version this library implements.
#[unsafe(no_mangle)]
pub extern "C" fn fsh_abi_version() -> u32 {
    FSH_ABI_VERSION
}

/// Adds two integers, wrapping on overflow (defined behaviour, identical in debug and release).
#[unsafe(no_mangle)]
pub extern "C" fn fsh_add(a: i32, b: i32) -> i32 {
    a.wrapping_add(b)
}

/// Sums `len` integers starting at `values` into `*out`.
///
/// # Safety
/// `values` must point to `len` readable `i32`s (it may be null only when `len` is 0) and `out` must be writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fsh_sum(values: *const i32, len: usize, out: *mut i64) -> i32 {
    guard(|| {
        if out.is_null() {
            return Err(null_pointer("out"));
        }
        if values.is_null() && len != 0 {
            return Err(null_pointer("values"));
        }
        let slice: &[i32] = if len == 0 {
            &[]
        } else {
            unsafe { std::slice::from_raw_parts(values, len) }
        };
        let total: i64 = slice.iter().map(|&v| i64::from(v)).sum();
        unsafe { *out = total };
        Ok(())
    })
}

/// Integer division. Division by zero and `i32::MIN / -1` are reported, never trapped.
///
/// # Safety
/// `out` must be writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fsh_divide(a: i32, b: i32, out: *mut i32) -> i32 {
    guard(|| {
        if out.is_null() {
            return Err(null_pointer("out"));
        }
        let quotient = a.checked_div(b).ok_or_else(|| {
            let why = if b == 0 {
                "division by zero"
            } else {
                "overflow"
            };
            (
                FSH_INVALID_ARGUMENT,
                format!("cannot divide {a} by {b}: {why}"),
            )
        })?;
        unsafe { *out = quotient };
        Ok(())
    })
}

/// Writes a newly allocated greeting to `*out`. Release it with `fsh_free_string`.
///
/// # Safety
/// `name` must be a NUL-terminated string and `out` must be writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fsh_greet(name: *const c_char, out: *mut *mut c_char) -> i32 {
    guard(|| {
        if name.is_null() {
            return Err(null_pointer("name"));
        }
        if out.is_null() {
            return Err(null_pointer("out"));
        }
        let name = unsafe { CStr::from_ptr(name) }
            .to_str()
            .map_err(|e| (FSH_INVALID_UTF8, format!("name is not valid UTF-8: {e}")))?;
        let greeting = CString::new(format!("Hello, {name}!"))
            .map_err(|e| (FSH_INVALID_ARGUMENT, e.to_string()))?;
        unsafe { *out = greeting.into_raw() };
        Ok(())
    })
}

/// Deliberately panics, to prove a panic comes back as `FSH_PANIC` instead of aborting the process.
#[unsafe(no_mangle)]
pub extern "C" fn fsh_trigger_panic() -> i32 {
    guard(|| -> Result<(), Failure> { panic!("deliberate panic from fsh_trigger_panic") })
}

/// Returns a copy of the calling thread's last error message, or null when the last call succeeded.
/// Release it with `fsh_free_string`.
#[unsafe(no_mangle)]
pub extern "C" fn fsh_last_error() -> *mut c_char {
    LAST_ERROR.with(|slot| match &*slot.borrow() {
        Some(message) => message.clone().into_raw(),
        None => std::ptr::null_mut(),
    })
}

/// Releases a string returned by this library. Null is allowed and ignored.
///
/// # Safety
/// `text` must be null or a pointer this library returned that has not been freed yet.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fsh_free_string(text: *mut c_char) {
    if !text.is_null() {
        drop(unsafe { CString::from_raw(text) });
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn take(p: *mut c_char) -> Option<String> {
        if p.is_null() {
            return None;
        }
        let text = unsafe { CStr::from_ptr(p) }.to_string_lossy().into_owned();
        unsafe { fsh_free_string(p) };
        Some(text)
    }

    #[test]
    fn adds_and_wraps() {
        assert_eq!(fsh_add(2, 3), 5);
        assert_eq!(fsh_add(i32::MAX, 1), i32::MIN);
    }

    #[test]
    fn sums_a_slice_and_accepts_empty() {
        let values = [1, 2, 3, i32::MAX];
        let mut total = 0i64;
        assert_eq!(
            unsafe { fsh_sum(values.as_ptr(), values.len(), &mut total) },
            FSH_OK
        );
        assert_eq!(total, 6 + i64::from(i32::MAX));
        assert_eq!(unsafe { fsh_sum(std::ptr::null(), 0, &mut total) }, FSH_OK);
        assert_eq!(total, 0);
    }

    #[test]
    fn null_pointers_are_reported_with_a_message() {
        assert_eq!(
            unsafe { fsh_sum(std::ptr::null(), 3, &mut 0) },
            FSH_NULL_POINTER
        );
        assert_eq!(
            take(fsh_last_error()).as_deref(),
            Some("values must not be null")
        );
        assert_eq!(
            unsafe { fsh_sum([1].as_ptr(), 1, std::ptr::null_mut()) },
            FSH_NULL_POINTER
        );
    }

    #[test]
    fn division_reports_instead_of_trapping() {
        let mut q = 0;
        assert_eq!(unsafe { fsh_divide(7, 2, &mut q) }, FSH_OK);
        assert_eq!(q, 3);
        assert_eq!(unsafe { fsh_divide(1, 0, &mut q) }, FSH_INVALID_ARGUMENT);
        assert!(take(fsh_last_error()).unwrap().contains("division by zero"));
        assert_eq!(
            unsafe { fsh_divide(i32::MIN, -1, &mut q) },
            FSH_INVALID_ARGUMENT
        );
    }

    #[test]
    fn greets_in_utf8_and_rejects_bad_bytes() {
        let name = CString::new("Ünïcode 🦀").unwrap();
        let mut out: *mut c_char = std::ptr::null_mut();
        assert_eq!(unsafe { fsh_greet(name.as_ptr(), &mut out) }, FSH_OK);
        assert_eq!(take(out).as_deref(), Some("Hello, Ünïcode 🦀!"));

        let bad = CString::new(vec![0xFFu8, 0xFE]).unwrap();
        assert_eq!(
            unsafe { fsh_greet(bad.as_ptr(), &mut out) },
            FSH_INVALID_UTF8
        );
    }

    #[test]
    fn a_panic_becomes_a_status_and_a_message() {
        assert_eq!(fsh_trigger_panic(), FSH_PANIC);
        assert!(
            take(fsh_last_error())
                .unwrap()
                .starts_with("panic: deliberate panic")
        );
    }

    #[test]
    fn success_clears_the_previous_error() {
        let mut q = 0;
        assert_eq!(unsafe { fsh_divide(1, 0, &mut q) }, FSH_INVALID_ARGUMENT);
        assert_eq!(unsafe { fsh_divide(4, 2, &mut q) }, FSH_OK);
        assert!(fsh_last_error().is_null());
    }

    #[test]
    fn freeing_null_is_allowed() {
        unsafe { fsh_free_string(std::ptr::null_mut()) };
    }

    #[test]
    fn abi_version_is_one() {
        assert_eq!(fsh_abi_version(), 1);
    }
}
