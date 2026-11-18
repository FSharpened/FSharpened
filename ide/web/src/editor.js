import { EditorState, Compartment, EditorSelection, countColumn } from "@codemirror/state";
import { EditorView, keymap, lineNumbers, highlightActiveLine, highlightActiveLineGutter, drawSelection, dropCursor, rectangularSelection, crosshairCursor, highlightWhitespace } from "@codemirror/view";
import { defaultKeymap, history, historyKeymap, indentMore, indentLess, toggleComment, toggleBlockComment, copyLineDown, copyLineUp, moveLineDown, moveLineUp, deleteLine, deleteTrailingWhitespace, indentSelection, undo, redo, selectAll } from "@codemirror/commands";
import { StreamLanguage, syntaxHighlighting, HighlightStyle, indentOnInput, bracketMatching, foldGutter, foldKeymap, foldAll, unfoldAll, indentUnit } from "@codemirror/language";
import { closeBrackets, closeBracketsKeymap, autocompletion, completionKeymap, snippet, acceptCompletion } from "@codemirror/autocomplete";
import { searchKeymap, highlightSelectionMatches, openSearchPanel, selectSelectionMatches, selectNextOccurrence, gotoLine } from "@codemirror/search";
import { lintGutter, setDiagnostics, lintKeymap } from "@codemirror/lint";
import { fSharp, oCaml } from "@codemirror/legacy-modes/mode/mllike";
import { c, cpp, csharp, java, dart } from "@codemirror/legacy-modes/mode/clike";
import { rust } from "@codemirror/legacy-modes/mode/rust";
import { python } from "@codemirror/legacy-modes/mode/python";
import { javascript, typescript } from "@codemirror/legacy-modes/mode/javascript";
import { vb } from "@codemirror/legacy-modes/mode/vb";
import { go } from "@codemirror/legacy-modes/mode/go";
import { ruby } from "@codemirror/legacy-modes/mode/ruby";
import { lua } from "@codemirror/legacy-modes/mode/lua";
import { r } from "@codemirror/legacy-modes/mode/r";
import { haskell } from "@codemirror/legacy-modes/mode/haskell";
import { tags as t } from "@lezer/highlight";
import { copyText } from "./transfer.js";

// @codemirror/legacy-modes has no PHP mode, so this is a small one: enough to colour real PHP (tags, $variables,
// strings across lines, three comment styles, numbers, keywords) without pretending to parse it.
const PHP_KEYWORDS = new Set(("abstract and array as break callable case catch class clone const continue declare default do echo else elseif empty " +
  "enddeclare endfor endforeach endif endswitch endwhile enum extends final finally fn for foreach function global goto if implements include " +
  "include_once instanceof insteadof interface isset list match namespace new or print private protected public readonly require require_once " +
  "return static switch throw trait try unset use var while xor yield").split(" "));
const PHP_ATOMS = new Set(["true", "false", "null", "TRUE", "FALSE", "NULL", "self", "parent"]);

const php = {
  name: "php",
  startState: () => ({ block: false, quote: "" }),
  token(stream, state) {
    if (state.block) {
      if (stream.skipTo("*/")) { stream.match("*/"); state.block = false; } else stream.skipToEnd();
      return "comment";
    }
    if (state.quote) {
      while (!stream.eol()) {
        const ch = stream.next();
        if (ch === "\\") stream.next();
        else if (ch === state.quote) { state.quote = ""; break; }
      }
      return "string";
    }
    if (stream.eatSpace()) return null;
    if (stream.match("<?php") || stream.match("<?=") || stream.match("?>")) return "meta";
    if (stream.match("/*")) { state.block = true; return "comment"; }
    if (stream.match("//") || (stream.peek() === "#" && !stream.match("#[", false))) { stream.skipToEnd(); return "comment"; }
    if (stream.match("#[")) return "meta";
    const ch = stream.peek();
    if (ch === "'" || ch === '"') { state.quote = ch; stream.next(); return php.token(stream, state); }
    if (stream.match(/^\$[A-Za-z_\x80-￿][\w\x80-￿]*/)) return "variableName";
    if (stream.match(/^(?:0[xX][0-9a-fA-F_]+|0[bB][01_]+|\d[\d_]*(?:\.\d[\d_]*)?(?:[eE][+-]?\d+)?)/)) return "number";
    if (stream.match(/^[A-Za-z_\x80-￿][\w\x80-￿]*/)) {
      const w = stream.current();
      if (PHP_ATOMS.has(w)) return "atom";
      return PHP_KEYWORDS.has(w.toLowerCase()) ? "keyword" : null;
    }
    stream.next();
    return null;
  },
  languageData: { commentTokens: { line: "//", block: { open: "/*", close: "*/" } } },
};

