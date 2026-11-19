//! Fast text search over a folder. The rules mirror the managed `Search` module of `FSharpened.Ide`, so the IDE shows
//! the same results whichever implementation answered: one line at a time, 1-based line and UTF-16 column, preview
//! slice, whole-word and case options, include/exclude globs, a cap on hits in total and per file, and binary, oversize
//! and non-UTF-8 files skipped. Differences are deliberate and small: `.gitignore` files are honoured, and a regular
//! expression uses the `regex` crate's syntax (no look-around or back-references); a pattern it cannot compile comes
//! back as an error, and the caller falls back to the managed engine.

use crate::glob::{self, Globs};
use crate::json;
use ignore::WalkBuilder;
use regex::{Regex, RegexBuilder};
use std::cmp::Ordering;
use std::fs;
use std::path::Path;
use std::sync::atomic::{AtomicUsize, Ordering as AtomicOrdering};

pub const MAX_QUERY: usize = 500;
const PREVIEW_LENGTH: usize = 200;
const MAX_DEPTH: usize = 13;

#[derive(Clone, Debug)]
pub struct Options {
    pub query: String,
    pub case_sensitive: bool,
    pub whole_word: bool,
    pub regex: bool,
    pub include: String,
    pub exclude: String,
    pub ignore_dirs: Vec<String>,
    pub use_ignore_files: bool,
    pub max_hits: usize,
    pub max_per_file: usize,
    pub max_file_bytes: u64,
}

