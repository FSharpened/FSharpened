//! A recursive folder watcher that reports batches. The operating system's raw events (many per save, in an order that
//! differs between Windows, Linux and macOS) go into an `Aggregator`, which waits for a quiet moment and then reports
//! what changed, once per path, judged by what is on disk when the batch closes: created, changed, removed or renamed.

use crate::json;
use notify::event::{ModifyKind, RenameMode};
use notify::{Event, EventKind, RecommendedWatcher, RecursiveMode, Watcher};
use std::collections::{BTreeMap, VecDeque};
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::mpsc::{self, RecvTimeoutError};
use std::sync::{Arc, Condvar, Mutex};
use std::thread::JoinHandle;
use std::time::{Duration, Instant};

/// More changed paths than this in one batch is reported as "overflow": the caller should look at the folder again.
const MAX_EVENTS_PER_BATCH: usize = 5000;
/// Batches the caller has not collected yet. Beyond this they are folded into one overflow batch.
const MAX_QUEUED_BATCHES: usize = 64;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Kind {
    Created,
    Changed,
    Removed,
    Renamed,
}

impl Kind {
    fn name(self) -> &'static str {
        match self {
            Kind::Created => "created",
            Kind::Changed => "changed",
            Kind::Removed => "removed",
            Kind::Renamed => "renamed",
        }
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Change {
    pub kind: Kind,
    /// Relative to the watched folder, forward slashes. For a rename, the new path.
    pub path: String,
    /// For a rename, the old path.
    pub from: Option<String>,
    pub is_dir: bool,
}

#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct Batch {
    pub changes: Vec<Change>,
    /// Events were lost or too many to list: look at the whole folder again.
    pub overflow: bool,
}

impl Batch {
    pub fn to_json(&self) -> String {
        let mut s = String::from("{\"overflow\":");
        json::push_bool(&mut s, self.overflow);
        s.push_str(",\"events\":[");
        for (i, c) in self.changes.iter().enumerate() {
            if i > 0 {
                s.push(',');
            }
            s.push_str("{\"kind\":\"");
            s.push_str(c.kind.name());
            s.push_str("\",\"path\":");
            json::push_str(&mut s, &c.path);
            if let Some(from) = &c.from {
                s.push_str(",\"from\":");
                json::push_str(&mut s, from);
            }
            s.push_str(",\"dir\":");
            json::push_bool(&mut s, c.is_dir);
            s.push('}');
        }
        s.push_str("]}");
        s
    }
}

#[derive(Clone, Copy, PartialEq, Eq)]
enum Op {
    Create,
    Remove,
    Change,
    /// Something happened to the path and the event does not say what (macOS does this for renames).
    Touch,
}

struct Pending {
    /// Whether the path existed before the first event of the batch. None when the first event did not say.
    existed_before: Option<bool>,
    dir_hint: bool,
}

/// Turns raw events into batches.
pub struct Aggregator {
    root: PathBuf,
    /// The root with links resolved, for systems that report resolved paths (macOS).
    real_root: Option<PathBuf>,
    ignore: Vec<String>,
    pending: BTreeMap<String, Pending>,
    from_side: Vec<String>,
    to_side: Vec<String>,
    pairs: Vec<(String, String)>,
    overflow: bool,
}

fn forward(p: &Path) -> Option<String> {
    p.to_str().map(|s| s.replace('\\', "/"))
}

impl Aggregator {
    pub fn new(root: &Path, ignore: Vec<String>) -> Aggregator {
        Aggregator {
            root: root.to_path_buf(),
            real_root: fs::canonicalize(root).ok(),
            ignore,
            pending: BTreeMap::new(),
            from_side: Vec::new(),
            to_side: Vec::new(),
            pairs: Vec::new(),
            overflow: false,
        }
    }

    /// The path below the root, with forward slashes, or None for the root itself, a path outside it, or a path that
    /// is inside an ignored folder.
    fn relative(&self, p: &Path) -> Option<String> {
        let rel = p
            .strip_prefix(&self.root)
            .ok()
            .or_else(|| self.real_root.as_ref().and_then(|r| p.strip_prefix(r).ok()))?;
        let rel = forward(rel)?;
        if rel.is_empty() {
            return None;
        }
        if rel
            .split('/')
            .any(|part| self.ignore.iter().any(|i| i == part))
        {
            return None;
        }
        Some(rel)
    }