const modes = {
  fsharp: fSharp, csharp, vb, javascript, typescript, python, rust, dart, c, cpp, go, java, ruby, lua, php, r, haskell, ocaml: oCaml,
};

const highlight = HighlightStyle.define([
  { tag: [t.keyword, t.controlKeyword, t.definitionKeyword, t.modifier, t.operatorKeyword], color: "var(--syn-keyword)" },
  { tag: [t.string, t.special(t.string)], color: "var(--syn-string)" },
  { tag: [t.number, t.bool, t.atom], color: "var(--syn-number)" },
  { tag: [t.comment, t.lineComment, t.blockComment], color: "var(--syn-comment)", fontStyle: "italic" },
  { tag: [t.typeName, t.className, t.namespace], color: "var(--syn-type)" },
  { tag: [t.function(t.variableName), t.function(t.propertyName)], color: "var(--syn-function)" },
  { tag: [t.meta, t.processingInstruction], color: "var(--syn-meta)" },
  { tag: [t.operator, t.punctuation], color: "var(--syn-op)" },
]);

// CodeMirror 6.36 uses Chromium's EditContext API when it exists. With it, Select All then Delete in an empty file throws an
// uncaught "Invalid change range 0 to 1 (in doc of length 0)" from its textupdate handler (found by the browser tests,
// ide/e2e). The classic contenteditable path has no such bug and is what every other browser uses, so use it everywhere.
EditorView.EDIT_CONTEXT = false;

const theme = EditorView.theme({
  "&": { height: "100%", color: "var(--fg)", backgroundColor: "var(--ed-bg)", fontSize: "var(--ed-fs, 14px)" },
  ".cm-scroller": { fontFamily: "ui-monospace, 'Cascadia Code', Consolas, monospace", lineHeight: "1.55" },
  ".cm-content": { caretColor: "var(--brand)" },
  ".cm-gutters": { backgroundColor: "var(--ed-bg)", color: "var(--mut)", border: "none" },
  ".cm-panels": { backgroundColor: "var(--card)", color: "var(--fg)", borderColor: "var(--line)" },
  ".cm-panels input, .cm-panels button": { color: "var(--fg)", backgroundColor: "var(--bg)", border: "1px solid var(--line)", borderRadius: "6px", backgroundImage: "none" },
  ".cm-panels button:hover": { backgroundColor: "var(--code)" },
  ".cm-searchMatch": { backgroundColor: "var(--brand-25)", outline: "1px solid var(--brand)" },
  ".cm-searchMatch.cm-searchMatch-selected": { backgroundColor: "var(--brand-25)", outline: "2px solid var(--brand)" },
  ".cm-selectionMatch": { backgroundColor: "var(--brand-14)" },
  ".cm-matchingBracket, &.cm-focused .cm-matchingBracket": { backgroundColor: "var(--brand-25)", outline: "1px solid var(--brand)" },
  ".cm-highlightSpace": { backgroundImage: "radial-gradient(circle at 50% 55%, var(--mut) 8%, transparent 9%)", backgroundSize: "1ch 100%", backgroundRepeat: "repeat-x" },
  ".cm-highlightTab": { backgroundImage: "linear-gradient(var(--mut), var(--mut))", backgroundSize: "100% 1px", backgroundRepeat: "no-repeat", backgroundPosition: "0 60%" },
  ".cm-snippetField": { backgroundColor: "var(--brand-14)", outline: "1px dotted var(--brand)" },
  ".cm-activeLine": { backgroundColor: "var(--brand-7)" },
  ".cm-activeLineGutter": { backgroundColor: "transparent", color: "var(--fg)" },
  "&.cm-focused": { outline: "none" },
  "&.cm-focused .cm-cursor": { borderLeftColor: "var(--brand)" },
  ".cm-selectionBackground, &.cm-focused .cm-selectionBackground": { backgroundColor: "var(--brand-25)" },
  ".cm-tooltip": { backgroundColor: "var(--card)", color: "var(--fg)", border: "1px solid var(--line)", borderRadius: "8px" },
  ".cm-tooltip-autocomplete ul li[aria-selected]": { backgroundColor: "var(--brand)", color: "var(--brand-fg)" },
  ".cm-diagnostic-error": { borderLeft: "3px solid var(--bad)" },
  ".cm-diagnostic-warning": { borderLeft: "3px solid var(--warn)" },
});