impl Options {
    pub fn new(query: &str) -> Options {
        Options {
            query: query.to_string(),
            case_sensitive: false,
            whole_word: false,
            regex: false,
            include: String::new(),
            exclude: String::new(),
            ignore_dirs: crate::DEFAULT_IGNORED
                .iter()
                .map(|s| s.to_string())
                .collect(),
            use_ignore_files: true,
            max_hits: 2000,
            max_per_file: 200,
            max_file_bytes: 5 * 1024 * 1024,
        }
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Hit {
    pub line: usize,
    /// 1-based, in UTF-16 code units (what .NET strings index by).
    pub column: usize,
    /// In UTF-16 code units.
    pub length: usize,
    pub preview: String,
    pub preview_start: usize,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct FileHits {
    pub path: String,
    pub hits: Vec<Hit>,
    pub more: bool,
}

#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct Outcome {
    pub files: Vec<FileHits>,
    pub total: usize,
    pub files_searched: usize,
    pub truncated: bool,
    /// Why the search could not run (an invalid pattern), or empty.
    pub error: String,
}

enum Matcher {
    Plain { re: Regex, whole_word: bool },
    Pattern { re: Regex },
}

fn is_word(c: char) -> bool {
    c.is_alphanumeric() || c == '_'
}

fn build(o: &Options) -> Result<Matcher, String> {
    if o.query.is_empty() {
        return Err(String::new());
    }
    if o.query.encode_utf16().count() > MAX_QUERY {
        return Err("the search text is longer than 500 characters".to_string());
    }
    if o.regex {
        let pattern = if o.whole_word {
            format!("\\b(?:{})\\b", o.query)
        } else {
            o.query.clone()
        };
        RegexBuilder::new(&pattern)
            .case_insensitive(!o.case_sensitive)
            .size_limit(20 * 1024 * 1024)
            .build()
            .map(|re| Matcher::Pattern { re })
            .map_err(|e| format!("not a valid regular expression: {e}"))
    } else {
        RegexBuilder::new(&regex::escape(&o.query))
            .case_insensitive(!o.case_sensitive)
            .build()
            .map(|re| Matcher::Plain {
                re,
                whole_word: o.whole_word,
            })
            .map_err(|e| format!("the search text cannot be searched: {e}"))
    }
}

fn first_char_len(s: &str) -> usize {
    s.chars().next().map_or(1, char::len_utf8)
}

fn boundary_ok(line: &str, start: usize, end: usize) -> bool {
    let before = line[..start].chars().next_back();
    let after = line[end..].chars().next();
    !before.is_some_and(is_word) && !after.is_some_and(is_word)
}

/// Calls `found(start, end)` (byte offsets) for each match in the line, in order, until it returns false.
fn each_match(m: &Matcher, line: &str, mut found: impl FnMut(usize, usize) -> bool) {
    match m {
        Matcher::Plain { re, whole_word } => {
            let mut pos = 0;
            while pos <= line.len() {
                let Some(hit) = re.find_at(line, pos) else {
                    break;
                };
                if !*whole_word || boundary_ok(line, hit.start(), hit.end()) {
                    if !found(hit.start(), hit.end()) {
                        break;
                    }
                    pos = hit.end();
                } else {
                    // A candidate that fails the word test: the next one may overlap it.
                    pos = hit.start() + first_char_len(&line[hit.start()..]);
                }
            }
        }
        Matcher::Pattern { re } => {
            let mut pos = 0;
            while pos <= line.len() {
                let Some(hit) = re.find_at(line, pos) else {
                    break;
                };
                if !found(hit.start(), hit.end()) {
                    break;
                }
                pos = if hit.is_empty() {
                    hit.end() + first_char_len(&line[hit.end()..])
                } else {
                    hit.end()
                };
            }
        }
    }
}

fn utf16_len(s: &str) -> usize {
    if s.is_ascii() {
        s.len()
    } else {
        s.chars().map(char::len_utf16).sum()
    }
}

fn make_hit(line: &str, line_no: usize, start: usize, end: usize) -> Hit {
    let units: Vec<u16> = line.encode_utf16().collect();
    let start16 = utf16_len(&line[..start]);
    let length = utf16_len(&line[start..end]);
    let lead: usize = line
        .chars()
        .take_while(|c| c.is_whitespace())
        .map(char::len_utf16)
        .sum();
    let first = if start16 <= 40 {
        lead.min(start16)
    } else {
        start16 - 30
    };
    let take = PREVIEW_LENGTH.min(units.len().saturating_sub(first));
    Hit {
        line: line_no,
        column: start16 + 1,
        length,
        preview: String::from_utf16_lossy(&units[first..first + take]),
        preview_start: first,
    }
}

/// Every match of the pattern in one text (a file with its byte-order mark already removed).
fn find_in_text(
    m: &Matcher,
    plain_prefilter: Option<&Regex>,
    max_per_file: usize,
    text: &str,
) -> (Vec<Hit>, bool) {
    if plain_prefilter.is_some_and(|re| !re.is_match(text)) {
        return (Vec::new(), false);
    }
    let mut hits: Vec<Hit> = Vec::new();
    let mut more = false;
    'lines: for (i, raw) in text.split('\n').enumerate() {
        let line = raw.strip_suffix('\r').unwrap_or(raw);
        let mut stop = false;
        each_match(m, line, |start, end| {
            if hits.len() >= max_per_file {
                more = true;
                stop = true;
                return false;
            }
            hits.push(make_hit(line, i + 1, start, end));
            true
        });
        if stop {
            break 'lines;
        }
    }
    (hits, more)
}

/// The order of the explorer and of the managed search: within a folder the files come first, then the sub-folders,
/// each alphabetical without regard to case.
pub fn cmp_paths(a: &str, b: &str) -> Ordering {
    let pa: Vec<&str> = a.split('/').collect();
    let pb: Vec<&str> = b.split('/').collect();
    for i in 0..pa.len().min(pb.len()) {
        let a_file = i == pa.len() - 1;
        let b_file = i == pb.len() - 1;
        if a_file != b_file {
            return if a_file {
                Ordering::Less
            } else {
                Ordering::Greater
            };
        }
        let ord = pa[i].to_lowercase().cmp(&pb[i].to_lowercase());
        if ord != Ordering::Equal {
            return ord;
        }
    }
    pa.len().cmp(&pb.len())
}

/// Every file below the root that passes the folder and glob rules, as relative paths with forward slashes, sorted.
pub fn list_files(root: &Path, o: &Options) -> Vec<String> {
    let ignored: Vec<String> = o.ignore_dirs.clone();
    let includes = Globs::parse(&o.include);
    let excludes = Globs::parse(&o.exclude);
    let mut walker = WalkBuilder::new(root);
    walker
        .hidden(false)
        .parents(false)
        .git_global(false)
        .require_git(false)
        .follow_links(false)
        .max_depth(Some(MAX_DEPTH))
        .git_ignore(o.use_ignore_files)
        .git_exclude(o.use_ignore_files)
        .ignore(o.use_ignore_files)
        .filter_entry(move |e| {
            !(e.file_type().is_some_and(|t| t.is_dir())
                && e.depth() > 0
                && e.file_name()
                    .to_str()
                    .is_some_and(|n| ignored.iter().any(|i| i == n)))
        });
    let mut files: Vec<String> = Vec::new();
    for entry in walker.build().flatten() {
        if !entry.file_type().is_some_and(|t| t.is_file()) {
            continue;
        }
        let Ok(rel) = entry.path().strip_prefix(root) else {
            continue;
        };
        let Some(rel) = rel.to_str() else {
            continue;
        };
        let rel = rel.replace('\\', "/");
        if glob::allowed(&includes, &excludes, &rel) {
            files.push(rel);
        }
    }
    files.sort_by(|a, b| cmp_paths(a, b));
    files
}

/// The text of a file, or None when it is not searchable (unreadable, too large, binary, not UTF-8).
fn read_text(path: &Path, max_bytes: u64) -> Option<String> {
    let meta = fs::metadata(path).ok()?;
    if meta.len() > max_bytes {
        return None;
    }
    let bytes = fs::read(path).ok()?;
    if bytes[..bytes.len().min(8192)].contains(&0) {
        return None;
    }
    let body = bytes.strip_prefix(&[0xEF, 0xBB, 0xBF]).unwrap_or(&bytes);
    std::str::from_utf8(body).ok().map(str::to_string)
}

struct FileResult {
    searched: bool,
    hits: Vec<Hit>,
    more: bool,
}

fn search_file(
    root: &Path,
    rel: &str,
    m: &Matcher,
    prefilter: Option<&Regex>,
    o: &Options,
) -> FileResult {
    match read_text(&root.join(rel), o.max_file_bytes) {
        None => FileResult {
            searched: false,
            hits: Vec::new(),
            more: false,
        },
        Some(text) => {
            let (hits, more) = find_in_text(m, prefilter, o.max_per_file, &text);
            FileResult {
                searched: true,
                hits,
                more,
            }
        }
    }
}

/// Searches the folder. Files are read on several threads, a few at a time, and their results are taken in file order,
/// so the same folder always gives the same answer and the cap on hits cuts it at the same place.
pub fn run(root: &Path, o: &Options) -> Outcome {
    let matcher = match build(o) {
        Ok(m) => m,
        Err(error) => {
            return Outcome {
                error,
                ..Outcome::default()
            };
        }
    };
    let prefilter = match &matcher {
        Matcher::Plain { re, .. } => Some(re),
        Matcher::Pattern { .. } => None,
    };
    let files = list_files(root, o);
    let workers = std::thread::available_parallelism()
        .map_or(2, |n| n.get())
        .clamp(1, 8);
    let batch = workers * 8;

    let mut out = Outcome::default();
    'batches: for chunk in files.chunks(batch) {
        let next = AtomicUsize::new(0);
        let mut results: Vec<Option<FileResult>> = Vec::new();
        results.resize_with(chunk.len(), || None);
        let slots: Vec<std::sync::Mutex<Option<FileResult>>> =
            results.into_iter().map(std::sync::Mutex::new).collect();
        std::thread::scope(|scope| {
            for _ in 0..workers.min(chunk.len()) {
                scope.spawn(|| {
                    loop {
                        let i = next.fetch_add(1, AtomicOrdering::Relaxed);
                        if i >= chunk.len() {
                            break;
                        }
                        let result = search_file(root, &chunk[i], &matcher, prefilter, o);
                        if let Ok(mut slot) = slots[i].lock() {
                            *slot = Some(result);
                        }
                    }
                });
            }
        });
        for (rel, slot) in chunk.iter().zip(slots) {
            let Some(result) = slot.into_inner().ok().flatten() else {
                continue;
            };
            if !result.searched {
                continue;
            }
            out.files_searched += 1;
            if result.hits.is_empty() {
                continue;
            }
            let room = o.max_hits - out.total;
            if result.hits.len() > room {
                out.files.push(FileHits {
                    path: rel.clone(),
                    hits: result.hits.into_iter().take(room).collect(),
                    more: true,
                });
                out.total = o.max_hits;
                out.truncated = true;
            } else {
                out.total += result.hits.len();
                out.files.push(FileHits {
                    path: rel.clone(),
                    hits: result.hits,
                    more: result.more,
                });
                if out.total >= o.max_hits {
                    out.truncated = true;
                }
            }
            if out.truncated {
                break 'batches;
            }
        }
    }
    out
}