    fn note(&mut self, p: &Path, op: Op, dir_hint: bool) {
        let Some(rel) = self.relative(p) else {
            return;
        };
        let origin = match op {
            Op::Create => Some(false),
            Op::Remove | Op::Change => Some(true),
            Op::Touch => None,
        };
        let entry = self.pending.entry(rel).or_insert(Pending {
            existed_before: origin,
            dir_hint,
        });
        entry.dir_hint |= dir_hint;
    }

    pub fn is_empty(&self) -> bool {
        self.pending.is_empty()
            && self.from_side.is_empty()
            && self.to_side.is_empty()
            && self.pairs.is_empty()
            && !self.overflow
    }

    /// The watcher reported an error or lost events: the caller cannot trust the batch to be complete.
    pub fn lost(&mut self) {
        self.overflow = true;
    }

    pub fn feed(&mut self, ev: &Event) {
        if ev.need_rescan() {
            self.overflow = true;
        }
        use notify::event::{CreateKind, RemoveKind};
        match &ev.kind {
            EventKind::Create(kind) => {
                for p in &ev.paths {
                    self.note(p, Op::Create, matches!(kind, CreateKind::Folder));
                }
            }
            EventKind::Remove(kind) => {
                for p in &ev.paths {
                    self.note(p, Op::Remove, matches!(kind, RemoveKind::Folder));
                }
            }
            EventKind::Modify(ModifyKind::Name(mode)) => match mode {
                RenameMode::From => {
                    for p in &ev.paths {
                        self.from_side.push(p.to_string_lossy().into_owned());
                    }
                }
                RenameMode::To => {
                    for p in &ev.paths {
                        self.to_side.push(p.to_string_lossy().into_owned());
                    }
                }
                RenameMode::Both if ev.paths.len() == 2 => {
                    self.pairs.push((
                        ev.paths[0].to_string_lossy().into_owned(),
                        ev.paths[1].to_string_lossy().into_owned(),
                    ));
                }
                _ => {
                    for p in &ev.paths {
                        self.note(p, Op::Touch, false);
                    }
                }
            },
            EventKind::Modify(ModifyKind::Metadata(_)) | EventKind::Access(_) => {}
            EventKind::Modify(_) => {
                for p in &ev.paths {
                    self.note(p, Op::Change, false);
                }
            }
            EventKind::Any | EventKind::Other => {
                for p in &ev.paths {
                    self.note(p, Op::Touch, false);
                }
            }
        }
    }

