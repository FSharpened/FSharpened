//! Test helpers: a temporary folder removed on drop, without a dependency on a crate for it.

use std::fs;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicUsize, Ordering};

static COUNTER: AtomicUsize = AtomicUsize::new(0);

pub struct TempDir(PathBuf);

impl TempDir {
    pub fn new(label: &str) -> TempDir {
        let n = COUNTER.fetch_add(1, Ordering::Relaxed);
        let path =
            std::env::temp_dir().join(format!("fsh-watch-{}-{}-{}", std::process::id(), label, n));
        let _ = fs::remove_dir_all(&path);
        fs::create_dir_all(&path).expect("create temp dir");
        // Resolve links (macOS /var -> /private/var) so event paths and the root compare equal.
        let path = fs::canonicalize(&path).unwrap_or(path);
        TempDir(strip_verbatim(path))
    }

    pub fn path(&self) -> &Path {
        &self.0
    }

    pub fn write(&self, rel: &str, text: &str) {
        self.write_bytes(rel, text.as_bytes());
    }

    pub fn write_bytes(&self, rel: &str, bytes: &[u8]) {
        let full = self.0.join(rel);
        if let Some(parent) = full.parent() {
            fs::create_dir_all(parent).expect("create parent");
        }
        fs::write(full, bytes).expect("write file");
    }
}

impl Drop for TempDir {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.0);
    }
}

/// `canonicalize` on Windows returns a `\?\` path; the library is given ordinary paths, so tests use them too.
fn strip_verbatim(path: PathBuf) -> PathBuf {
    match path.to_str().and_then(|s| s.strip_prefix(r"\?\")) {
        Some(rest) => PathBuf::from(rest),
        None => path,
    }
}
