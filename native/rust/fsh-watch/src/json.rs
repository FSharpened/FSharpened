//! A few lines of JSON writing, so the library needs no serialization crate. Everything the library returns to .NET is
//! a UTF-8 JSON document that `System.Text.Json`'s `JsonDocument` reads without reflection (safe under NativeAOT).

use std::fmt::Write;

/// Appends `s` as a JSON string literal (with the quotes).
pub fn push_str(out: &mut String, s: &str) {
    out.push('"');
    for c in s.chars() {
        match c {
            '"' => out.push_str("\\\""),
            '\\' => out.push_str("\\\\"),
            '\n' => out.push_str("\\n"),
            '\r' => out.push_str("\\r"),
            '\t' => out.push_str("\\t"),
            c if (c as u32) < 0x20 => {
                // Writing into a String cannot fail.
                let _ = write!(out, "\\u{:04x}", c as u32);
            }
            c => out.push(c),
        }
    }
    out.push('"');
}

pub fn push_bool(out: &mut String, value: bool) {
    out.push_str(if value { "true" } else { "false" });
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn escapes_quotes_backslashes_and_controls() {
        let mut s = String::new();
        push_str(&mut s, "a\"b\\c\nd\te\u{1}f");
        assert_eq!(s, "\"a\\\"b\\\\c\\nd\\te\\u0001f\"");
    }

    #[test]
    fn keeps_non_ascii_as_is() {
        let mut s = String::new();
        push_str(&mut s, "größe 日本 😀");
        assert_eq!(s, "\"größe 日本 😀\"");
    }

    #[test]
    fn writes_booleans() {
        let mut s = String::new();
        push_bool(&mut s, true);
        push_bool(&mut s, false);
        assert_eq!(s, "truefalse");
    }
}