const keywords = {
  fsharp: "let mutable rec and in if then elif else match with when function fun type module namespace open of do for to downto while yield return use new not true false null async task seq list array option Some None Ok Error printfn sprintf failwith List Seq Array Map Set String Option Result".split(" "),
  c: "auto break case char const continue default do double else enum extern float for goto if inline int long register restrict return short signed sizeof static struct switch typedef union unsigned void volatile while printf scanf puts malloc free memcpy strlen NULL stdio stdlib string".split(" "),
  cpp: "alignas auto bool break case catch char class const constexpr continue decltype default delete do double else enum explicit false final float for friend goto if inline int long namespace new noexcept nullptr operator override private protected public return short signed sizeof static struct switch template this throw true try typedef typename union unsigned using virtual void volatile while std vector string map set unordered_map cout cin endl sort size_t unique_ptr shared_ptr make_unique make_shared optional".split(" "),
  rust: "as async await break const continue crate dyn else enum extern false fn for if impl in let loop match mod move mut pub ref return self Self static struct super trait true type unsafe use where while Option Some None Result Ok Err Vec String Box Rc Arc HashMap HashSet BTreeMap println print eprintln format vec assert assert_eq panic todo unreachable derive Debug Clone Copy PartialEq Default Iterator IntoIterator From Into".split(" "),
  csharp: "abstract as async await base bool break byte case catch char checked class const continue decimal default delegate do double else enum event explicit extern false finally fixed float for foreach goto if implicit in int interface internal is lock long namespace new null object operator out override params private protected public readonly ref return sbyte sealed short sizeof static string struct switch this throw true try typeof uint ulong unchecked unsafe ushort using var virtual void volatile while record Console List Dictionary Linq Task".split(" "),
  vb: "AddHandler AndAlso As Boolean ByRef Byte ByVal Case Catch Char Class Const Continue Date Decimal Dim Do Double Each Else ElseIf End Enum Event Exit False Finally For Friend Function Get If Imports In Inherits Integer Interface Is IsNot Let Long Loop Me Module MustInherit MustOverride Namespace New Next Nothing Not NotInheritable Of Or OrElse Overridable Overrides Private Property Protected Public ReadOnly ReDim Return Select Set Shared Short Single String Structure Sub Then Throw To True Try TryCast Using When While With WriteOnly Console WriteLine ReadLine List Dictionary Math Linq".split(" "),
  javascript: "async await break case catch class const continue debugger default delete do else export extends false finally for from function if import in instanceof let new null of return static super switch this throw true try typeof undefined var void while with yield console log map filter reduce forEach Promise Array Object JSON Math Map Set Symbol Number String Date require module process".split(" "),
  typescript: "abstract any as async await boolean break case catch class const constructor continue declare default delete do else enum export extends false finally for from function get if implements import in infer instanceof interface is keyof let namespace never new null number object of private protected public readonly return satisfies set static string super switch symbol this throw true try type typeof undefined unknown var void while yield console log Array Record Partial Readonly Promise Map Set Pick Omit".split(" "),
  python: "False None True and as assert async await break class continue def del elif else except finally for from global if import in is lambda match case nonlocal not or pass raise return try while with yield print len range enumerate zip map filter sorted sum min max isinstance dict list set tuple str int float bool open self dataclass field".split(" "),
  dart: "abstract as assert async await base break case catch class const continue covariant default deferred do dynamic else enum export extends extension external factory false final finally for Function get hide if implements import in interface is late library mixin new null of on operator part required rethrow return sealed set show static super switch sync this throw true try typedef var void when while with yield print int double num String bool List Map Set Future Stream Iterable Object Duration".split(" "),
  go: "break case chan const continue default defer else fallthrough for func go goto if import interface map package range return select struct switch type var any bool byte error false float64 int int64 iota make new nil rune string true uint append cap close copy delete len panic print println recover fmt Println Printf Sprintf Errorf strings strconv sort sync time".split(" "),
  java: "abstract assert boolean break byte case catch char class const continue default do double else enum extends final finally float for if implements import instanceof int interface long native new null package permits private protected public record return sealed short static super switch synchronized this throw throws transient true false try var void volatile while yield String System out println printf List Map Set ArrayList HashMap Optional Stream Arrays Collections Math Integer Object".split(" "),
  ruby: "BEGIN END alias and begin break case class def defined? do else elsif end ensure false for if in module next nil not or redo rescue retry return self super then true undef unless until when while yield puts print p require require_relative attr_reader attr_writer attr_accessor lambda proc each map select reject reduce inject Struct Hash Array String Integer Float Comparable Enumerable".split(" "),
  lua: "and break do else elseif end false for function goto if in local nil not or repeat return then true until while print pairs ipairs type tostring tonumber setmetatable getmetatable require pcall error assert select string table math os io coroutine insert remove concat format sort unpack".split(" "),
  php: "abstract and array as break callable case catch class clone const continue declare default do echo else elseif empty enum extends final finally fn for foreach function global if implements include interface isset list match namespace new null or print private protected public readonly require return static switch throw trait true false try unset use var while yield strlen count array_map array_filter array_reduce implode explode sprintf printf str_repeat json_encode var_dump in_array array_keys array_values sort usort PHP_EOL".split(" "),
  r: "if else repeat while function for next break TRUE FALSE NULL NA NA_integer_ NA_real_ NA_character_ Inf NaN in library require return print cat paste paste0 c list vector matrix data.frame length seq rep sum mean median sd var min max range sapply lapply vapply Map Filter Reduce apply tapply summary str head tail nrow ncol names is.na ifelse stop warning tryCatch lm predict coef plot".split(" "),
  haskell: "case class data default deriving do else forall foreign if import in infix infixl infixr instance let module newtype of then type where main putStrLn putStr print show read map filter foldr foldl sum length reverse zip zipWith take drop head tail lines unwords unlines Maybe Just Nothing Either Left Right IO Int Integer Double Bool True False String Char Show Eq Ord Num Functor Monad return pure fmap".split(" "),
  ocaml: "and as assert begin class constraint do done downto else end exception external false for fun function functor if in include inherit initializer lazy let match method module mutable new nonrec object of open or private rec sig struct then to true try type val virtual when while with List Array String Printf printf sprintf Hashtbl Option Result Some None Ok Error print_endline print_string string_of_int failwith ref ignore fst snd".split(" "),
};