    /// Closes the batch. `exists` says whether a full path exists now, and whether it is a folder.
    pub fn take(&mut self, exists: &dyn Fn(&Path) -> Option<bool>) -> Option<Batch> {
        if self.is_empty() {
            return None;
        }
        // Pair up "renamed from" with "renamed to" when the counts agree; otherwise they are a removal and a creation.
        let mut pairs = std::mem::take(&mut self.pairs);
        let from_side = std::mem::take(&mut self.from_side);
        let to_side = std::mem::take(&mut self.to_side);
        if from_side.len() == to_side.len() {
            pairs.extend(from_side.into_iter().zip(to_side));
        } else {
            for p in from_side {
                self.note(Path::new(&p), Op::Remove, false);
            }
            for p in to_side {
                self.note(Path::new(&p), Op::Create, false);
            }
        }

        let mut changes: Vec<Change> = Vec::new();
        let mut renamed_to: Vec<String> = Vec::new();
        for (from, to) in pairs {
            let (rf, rt) = (
                self.relative(Path::new(&from)),
                self.relative(Path::new(&to)),
            );
            match (rf, rt) {
                (Some(f), Some(t)) => {
                    let is_dir = exists(&self.root.join(&t)).unwrap_or(false);
                    self.pending.remove(&f);
                    renamed_to.push(t.clone());
                    changes.push(Change {
                        kind: Kind::Renamed,
                        path: t,
                        from: Some(f),
                        is_dir,
                    });
                }
                // Moved out of sight (into an ignored folder or out of the root): it is gone from here.
                (Some(_), None) => self.note(Path::new(&from), Op::Remove, false),
                // Moved in from out of sight: it is new here.
                (None, Some(_)) => self.note(Path::new(&to), Op::Create, false),
                (None, None) => {}
            }
        }

        let pending = std::mem::take(&mut self.pending);
        for (rel, p) in pending {
            if renamed_to.contains(&rel) {
                continue;
            }
            let now = exists(&self.root.join(&rel));
            let before = p.existed_before.unwrap_or(true);
            let change = match (before, now) {
                (false, None) => None,
                (true, None) => Some((Kind::Removed, p.dir_hint)),
                (false, Some(dir)) => Some((Kind::Created, dir)),
                // A folder "changes" whenever something inside it does; that is not worth reporting.
                (true, Some(true)) => None,
                (true, Some(false)) => Some((Kind::Changed, false)),
            };
            if let Some((kind, is_dir)) = change {
                changes.push(Change {
                    kind,
                    path: rel,
                    from: None,
                    is_dir,
                });
            }
        }

        let mut overflow = std::mem::take(&mut self.overflow);
        if changes.len() > MAX_EVENTS_PER_BATCH {
            changes.clear();
            overflow = true;
        }
        changes.sort_by(|a, b| {
            (a.kind != Kind::Renamed, &a.path).cmp(&(b.kind != Kind::Renamed, &b.path))
        });
        if changes.is_empty() && !overflow {
            return None;
        }
        Some(Batch { changes, overflow })
    }
}

/// What is at a path right now: None when nothing, otherwise whether it is a folder.
pub fn exists_now(p: &Path) -> Option<bool> {
    fs::symlink_metadata(p).ok().map(|m| m.is_dir())
}

struct Shared {
    state: Mutex<Queue>,
    ready: Condvar,
}

struct Queue {
    batches: VecDeque<Batch>,
    closed: bool,
}

pub enum Poll {
    Batch(Batch),
    Idle,
    Closed,
}

/// A running watcher. Dropping it (or `stop`) ends the watch and its thread.
pub struct Watch {
    shared: Arc<Shared>,
    watcher: Mutex<Option<RecommendedWatcher>>,
    thread: Mutex<Option<JoinHandle<()>>>,
}

impl Watch {
    pub fn start(root: &Path, ignore: Vec<String>, debounce: Duration) -> Result<Watch, String> {
        if !root.is_dir() {
            return Err(format!("not a folder: {}", root.display()));
        }
        let (tx, rx) = mpsc::channel::<notify::Result<Event>>();
        let mut watcher = notify::recommended_watcher(move |res| {
            // The receiver is gone once the watch is stopped; that is not an error.
            let _ = tx.send(res);
        })
        .map_err(|e| format!("cannot create a watcher: {e}"))?;
        watcher
            .watch(root, RecursiveMode::Recursive)
            .map_err(|e| format!("cannot watch {}: {e}", root.display()))?;

        let shared = Arc::new(Shared {
            state: Mutex::new(Queue {
                batches: VecDeque::new(),
                closed: false,
            }),
            ready: Condvar::new(),
        });
        let mut agg = Aggregator::new(root, ignore);
        let for_thread = Arc::clone(&shared);
        let max_wait = (debounce * 10).max(Duration::from_millis(1000));
        let thread = std::thread::Builder::new()
            .name("fsh-watch".into())
            .spawn(move || {
                'outer: while let Ok(res) = rx.recv() {
                    // The first event of a burst has arrived.
                    feed(&mut agg, res);
                    // Keep collecting until things go quiet (or the burst has lasted too long to keep waiting).
                    let started = Instant::now();
                    loop {
                        match rx.recv_timeout(debounce) {
                            Ok(res) => {
                                feed(&mut agg, res);
                                if started.elapsed() > max_wait {
                                    break;
                                }
                            }
                            Err(RecvTimeoutError::Timeout) => break,
                            Err(RecvTimeoutError::Disconnected) => {
                                publish(&for_thread, agg.take(&exists_now));
                                break 'outer;
                            }
                        }
                    }
                    publish(&for_thread, agg.take(&exists_now));
                }
                if let Ok(mut q) = for_thread.state.lock() {
                    q.closed = true;
                }
                for_thread.ready.notify_all();
            })
            .map_err(|e| format!("cannot start the watcher thread: {e}"))?;

