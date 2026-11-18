// The static fallback: deterministic keyword completions, the answer when AI is off, over budget, failing or slow
// (ADR-0012: the symbolic layer is the guaranteed path). No model, no network, no state: the same input gives the same output.

import type { Candidate } from "./cache";

const WORDS: Record<string, readonly string[]> = {
  fsharp: ["let", "let rec", "match", "with", "function", "fun", "type", "module", "namespace", "open", "if", "then", "elif", "else", "for", "while", "do", "yield", "use", "member", "interface", "inherit", "async", "task", "try", "finally", "printfn", "failwith", "Some", "None", "List.map", "Seq.map", "Option.map"],
  csharp: ["using", "namespace", "class", "struct", "record", "interface", "public", "private", "static", "async", "await", "var", "foreach", "return", "switch", "string", "int", "bool", "void", "Console.WriteLine", "throw new"],
  typescript: ["const", "let", "function", "async", "await", "return", "interface", "type", "export", "import", "from", "class", "extends", "implements", "if", "else", "for", "while", "switch", "console.log", "throw new Error"],
  javascript: ["const", "let", "function", "async", "await", "return", "export", "import", "from", "class", "extends", "if", "else", "for", "while", "switch", "console.log", "throw new Error"],
  python: ["def", "class", "import", "from", "return", "if", "elif", "else", "for", "while", "with", "as", "try", "except", "finally", "lambda", "yield", "async", "await", "print", "raise", "None", "True", "False"],
  rust: ["fn", "let", "let mut", "struct", "enum", "impl", "trait", "pub", "use", "mod", "match", "if let", "while let", "for", "loop", "return", "Some", "None", "Ok", "Err", "println!", "unwrap_or", "Vec::new"],
  go: ["func", "package", "import", "var", "const", "type", "struct", "interface", "return", "if", "else", "for", "range", "switch", "defer", "go", "chan", "select", "fmt.Println", "make", "nil"],
  c: ["#include", "#define", "int", "char", "void", "struct", "typedef", "return", "if", "else", "for", "while", "switch", "static", "const", "sizeof", "printf", "NULL"],
  cpp: ["#include", "#define", "namespace", "class", "struct", "template", "typename", "int", "auto", "void", "return", "if", "else", "for", "while", "switch", "std::string", "std::vector", "std::cout", "nullptr"],
  java: ["import", "package", "class", "interface", "public", "private", "static", "void", "int", "String", "return", "if", "else", "for", "while", "switch", "try", "catch", "throw new", "System.out.println"],
};
const ALIASES: Record<string, string> = { "c#": "csharp", ts: "typescript", js: "javascript", "c++": "cpp", py: "python", fs: "fsharp" };

/** The identifier characters directly before the cursor: the word being typed. */
export function currentWord(prefix: string): string {
  const m = /[A-Za-z_#][A-Za-z0-9_#.:!]*$/.exec(prefix);
  return m ? m[0] : "";
}

/** Keywords of the language that continue the word at the cursor, shortest first, at most `max`. Nothing when the word is empty. */
export function staticCandidates(language: string, prefix: string, max = 5): Candidate[] {
  const word = currentWord(prefix);
  if (word === "") return [];
  const list = WORDS[ALIASES[language] ?? language] ?? [];
  const lower = word.toLowerCase();
  return list
    .filter((w) => w.length > word.length && w.toLowerCase().startsWith(lower))
    .sort((a, b) => a.length - b.length || (a < b ? -1 : 1))
    .slice(0, max)
    .map((w) => ({ text: w.slice(word.length), kind: "keyword" as const, confidence: 0.3 }));
}