impl Outcome {
    /// The JSON the C ABI returns; field names match the managed `SearchResult`, `FileHits` and `SearchHit` records.
    pub fn to_json(&self) -> String {
        let mut s = String::with_capacity(256 + self.total * 96);
        s.push_str("{\"files\":[");
        for (i, f) in self.files.iter().enumerate() {
            if i > 0 {
                s.push(',');
            }
            s.push_str("{\"path\":");
            json::push_str(&mut s, &f.path);
            s.push_str(",\"more\":");
            json::push_bool(&mut s, f.more);
            s.push_str(",\"hits\":[");
            for (j, h) in f.hits.iter().enumerate() {
                if j > 0 {
                    s.push(',');
                }
                s.push_str(&format!(
                    "{{\"line\":{},\"column\":{},\"length\":{},\"previewStart\":{},\"preview\":",
                    h.line, h.column, h.length, h.preview_start
                ));
                json::push_str(&mut s, &h.preview);
                s.push('}');
            }
            s.push_str("]}");
        }
        s.push_str(&format!(
            "],\"total\":{},\"filesSearched\":{},\"truncated\":",
            self.total, self.files_searched
        ));
        json::push_bool(&mut s, self.truncated);
        s.push_str(",\"error\":");
        json::push_str(&mut s, &self.error);
        s.push('}');
        s
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::testutil::TempDir;

    fn opts(q: &str) -> Options {
        Options::new(q)
    }

    fn text_hits(o: &Options, text: &str) -> Vec<Hit> {
        let m = build(o).expect("pattern builds");
        find_in_text(&m, None, o.max_per_file, text).0
    }

    #[test]
    fn plain_search_is_case_insensitive_by_default() {
        let hits = text_hits(&opts("hello"), "say Hello\nHELLO hello");
        let at: Vec<(usize, usize)> = hits.iter().map(|h| (h.line, h.column)).collect();
        assert_eq!(at, vec![(1, 5), (2, 1), (2, 7)]);
    }

    #[test]
    fn case_sensitive_option() {
        let mut o = opts("Hello");
        o.case_sensitive = true;
        assert_eq!(text_hits(&o, "Hello hello HELLO").len(), 1);
    }

    #[test]
    fn whole_word_needs_boundaries_and_keeps_scanning_after_a_miss() {
        let mut o = opts("aba");
        o.whole_word = true;
        assert_eq!(text_hits(&o, "ababa aba").len(), 1);
        let mut o = opts("cat");
        o.whole_word = true;
        assert_eq!(text_hits(&o, "cat concat cat_ cat.").len(), 2);
    }

    #[test]
    fn regex_search_and_whole_word_wrapper() {
        let mut o = opts("fo+");
        o.regex = true;
        assert_eq!(text_hits(&o, "f foo fooo").len(), 2);
        o.whole_word = true;
        assert_eq!(text_hits(&o, "foo xfoo foox").len(), 1);
    }

    #[test]
    fn invalid_regex_is_an_error_not_a_crash() {
        let mut o = opts("(");
        o.regex = true;
        let e = build(&o).err().expect("must fail");
        assert!(e.starts_with("not a valid regular expression"), "{e}");
    }

    #[test]
    fn empty_and_overlong_queries_are_refused() {
        assert_eq!(build(&opts("")).err(), Some(String::new()));
        let long = "x".repeat(501);
        assert!(build(&opts(&long)).err().unwrap().contains("500"));
    }

    #[test]
    fn columns_and_lengths_count_utf16_units() {
        // 😀 is two UTF-16 units; ä is one but two UTF-8 bytes.
        let hits = text_hits(&opts("x"), "😀ä x");
        assert_eq!(hits.len(), 1);
        assert_eq!(hits[0].column, 5);
        assert_eq!(hits[0].length, 1);
        let hits = text_hits(&opts("😀"), "a😀");
        assert_eq!((hits[0].column, hits[0].length), (2, 2));
    }

    #[test]
    fn crlf_lines_do_not_leak_the_carriage_return() {
        let mut o = opts("end$");
        o.regex = true;
        assert_eq!(text_hits(&o, "the end\r\nno").len(), 1);
    }

    #[test]
    fn per_file_cap_sets_more() {
        let mut o = opts("a");
        o.max_per_file = 3;
        let m = build(&o).unwrap();
        let (hits, more) = find_in_text(&m, None, 3, "a a a a a");
        assert_eq!(hits.len(), 3);
        assert!(more);
    }

    #[test]
    fn preview_starts_at_the_first_non_blank_for_early_matches_and_trails_late_ones() {
        let h = &text_hits(&opts("needle"), "    needle here")[0];
        assert_eq!((h.preview_start, h.preview.as_str()), (4, "needle here"));
        let long = format!("{}needle", "x".repeat(100));
        let h = &text_hits(&opts("needle"), &long)[0];
        assert_eq!(h.preview_start, 70);
        assert!(h.preview.ends_with("needle"));
    }

    #[test]
    fn empty_regex_matches_step_over_without_looping() {
        let mut o = opts("^");
        o.regex = true;
        assert_eq!(text_hits(&o, "a\nb").len(), 2);
        let mut o = opts("x*");
        o.regex = true;
        assert_eq!(text_hits(&o, "ab").len(), 3);
    }

    #[test]
    fn file_order_puts_files_before_folders() {
        let mut v = vec!["b/z.fs", "B.fs", "a/y.fs", "a.fs", "b/a/q.fs"];
        v.sort_by(|a, b| cmp_paths(a, b));
        assert_eq!(v, vec!["a.fs", "B.fs", "a/y.fs", "b/z.fs", "b/a/q.fs"]);
    }

    fn fixture() -> TempDir {
        let d = TempDir::new("search");
        d.write("src/a.fs", "let hello = 1\nlet world = 2\n");
        d.write("src/deep/b.fs", "// Hello again\n");
        d.write("notes.md", "nothing here\r\nhello CRLF\r\n");
        d.write("node_modules/p/x.js", "hello hidden\n");
        d.write("bin/out.txt", "hello hidden\n");
        d.write("obj/x.txt", "hello hidden\n");
        d.write("target/x.txt", "hello hidden\n");
        d.write(".git/config", "hello hidden\n");
        d.write_bytes("blob.bin", &[104, 101, 108, 108, 111, 0, 1, 2]);
        d.write_bytes("latin1.txt", &[104, 101, 108, 108, 111, 0xE9]);
        d.write("bom.txt", "\u{FEFF}hello bom\n");
        d.write(".hidden/h.txt", "hello dotdir\n");
        d
    }

    #[test]
    fn folder_search_skips_ignored_folders_binary_and_non_utf8() {
        let d = fixture();
        let out = run(d.path(), &opts("hello"));
        let paths: Vec<&str> = out.files.iter().map(|f| f.path.as_str()).collect();
        assert_eq!(
            paths,
            vec![
                "bom.txt",
                "notes.md",
                ".hidden/h.txt",
                "src/a.fs",
                "src/deep/b.fs"
            ]
        );
        assert_eq!(out.total, 5);
        assert!(!out.truncated);
        assert_eq!(out.error, "");
        // bom.txt: the byte-order mark is not part of the column.
        assert_eq!(out.files[0].hits[0].column, 1);
        // blob.bin and latin1.txt are not "searched".
        assert_eq!(out.files_searched, 5);
    }

    #[test]
    fn include_and_exclude_globs_apply() {
        let d = fixture();
        let mut o = opts("hello");
        o.include = "*.fs".into();
        let out = run(d.path(), &o);
        assert_eq!(out.files.len(), 2);
        o.exclude = "deep".into();
        let out = run(d.path(), &o);
        assert_eq!(out.files.len(), 1);
        assert_eq!(out.files[0].path, "src/a.fs");
    }

    #[test]
    fn gitignore_is_honoured_without_a_git_repository_and_can_be_switched_off() {
        let d = fixture();
        d.write(".gitignore", "notes.md\n");
        let mut o = opts("hello");
        let out = run(d.path(), &o);
        assert!(!out.files.iter().any(|f| f.path == "notes.md"));
        o.use_ignore_files = false;
        let out = run(d.path(), &o);
        assert!(out.files.iter().any(|f| f.path == "notes.md"));
    }

    #[test]
    fn result_cap_truncates_in_file_order() {
        let d = TempDir::new("cap");
        for n in 0..10 {
            d.write(&format!("f{n}.txt"), "x x x\n");
        }
        let mut o = opts("x");
        o.max_hits = 7;
        let out = run(d.path(), &o);
        assert_eq!(out.total, 7);
        assert!(out.truncated);
        assert_eq!(out.files.len(), 3);
        assert_eq!(out.files[0].path, "f0.txt");
        assert_eq!(out.files[2].hits.len(), 1);
        assert!(out.files[2].more);
    }

    #[test]
    fn oversize_files_are_skipped() {
        let d = TempDir::new("big");
        d.write("small.txt", "hit\n");
        d.write("big.txt", &format!("hit {}", "x".repeat(2000)));
        let mut o = opts("hit");
        o.max_file_bytes = 1000;
        let out = run(d.path(), &o);
        assert_eq!(out.files.len(), 1);
        assert_eq!(out.files[0].path, "small.txt");
    }

    #[test]
    fn json_has_the_managed_field_names_and_escapes() {
        let d = TempDir::new("json");
        d.write("quote.txt", "tab\there \"x\"\n");
        let out = run(d.path(), &opts("here"));
        let j = out.to_json();
        assert!(j.contains("\"filesSearched\":1"), "{j}");
        assert!(j.contains("\"previewStart\":0"), "{j}");
        assert!(j.contains("tab\\there"), "{j}");
        assert!(j.starts_with("{\"files\":[{\"path\":"), "{j}");
        assert!(j.ends_with("\"error\":\"\"}"), "{j}");
    }

    #[test]
    fn an_invalid_pattern_returns_its_error_in_the_outcome() {
        let d = TempDir::new("err");
        let mut o = opts("[");
        o.regex = true;
        let out = run(d.path(), &o);
        assert!(out.error.starts_with("not a valid regular expression"));
        assert!(out.files.is_empty());
    }
}