        Ok(Watch {
            shared,
            watcher: Mutex::new(Some(watcher)),
            thread: Mutex::new(Some(thread)),
        })
    }

    /// Waits up to `timeout` for the next batch.
    pub fn poll(&self, timeout: Duration) -> Poll {
        let Ok(mut q) = self.shared.state.lock() else {
            return Poll::Closed;
        };
        if q.batches.is_empty() && !q.closed {
            q = match self.shared.ready.wait_timeout(q, timeout) {
                Ok((guard, _)) => guard,
                Err(_) => return Poll::Closed,
            };
        }
        match q.batches.pop_front() {
            Some(b) => Poll::Batch(b),
            None if q.closed => Poll::Closed,
            None => Poll::Idle,
        }
    }

    /// Ends the watch: the operating system watcher is released, which ends the thread, and waiting polls return.
    pub fn stop(&self) {
        if let Ok(mut q) = self.shared.state.lock() {
            q.closed = true;
        }
        self.shared.ready.notify_all();
        if let Ok(mut w) = self.watcher.lock() {
            *w = None;
        }
        let handle = self.thread.lock().ok().and_then(|mut t| t.take());
        if let Some(h) = handle {
            let _ = h.join();
        }
    }
}

impl Drop for Watch {
    fn drop(&mut self) {
        self.stop();
    }
}

fn feed(agg: &mut Aggregator, res: notify::Result<Event>) {
    match res {
        Ok(ev) => agg.feed(&ev),
        Err(_) => agg.lost(),
    }
}