const WORD = /[A-Za-z_][A-Za-z0-9_]*/;
const IDENT = /^[A-Za-z_]\w*$/;

/** Completion: the language's keywords plus the snippets whose prefix is a word (built-in and imported). */
function complete(lang, host) {
  const words = (keywords[lang] ?? []).map((label) => ({ label, type: "keyword" }));
  return (ctx) => {
    const w = ctx.matchBefore(WORD);
    if (!w || (w.from === w.to && !ctx.explicit)) return null;
    const snips = host.snippets(lang)
      .filter((s) => IDENT.test(s.label))
      .map((s) => ({ label: s.label, detail: s.detail, type: "snippet", boost: 1, apply: (view, _c, from, to) => insertBody(view, host, s.body, from, to) }));
    return { from: w.from, options: [...snips, ...words], validFor: /^[A-Za-z0-9_]*$/ };
  };
}

/** Expand a VS Code snippet body at [from, to) as a CodeMirror template, with tab stops and placeholders. */
function insertBody(view, host, body, from, to) {
  const selected = view.state.sliceDoc(view.state.selection.main.from, view.state.selection.main.to);
  const made = host.snippetTemplate(body, { ...host.snippetContext(), selected });
  snippet(made.template)(view, null, from, to);
}

const mac = typeof navigator !== "undefined" && /Mac|iPhone|iPad/.test(navigator.platform ?? "");

