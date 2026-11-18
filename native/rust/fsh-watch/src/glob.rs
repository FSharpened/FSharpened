//! File-name patterns for "files to include" and "files to exclude", the same rules as the managed `Glob` module of
//! `FSharpened.Ide` (`tools/FSharpened.Ide/Search.fs`): `*` and `?` inside a name, `**` across folders, comma-separated,
//! case-insensitive. A pattern with no slash matches any file or folder of that name, at any depth.

fn lower(c: char) -> char {
    let mut it = c.to_lowercase();
    match (it.next(), it.next()) {
        (Some(one), None) => one,
        _ => c,
    }
}

/// Matches one path segment against one pattern segment (`*` any run, `?` any one character).
fn segment(pat: &[char], s: &[char]) -> bool {
    let (mut pi, mut si) = (0usize, 0usize);
    let mut star: Option<usize> = None;
    let mut mark = 0usize;
    while si < s.len() {
        if pi < pat.len() && (pat[pi] == '?' || (pat[pi] != '*' && lower(pat[pi]) == lower(s[si])))
        {
            pi += 1;
            si += 1;
        } else if pi < pat.len() && pat[pi] == '*' {
            star = Some(pi);
            mark = si;
            pi += 1;
        } else if let Some(st) = star {
            pi = st + 1;
            mark += 1;
            si = mark;
        } else {
            return false;
        }
    }
    while pi < pat.len() && pat[pi] == '*' {
        pi += 1;
    }
    pi == pat.len()
}

fn parts(s: &str) -> Vec<&str> {
    s.split('/').filter(|x| !x.is_empty()).collect()
}

struct Pattern {
    segments: Vec<Vec<char>>,
    has_slash: bool,
}

fn go(ps: &[Vec<char>], ss: &[&str]) -> bool {
    match (ps.split_first(), ss.split_first()) {
        (None, _) => true,
        (Some((p, rest)), _) if p.iter().collect::<String>() == "**" => {
            go(rest, ss) || (!ss.is_empty() && go(ps, &ss[1..]))
        }
        (Some((p, prest)), Some((s, stail))) => {
            let s: Vec<char> = s.chars().collect();
            segment(p, &s) && go(prest, stail)
        }
        (Some(_), None) => false,
    }
}

impl Pattern {
    fn matches(&self, path: &str) -> bool {
        let ss = parts(path);
        if self.segments.is_empty() {
            return false;
        }
        if self.segments.len() == 1 && !self.has_slash {
            return ss.iter().any(|s| {
                let s: Vec<char> = s.chars().collect();
                segment(&self.segments[0], &s)
            });
        }
        go(&self.segments, &ss)
    }
}

/// A comma-separated list of patterns, parsed once.
pub struct Globs {
    patterns: Vec<Pattern>,
}

impl Globs {
    pub fn parse(list: &str) -> Globs {
        let patterns = list
            .split(',')
            .map(|p| p.trim().replace('\\', "/"))
            .filter(|p| !p.is_empty())
            .map(|p| Pattern {
                has_slash: p.contains('/'),
                segments: parts(&p).iter().map(|s| s.chars().collect()).collect(),
            })
            .collect();
        Globs { patterns }
    }

    pub fn is_empty(&self) -> bool {
        self.patterns.is_empty()
    }

    /// True when any pattern matches the path (or a folder it is inside).
    pub fn matches_any(&self, path: &str) -> bool {
        self.patterns.iter().any(|p| p.matches(path))
    }
}

/// Included by `includes` (empty means everything) and not excluded by `excludes` (empty means nothing).
pub fn allowed(includes: &Globs, excludes: &Globs, path: &str) -> bool {
    (includes.is_empty() || includes.matches_any(path))
        && !(!excludes.is_empty() && excludes.matches_any(path))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn ok(inc: &str, exc: &str, path: &str) -> bool {
        allowed(&Globs::parse(inc), &Globs::parse(exc), path)
    }

    #[test]
    fn empty_lists_allow_everything() {
        assert!(ok("", "", "src/a.fs"));
    }

    #[test]
    fn name_pattern_matches_at_any_depth_and_folders() {
        assert!(ok("*.fs", "", "src/deep/a.fs"));
        assert!(!ok("*.fs", "", "src/a.txt"));
        assert!(ok("src", "", "src/a.txt"));
        assert!(!ok("", "src", "src/a.txt"));
    }

    #[test]
    fn comma_separated_and_case_insensitive() {
        assert!(ok("*.FS, *.md", "", "docs/readme.md"));
        assert!(!ok("", "*.MD", "docs/readme.md"));
    }

    #[test]
    fn double_star_crosses_folders() {
        assert!(ok("src/**/*.fs", "", "src/a/b/c.fs"));
        assert!(ok("src/**/*.fs", "", "src/c.fs"));
        assert!(!ok("src/**/*.fs", "", "tests/c.fs"));
    }

    #[test]
    fn question_mark_and_star_inside_a_name() {
        assert!(ok("a?c.txt", "", "abc.txt"));
        assert!(!ok("a?c.txt", "", "abbc.txt"));
        assert!(ok("a*c.txt", "", "abbbc.txt"));
    }

    #[test]
    fn exclude_wins_over_include() {
        assert!(!ok("*.fs", "gen", "gen/a.fs"));
        assert!(ok("*.fs", "gen", "src/a.fs"));
    }

    #[test]
    fn backslashes_are_slashes() {
        assert!(ok("src\\*.fs", "", "src/a.fs"));
    }
}
