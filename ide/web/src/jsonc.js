// VS Code's JSON files (themes, snippets, settings, package.json) are JSON with comments and trailing commas. This turns
// such text into plain JSON and parses it with the browser's own parser: nothing is evaluated, only data comes out.

/** The text with `//` and `/* *​/` comments and trailing commas removed. Strings are left exactly as they were. */
export function stripJsonc(text) {
  const t = text.charCodeAt(0) === 0xfeff ? text.slice(1) : text;
  let out = "";
  let i = 0;
  while (i < t.length) {
    const c = t[i];
    if (c === '"') {
      let j = i + 1;
      while (j < t.length && t[j] !== '"') j += t[j] === "\\" ? 2 : 1;
      out += t.slice(i, j + 1);
      i = j + 1;
    } else if (c === "/" && t[i + 1] === "/") {
      while (i < t.length && t[i] !== "\n") i++;
    } else if (c === "/" && t[i + 1] === "*") {
      const end = t.indexOf("*/", i + 2);
      i = end < 0 ? t.length : end + 2;
    } else {
      out += c;
      i++;
    }
  }
  return dropTrailingCommas(out);
}

// A comma is trailing when the next thing that is not white space closes an object or an array.
function dropTrailingCommas(text) {
  let out = "";
  let i = 0;
  while (i < text.length) {
    const c = text[i];
    if (c === '"') {
      let j = i + 1;
      while (j < text.length && text[j] !== '"') j += text[j] === "\\" ? 2 : 1;
      out += text.slice(i, j + 1);
      i = j + 1;
    } else if (c === ",") {
      let j = i + 1;
      while (j < text.length && /\s/.test(text[j])) j++;
      if (text[j] === "}" || text[j] === "]") i++;
      else { out += c; i++; }
    } else {
      out += c;
      i++;
    }
  }
  return out;
}

/** Parses JSON with comments. Throws a SyntaxError when it is not JSON even after that. */
export function parseJsonc(text) {
  return JSON.parse(stripJsonc(text));
}

/** The same, but returns `fallback` instead of throwing. */
export function tryParseJsonc(text, fallback = null) {
  try { return parseJsonc(text); } catch { return fallback; }
}