/** The editor options the settings control. Rebuilt and swapped in when a setting changes. */
function settingsExtensions(s) {
  return [
    EditorState.tabSize.of(s.tabSize),
    indentUnit.of(s.insertSpaces ? " ".repeat(s.tabSize) : "\t"),
    s.wordWrap ? EditorView.lineWrapping : [],
    s.lineNumbers ? [lineNumbers(), highlightActiveLineGutter()] : [],
    s.renderWhitespace ? highlightWhitespace() : [],
    s.autoCloseBrackets ? [closeBrackets(), keymap.of(closeBracketsKeymap)] : [],
  ];
}

const DEFAULTS = { tabSize: 4, insertSpaces: true, wordWrap: false, lineNumbers: true, renderWhitespace: false, autoCloseBrackets: true };

function cursorInfo(state, s) {
  const main = state.selection.main;
  const line = state.doc.lineAt(main.head);
  const col = countColumn(state.sliceDoc(line.from, main.head), s.tabSize) + 1;
  let selected = 0;
  for (const r of state.selection.ranges) selected += r.to - r.from;
  const multi = state.selection.ranges.length;
  const lines = new Set();
  if (selected) for (const r of state.selection.ranges) for (let n = state.doc.lineAt(r.from).number; n <= state.doc.lineAt(r.to).number; n++) lines.add(n);
  return { line: line.number, column: col, cursors: multi, selected, selectedLines: lines.size, lines: state.doc.lines, eol: state.lineBreak === "\r\n" ? "CRLF" : "LF" };
}

/** What Copy takes: the selections joined by line breaks, or the caret's whole line when nothing is selected. */
function clipText(state) {
  const filled = state.selection.ranges.filter((r) => !r.empty);
  if (filled.length) return filled.map((r) => state.sliceDoc(r.from, r.to)).join(state.lineBreak);
  const l = state.doc.lineAt(state.selection.main.head);
  return state.sliceDoc(l.from, l.to) + state.lineBreak;
}

/** Map every selection range through `fn` (a word at the cursor when nothing is selected). */
function mapText(fn) {
  return (v) => {
    const { state } = v;
    const spec = state.changeByRange((r) => {
      const range = r.empty ? state.wordAt(r.head) : r;
      if (!range) return { range: r };
      const text = state.sliceDoc(range.from, range.to);
      const next = fn(text);
      if (next === text) return { range: r };
      const keep = r.empty ? EditorSelection.cursor(Math.min(r.head, range.from + next.length)) : EditorSelection.range(range.from, range.from + next.length);
      return { changes: { from: range.from, to: range.to, insert: next }, range: keep };
    });
    if (spec.changes.empty) return true;
    v.dispatch(state.update(spec, { userEvent: "input" }));
    return true;
  };
}

/** Rewrite the selected lines, or every line when nothing is selected. */
function mapLines(fn) {
  return (v) => {
    const { state } = v;
    const sel = state.selection.main;
    let from = 0, to = state.doc.length;
    if (!sel.empty) {
      from = state.doc.lineAt(sel.from).from;
      to = state.doc.lineAt(sel.to).from === sel.to && sel.to > sel.from ? state.doc.lineAt(sel.to - 1).to : state.doc.lineAt(sel.to).to;
    }
    const text = state.sliceDoc(from, to);
    const out = fn(text.split(state.lineBreak)).join(state.lineBreak);
    if (out !== text) v.dispatch({ changes: { from, to, insert: out }, userEvent: "input" });
    return true;
  };
}

function joinLines(v) {
  const { state } = v;
  const sel = state.selection.main;
  const first = state.doc.lineAt(sel.from);
  const lastNo = Math.min(state.doc.lines, Math.max(state.doc.lineAt(sel.to).number, first.number + 1));
  if (lastNo === first.number) return true;
  const last = state.doc.line(lastNo);
  const parts = [];
  for (let n = first.number; n <= lastNo; n++) parts.push(n === first.number ? state.doc.line(n).text.replace(/\s+$/, "") : state.doc.line(n).text.trim());
  v.dispatch({ changes: { from: first.from, to: last.to, insert: parts.filter((p, i) => i === 0 || p).join(" ") }, userEvent: "delete" });
  return true;
}