fn publish(shared: &Shared, batch: Option<Batch>) {
    let Some(batch) = batch else {
        return;
    };
    if let Ok(mut q) = shared.state.lock() {
        if q.batches.len() >= MAX_QUEUED_BATCHES {
            q.batches.clear();
            q.batches.push_back(Batch {
                changes: Vec::new(),
                overflow: true,
            });
        } else {
            q.batches.push_back(batch);
        }
    }
    shared.ready.notify_all();
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::testutil::TempDir;
    use notify::event::{CreateKind, DataChange, RemoveKind};

    fn ev(kind: EventKind, paths: &[&Path]) -> Event {
        let mut e = Event::new(kind);
        for p in paths {
            e = e.add_path(p.to_path_buf());
        }
        e
    }

    fn agg(d: &TempDir) -> Aggregator {
        Aggregator::new(
            d.path(),
            vec![
                ".git".into(),
                "node_modules".into(),
                "bin".into(),
                "obj".into(),
                "target".into(),
            ],
        )
    }

    fn summary(b: &Batch) -> Vec<String> {
        b.changes
            .iter()
            .map(|c| match &c.from {
                Some(f) => format!("{:?} {} -> {}", c.kind, f, c.path),
                None => format!("{:?} {}", c.kind, c.path),
            })
            .collect()
    }

    #[test]
    fn create_then_change_is_one_created() {
        let d = TempDir::new("agg1");
        d.write("a.txt", "x");
        let p = d.path().join("a.txt");
        let mut a = agg(&d);
        a.feed(&ev(EventKind::Create(CreateKind::File), &[&p]));
        a.feed(&ev(
            EventKind::Modify(ModifyKind::Data(DataChange::Any)),
            &[&p],
        ));
        a.feed(&ev(
            EventKind::Modify(ModifyKind::Data(DataChange::Any)),
            &[&p],
        ));
        let b = a.take(&exists_now).unwrap();
        assert_eq!(summary(&b), vec!["Created a.txt"]);
        assert!(
            a.take(&exists_now).is_none(),
            "taking empties the aggregator"
        );
    }

    #[test]
    fn create_then_remove_in_one_batch_vanishes() {
        let d = TempDir::new("agg2");
        let p = d.path().join("tmp.txt");
        let mut a = agg(&d);
        a.feed(&ev(EventKind::Create(CreateKind::File), &[&p]));
        a.feed(&ev(EventKind::Remove(RemoveKind::File), &[&p]));
        assert!(a.take(&exists_now).is_none());
    }

    #[test]
    fn remove_then_create_is_a_change_the_atomic_save_pattern() {
        let d = TempDir::new("agg3");
        d.write("doc.txt", "new");
        let p = d.path().join("doc.txt");
        let mut a = agg(&d);
        a.feed(&ev(EventKind::Remove(RemoveKind::File), &[&p]));
        a.feed(&ev(EventKind::Create(CreateKind::File), &[&p]));
        let b = a.take(&exists_now).unwrap();
        assert_eq!(summary(&b), vec!["Changed doc.txt"]);
    }

    #[test]
    fn removal_reports_removed_and_keeps_the_folder_hint() {
        let d = TempDir::new("agg4");
        let p = d.path().join("gone");
        let mut a = agg(&d);
        a.feed(&ev(EventKind::Remove(RemoveKind::Folder), &[&p]));
        let b = a.take(&exists_now).unwrap();
        assert_eq!(summary(&b), vec!["Removed gone"]);
        assert!(b.changes[0].is_dir);
    }

    #[test]
    fn folder_data_changes_are_not_reported() {
        let d = TempDir::new("agg5");
        d.write("sub/a.txt", "x");
        let p = d.path().join("sub");
        let mut a = agg(&d);
        a.feed(&ev(EventKind::Modify(ModifyKind::Any), &[&p]));
        assert!(a.take(&exists_now).is_none());
    }

    #[test]
    fn ignored_folders_and_the_root_itself_are_dropped() {
        let d = TempDir::new("agg6");
        let mut a = agg(&d);
        for rel in [
            "node_modules/x/y.js",
            ".git/index",
            "bin/Debug/a.dll",
            "obj/b",
            "target/debug/c",
            "src/obj/d",
        ] {
            a.feed(&ev(
                EventKind::Create(CreateKind::File),
                &[&d.path().join(rel)],
            ));
        }
        a.feed(&ev(EventKind::Modify(ModifyKind::Any), &[d.path()]));
        assert!(a.is_empty());
        assert!(a.take(&exists_now).is_none());
    }

    #[test]
    fn paths_outside_the_root_are_dropped() {
        let d = TempDir::new("agg7");
        let other = TempDir::new("agg7b");
        let mut a = agg(&d);
        a.feed(&ev(
            EventKind::Create(CreateKind::File),
            &[&other.path().join("x")],
        ));
        assert!(a.is_empty());
    }

    #[test]
    fn rename_halves_are_paired_and_the_new_name_is_not_also_changed() {
        let d = TempDir::new("agg8");
        d.write("new.txt", "x");
        let (from, to) = (d.path().join("old.txt"), d.path().join("new.txt"));
        let mut a = agg(&d);
        a.feed(&ev(
            EventKind::Modify(ModifyKind::Name(RenameMode::From)),
            &[&from],
        ));
        a.feed(&ev(
            EventKind::Modify(ModifyKind::Name(RenameMode::To)),
            &[&to],
        ));
        a.feed(&ev(
            EventKind::Modify(ModifyKind::Data(DataChange::Any)),
            &[&to],
        ));
        let b = a.take(&exists_now).unwrap();
        assert_eq!(summary(&b), vec!["Renamed old.txt -> new.txt"]);
    }

    #[test]
    fn rename_both_in_one_event() {
        let d = TempDir::new("agg9");
        d.write("b.txt", "x");
        let mut a = agg(&d);
        a.feed(&ev(
            EventKind::Modify(ModifyKind::Name(RenameMode::Both)),
            &[&d.path().join("a.txt"), &d.path().join("b.txt")],
        ));
        assert_eq!(
            summary(&a.take(&exists_now).unwrap()),
            vec!["Renamed a.txt -> b.txt"]
        );
    }

    #[test]
    fn unmatched_rename_halves_become_removed_and_created() {
        let d = TempDir::new("agg10");
        d.write("kept.txt", "x");
        let mut a = agg(&d);
        a.feed(&ev(
            EventKind::Modify(ModifyKind::Name(RenameMode::From)),
            &[&d.path().join("lost.txt")],
        ));
        a.feed(&ev(
            EventKind::Modify(ModifyKind::Name(RenameMode::To)),
            &[&d.path().join("kept.txt")],
        ));
        a.feed(&ev(
            EventKind::Modify(ModifyKind::Name(RenameMode::To)),
            &[&d.path().join("never.txt")],
        ));
        let b = a.take(&exists_now).unwrap();
        let s = summary(&b);
        assert!(s.contains(&"Created kept.txt".to_string()), "{s:?}");
        assert!(s.contains(&"Removed lost.txt".to_string()), "{s:?}");
    }

    #[test]
    fn a_rename_into_an_ignored_folder_is_a_removal() {
        let d = TempDir::new("agg11");
        let mut a = agg(&d);
        a.feed(&ev(
            EventKind::Modify(ModifyKind::Name(RenameMode::Both)),
            &[&d.path().join("a.txt"), &d.path().join("bin/a.txt")],
        ));
        assert_eq!(
            summary(&a.take(&exists_now).unwrap()),
            vec!["Removed a.txt"]
        );
    }

    #[test]
    fn untyped_events_are_judged_by_the_disk() {
        let d = TempDir::new("agg12");
        d.write("here.txt", "x");
        let mut a = agg(&d);
        a.feed(&ev(
            EventKind::Any,
            &[&d.path().join("here.txt"), &d.path().join("gone.txt")],
        ));
        let s = summary(&a.take(&exists_now).unwrap());
        assert_eq!(s, vec!["Removed gone.txt", "Changed here.txt"]);
    }

    #[test]
    fn rescan_and_errors_set_overflow() {
        let d = TempDir::new("agg13");
        let mut a = agg(&d);
        a.feed(&Event::new(EventKind::Other).set_flag(notify::event::Flag::Rescan));
        let b = a.take(&exists_now).unwrap();
        assert!(b.overflow && b.changes.is_empty());
        a.lost();
        assert!(a.take(&exists_now).unwrap().overflow);
    }

    #[test]
    fn too_many_changes_collapse_to_overflow() {
        let d = TempDir::new("agg14");
        let mut a = agg(&d);
        for i in 0..(MAX_EVENTS_PER_BATCH + 1) {
            a.feed(&ev(
                EventKind::Remove(RemoveKind::File),
                &[&d.path().join(format!("f{i}"))],
            ));
        }
        let b = a.take(&exists_now).unwrap();
        assert!(b.overflow && b.changes.is_empty());
    }

    #[test]
    fn batch_json_shape() {
        let b = Batch {
            changes: vec![
                Change {
                    kind: Kind::Renamed,
                    path: "n".into(),
                    from: Some("o\"".into()),
                    is_dir: false,
                },
                Change {
                    kind: Kind::Created,
                    path: "d".into(),
                    from: None,
                    is_dir: true,
                },
            ],
            overflow: false,
        };
        assert_eq!(
            b.to_json(),
            "{\"overflow\":false,\"events\":[{\"kind\":\"renamed\",\"path\":\"n\",\"from\":\"o\\\"\",\"dir\":false},{\"kind\":\"created\",\"path\":\"d\",\"dir\":true}]}"
        );
    }

    /// Polls until the predicate sees what it wants, or fails after a generous time (CI machines are slow).
    fn wait_for(w: &Watch, mut want: impl FnMut(&Batch) -> bool) -> bool {
        let deadline = Instant::now() + Duration::from_secs(15);
        while Instant::now() < deadline {
            match w.poll(Duration::from_millis(200)) {
                Poll::Batch(b) if want(&b) => return true,
                _ => {}
            }
        }
        false
    }

    /// Polls until `seen` holds (it is told every change of every batch), or fails after a generous time.
    fn wait_until(w: &Watch, mut seen: impl FnMut(&Change) -> bool) -> bool {
        wait_for(w, |b| b.overflow || b.changes.iter().any(&mut seen))
    }

    #[test]
    fn a_real_watch_reports_create_change_rename_and_remove() {
        let d = TempDir::new("real");
        // The folder exists before the watch starts: a file made in a folder that is created at the same moment is
        // written before the platform (inotify) is watching that folder, and its event is never sent; the folder's
        // own creation is what arrives then. This test is about what happens to files in a watched folder.
        fs::create_dir_all(d.path().join("src")).unwrap();
        let w =
            Watch::start(d.path(), vec!["bin".into()], Duration::from_millis(50)).expect("start");

        // A fresh file is reported created (a platform may fold the write into it, so a change is accepted too).
        d.write("src/new.txt", "one");
        assert!(
            wait_until(&w, |c| c.path == "src/new.txt"
                && matches!(c.kind, Kind::Created | Kind::Changed)),
            "create"
        );

        // A rewrite is reported for the same path. Events of the first write may still be on their way, so the
        // write is repeated until the change is seen.
        let mut round = 0;
        let mut changed = false;
        while !changed && round < 5 {
            round += 1;
            d.write("src/new.txt", &format!("two {round}"));
            let deadline = Instant::now() + Duration::from_secs(3);
            while !changed && Instant::now() < deadline {
                if let Poll::Batch(b) = w.poll(Duration::from_millis(100)) {
                    changed = b.overflow
                        || b.changes
                            .iter()
                            .any(|c| c.path == "src/new.txt" && c.kind == Kind::Changed);
                }
            }
        }
        assert!(changed, "change");

        // A rename arrives as a rename, or, where the platform reports the two halves apart, as the old name
        // removed and the new one created. Either way both names are accounted for.
        fs::rename(d.path().join("src/new.txt"), d.path().join("src/moved.txt")).unwrap();
        let (mut renamed, mut old_gone, mut new_there) = (false, false, false);
        assert!(
            wait_for(&w, |b| {
                for c in &b.changes {
                    match (c.kind, c.path.as_str()) {
                        (Kind::Renamed, "src/moved.txt") => renamed = true,
                        (Kind::Removed, "src/new.txt") => old_gone = true,
                        (Kind::Created, "src/moved.txt") => new_there = true,
                        _ => {}
                    }
                }
                b.overflow || renamed || (old_gone && new_there)
            }),
            "rename"
        );

        // Ignored folders stay silent; the real change after them is what arrives.
        d.write("bin/ignored.dll", "x");
        d.write("visible.txt", "x");
        let mut saw_ignored = false;
        assert!(wait_for(&w, |b| {
            saw_ignored |= b.changes.iter().any(|c| c.path.starts_with("bin/"));
            b.overflow || b.changes.iter().any(|c| c.path == "visible.txt")
        }));
        assert!(!saw_ignored);

        // Let what is left of the writes settle, so the removal is the only thing the next wait can see.
        while let Poll::Batch(_) = w.poll(Duration::from_millis(300)) {}

        fs::remove_file(d.path().join("visible.txt")).unwrap();
        assert!(
            wait_until(&w, |c| c.path == "visible.txt" && c.kind == Kind::Removed),
            "remove"
        );

        w.stop();
        assert!(matches!(w.poll(Duration::from_millis(10)), Poll::Closed));
    }

    #[test]
    fn start_refuses_a_missing_folder() {
        let d = TempDir::new("missing");
        let r = Watch::start(&d.path().join("nope"), vec![], Duration::from_millis(50));
        assert!(r.is_err());
    }

    #[test]
    fn stop_wakes_a_waiting_poll() {
        let d = TempDir::new("stop");
        let w = Arc::new(Watch::start(d.path(), vec![], Duration::from_millis(50)).unwrap());
        let w2 = Arc::clone(&w);
        let t = std::thread::spawn(move || {
            let started = Instant::now();
            let r = w2.poll(Duration::from_secs(20));
            (matches!(r, Poll::Closed), started.elapsed())
        });
        std::thread::sleep(Duration::from_millis(200));
        w.stop();
        let (closed, took) = t.join().unwrap();
        assert!(closed);
        assert!(took < Duration::from_secs(10), "{took:?}");
    }
}