function addCursor(down) {
  return (v) => {
    const ranges = v.state.selection.ranges;
    const added = ranges.map((r) => v.moveVertically(EditorSelection.cursor(r.head), down));
    const all = [...ranges, ...added];
    v.dispatch({ selection: EditorSelection.create(all, all.length - 1), scrollIntoView: true, userEvent: "select" });
    return true;
  };
}

/** Change leading indentation of every line between tabs and spaces. */
function reindent(toSpaces, size) {
  return (v) => {
    const { state } = v;
    const changes = [];
    for (let n = 1; n <= state.doc.lines; n++) {
      const line = state.doc.line(n);
      const m = /^[ \t]+/.exec(line.text);
      if (!m) continue;
      const width = countColumn(m[0], size);
      const want = toSpaces ? " ".repeat(width) : "\t".repeat(Math.floor(width / size)) + " ".repeat(width % size);
      if (want !== m[0]) changes.push({ from: line.from, to: line.from + m[0].length, insert: want });
    }
    if (changes.length) v.dispatch({ changes, userEvent: "input" });
    return true;
  };
}

export function createEditor(parent, { onChange, onRun, onCursor = () => {}, snippets = () => [], snippetTemplate = (b) => ({ template: b, warnings: [] }), snippetContext = () => ({}) }) {
  const host = { snippets, snippetTemplate, snippetContext };
  // One editor state per file path, so every open file keeps its own undo history, selection and folding.
  const states = new Map();   // path -> EditorState
  const langOf = new Map();   // path -> language id ("" for text and data files)
  const versionOf = new Map();// path -> the settings version its state was made with
  const label = new Compartment();
  const prefs = new Compartment();
  let cfg = { ...DEFAULTS };
  let version = 0;
  // tabindex 0 on the (already focusable) content tells scanners that the scrolling editor can be reached by keyboard
  const labelOf = (name) => label.of(EditorView.contentAttributes.of({ "aria-label": (name || "No file") + " source code editor", tabindex: "0" }));
  const makeState = (lang, doc, name) => {
    versionOf.set(name, version);
    return EditorState.create({
      doc,
      extensions: [
        // the file's own line endings are kept: a CRLF file stays CRLF when it is edited
        doc.includes("\r\n") ? EditorState.lineSeparator.of("\r\n") : [],
        EditorState.allowMultipleSelections.of(true),
        prefs.of(settingsExtensions(cfg)),
        foldGutter(), history(), drawSelection(), dropCursor(), indentOnInput(), bracketMatching(),
        autocompletion({ override: [complete(lang, host)] }), highlightActiveLine(), highlightSelectionMatches(), lintGutter(),
        rectangularSelection({ eventFilter: (e) => e.altKey && e.shiftKey && e.button === 0 }), crosshairCursor(),
        EditorView.clickAddsSelectionRange.of((e) => e.altKey || (mac ? e.metaKey : e.ctrlKey)),
        modes[lang] ? StreamLanguage.define(modes[lang]) : [], syntaxHighlighting(highlight), theme,
        // Tab indents, but Escape releases it so keyboard users can leave the editor (WCAG 2.1.2).
        keymap.of([{ key: "Escape", run: () => { tabTraps = false; return false; } }, { key: "Tab", run: (v) => acceptCompletion(v) || (tabTraps ? indentMore(v) : false), shift: (v) => (tabTraps ? indentLess(v) : false) },
          { key: "Mod-Enter", run: () => { onRun(); return true; } },
          { key: "Mod-h", run: openSearchPanel, preventDefault: true },
          { key: "Mod-Alt-ArrowUp", run: addCursor(false) }, { key: "Mod-Alt-ArrowDown", run: addCursor(true) },
          ...defaultKeymap, ...searchKeymap, ...historyKeymap, ...foldKeymap, ...completionKeymap, ...lintKeymap]),
        EditorView.domEventHandlers({ keydown: (e) => { if (e.key !== "Escape" && e.key !== "Tab") tabTraps = true; return false; }, focus: () => { tabTraps = true; return false; } }),
        EditorView.updateListener.of((u) => {
          if (u.docChanged) onChange(u.state.sliceDoc());
          if (u.docChanged || u.selectionSet) onCursor(cursorInfo(u.state, cfg));
        }),
        labelOf(name),
      ],
    });
  };
  const view = new EditorView({ parent, state: makeState("", "", "") });
  let current = "";
  const report = () => onCursor(cursorInfo(view.state, cfg));
  const reconfigure = (st) => st.update({ effects: prefs.reconfigure(settingsExtensions(cfg)) }).state;
  const put = (path, st) => { states.set(path, st); view.setState(st); report(); };

  const commands = {
    commentLine: toggleComment, commentBlock: toggleBlockComment, foldAll, unfoldAll,
    selectAllMatches: selectSelectionMatches, addNextMatch: selectNextOccurrence,
    cursorAbove: addCursor(false), cursorBelow: addCursor(true),
    duplicateDown: copyLineDown, duplicateUp: copyLineUp, moveDown: moveLineDown, moveUp: moveLineUp, deleteLine,
    trimTrailing: deleteTrailingWhitespace, reindent: indentSelection, undo, redo, selectAll,
    find: openSearchPanel, gotoLine,
    sortAscending: mapLines((ls) => ls.slice().sort((a, b) => a.localeCompare(b))),
    sortDescending: mapLines((ls) => ls.slice().sort((a, b) => b.localeCompare(a))),
    uniqueLines: mapLines((ls) => ls.filter((l, i) => ls.indexOf(l) === i)),
    upper: mapText((s) => s.toUpperCase()), lower: mapText((s) => s.toLowerCase()),
    joinLines,
    toSpaces: (v) => reindent(true, cfg.tabSize)(v), toTabs: (v) => reindent(false, cfg.tabSize)(v),
  };

  return {
    /** Switch to a file. The first time a path is shown its state is made from `doc`; after that its own history is kept. */
    show(path, lang, doc) {
      states.set(current, view.state);
      current = path;
      if (!states.has(path) || langOf.get(path) !== lang) { states.set(path, makeState(lang, doc, path)); langOf.set(path, lang); }
      let st = states.get(path);
      if (versionOf.get(path) !== version) { st = reconfigure(st); versionOf.set(path, version); }
      put(path, st);
    },
    /** Replace a file's whole text from outside the editor (a template, an import). Its undo history starts over. */
    setText(path, lang, doc) {
      states.delete(path);
      langOf.delete(path);
      versionOf.delete(path);
      if (path === current) { langOf.set(path, lang); put(path, makeState(lang, doc, path)); }
    },
    /** A file was deleted. If it was on screen, the caller shows another one next. */
    forget(path) {
      states.delete(path);
      langOf.delete(path);
      versionOf.delete(path);
      if (path === current) current = "";
    },
    /** A file was renamed or moved. It keeps its history when the language stays the same. */
    move(from, to, lang, doc) {
      const live = from === current;
      const st = live ? view.state : states.get(from);
      const same = !!st && langOf.get(from) === lang;
      const was = live ? version : versionOf.get(from);
      states.delete(from);
      langOf.delete(from);
      versionOf.delete(from);
      states.delete(to);
      langOf.delete(to);
      versionOf.delete(to);
      if (live) current = to;
      if (!same) {
        if (live) { langOf.set(to, lang); put(to, makeState(lang, doc, to)); }
        return;
      }
      langOf.set(to, lang);
      versionOf.set(to, was);
      const effects = label.reconfigure(EditorView.contentAttributes.of({ "aria-label": to + " source code editor", tabindex: "0" }));
      if (live) { view.dispatch({ effects }); states.set(to, view.state); }
      else states.set(to, st.update({ effects }).state);
    },
    /** Forget every file (another project was opened, or a statepoint was restored). */
    reset() {
      states.clear();
      langOf.clear();
      versionOf.clear();
      current = "";
      put("", makeState("", "", ""));
    },
    /** New editor settings: they reach the open file now and every other file the next time it is shown. */
    configure(settings) {
      cfg = { ...DEFAULTS, ...settings };
      version++;
      view.dispatch({ effects: prefs.reconfigure(settingsExtensions(cfg)) });
      versionOf.set(current, version);
      report();
    },
    text: () => view.state.sliceDoc(),
    focus: () => view.focus(),
    hasFocus: () => view.hasFocus,
    selectionText: () => view.state.sliceDoc(view.state.selection.main.from, view.state.selection.main.to),
    /** The identifier under the caret, with its position, for "go to definition". */
    wordAtCaret() {
      const r = view.state.wordAt(view.state.selection.main.head);
      return r ? { text: view.state.sliceDoc(r.from, r.to), from: r.from, to: r.to } : null;
    },
    /** The caret's line and column, 1-based. */
    caret: () => { const i = cursorInfo(view.state, cfg); return { line: i.line, column: i.column }; },
    /** diagnostics: [{line,column,endLine,endColumn,severity,message}] */
    mark(diags) {
      const doc = view.state.doc;
      const list = diags.filter((d) => d.line > 0 && d.line <= doc.lines).map((d) => {
        const ln = doc.line(d.line);
        const from = Math.min(ln.to, ln.from + Math.max(0, d.column - 1));
        const el = d.endLine > 0 && d.endLine <= doc.lines ? doc.line(d.endLine) : ln;
        const to = Math.max(from + 1, Math.min(doc.length, el.from + Math.max(0, d.endColumn - 1)));
        return { from, to: Math.min(to, doc.length), severity: d.severity === "info" ? "info" : d.severity, message: d.message };
      });
      view.dispatch(setDiagnostics(view.state, list));
    },
    /** Move the caret to a line and column (1-based), selecting `length` characters from there when given. */
    reveal(line, column, length = 0) {
      const doc = view.state.doc;
      if (line < 1 || line > doc.lines) return;
      const ln = doc.line(line);
      const pos = Math.min(ln.to, ln.from + Math.max(0, column - 1));
      view.dispatch({ selection: length > 0 ? EditorSelection.range(pos, Math.min(ln.to, pos + length)) : { anchor: pos }, effects: EditorView.scrollIntoView(pos, { y: "center" }) });
      view.focus();
    },
    /** Before a context menu: a right-click outside the selection puts the caret there, as VS Code does. */
    prepareContext(x, y) {
      const pos = x == null ? null : view.posAtCoords({ x, y });
      if (pos != null && !view.state.selection.ranges.some((r) => !r.empty && pos >= r.from && pos <= r.to)) view.dispatch({ selection: { anchor: pos } });
      const sel = view.state.selection.main;
      const w = sel.empty ? view.state.wordAt(sel.head) : null;
      return { hasSelection: view.state.selection.ranges.some((r) => !r.empty), word: w ? view.state.sliceDoc(w.from, w.to) : "" };
    },
    /** Where the caret is on screen, for a menu opened from the keyboard. */
    caretPoint() {
      const c = view.coordsAtPos(view.state.selection.main.head);
      return c ? { x: c.left, y: c.bottom } : null;
    },
    /** Copy (or cut) the selection, or the caret's line. Resolves false when the browser refused the clipboard. */
    async copy(cut = false) {
      const ok = await copyText(clipText(view.state));
      if (ok && cut) { if (view.state.selection.ranges.some((r) => !r.empty)) view.dispatch(view.state.replaceSelection(""), { userEvent: "delete.cut" }); else deleteLine(view); }
      view.focus();
      return ok;
    },
    /** Paste from the clipboard. Browsers may refuse to let a page read it; then this resolves false. */
    async paste() {
      let text;
      try { text = await navigator.clipboard.readText(); } catch { return false; }
      view.focus();
      view.dispatch({ ...view.state.replaceSelection(text), scrollIntoView: true, userEvent: "input.paste" });
      return true;
    },
    /** Put a VS Code snippet at the caret, replacing the selection. */
    insertSnippet(body) {
      const r = view.state.selection.main;
      insertBody(view, host, body, r.from, r.to);
      view.focus();
    },
    /** Run an editor command by name; false when there is no such command. */
    run(name) {
      const fn = commands[name];
      if (!fn) return false;
      fn(view);
      view.focus();
      return true;
    },
    commandNames: () => Object.keys(commands),
  };
}
