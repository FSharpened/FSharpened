import { EditorState } from "@codemirror/state";
import { EditorView, keymap, lineNumbers, highlightActiveLine, highlightActiveLineGutter, drawSelection, dropCursor } from "@codemirror/view";
import { defaultKeymap, history, historyKeymap, indentMore, indentLess } from "@codemirror/commands";
import { StreamLanguage, syntaxHighlighting, HighlightStyle, indentOnInput, bracketMatching, foldGutter, foldKeymap } from "@codemirror/language";
import { closeBrackets, closeBracketsKeymap, autocompletion, completionKeymap } from "@codemirror/autocomplete";
import { searchKeymap, highlightSelectionMatches } from "@codemirror/search";
import { lintGutter, setDiagnostics, lintKeymap } from "@codemirror/lint";
import { fSharp } from "@codemirror/legacy-modes/mode/mllike";
import { c, cpp, csharp } from "@codemirror/legacy-modes/mode/clike";
import { tags as t } from "@lezer/highlight";

const modes = { fsharp: fSharp, c, cpp, csharp };

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

const theme = EditorView.theme({
  "&": { height: "100%", color: "var(--fg)", backgroundColor: "var(--ed-bg)", fontSize: "14px" },
  ".cm-scroller": { fontFamily: "ui-monospace, 'Cascadia Code', Consolas, monospace", lineHeight: "1.55" },
  ".cm-content": { caretColor: "var(--brand)" },
  ".cm-gutters": { backgroundColor: "var(--ed-bg)", color: "var(--mut)", border: "none" },
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
  csharp: "abstract as async await base bool break byte case catch char checked class const continue decimal default delegate do double else enum event explicit extern false finally fixed float for foreach goto if implicit in int interface internal is lock long namespace new null object operator out override params private protected public readonly ref return sbyte sealed short sizeof static string struct switch this throw true try typeof uint ulong unchecked unsafe ushort using var virtual void volatile while record Console List Dictionary Linq Task".split(" "),
};

function complete(lang) {
  return (ctx) => {
    const w = ctx.matchBefore(/[A-Za-z_][A-Za-z0-9_]*/);
    if (!w || (w.from === w.to && !ctx.explicit)) return null;
    return { from: w.from, options: keywords[lang].map((label) => ({ label, type: "keyword" })), validFor: /^[A-Za-z0-9_]*$/ };
  };
}

let tabTraps = true;

export function createEditor(parent, { onChange, onRun }) {
  const states = {};
  const makeState = (lang, doc) => EditorState.create({
    doc,
    extensions: [
      lineNumbers(), highlightActiveLineGutter(), foldGutter(), history(), drawSelection(), dropCursor(), indentOnInput(), bracketMatching(),
      closeBrackets(), autocompletion({ override: [complete(lang)] }), highlightActiveLine(), highlightSelectionMatches(), lintGutter(),
      StreamLanguage.define(modes[lang]), syntaxHighlighting(highlight), theme, EditorState.tabSize.of(4),
      // Tab indents, but Escape releases it so keyboard users can leave the editor (WCAG 2.1.2).
      keymap.of([{ key: "Escape", run: () => { tabTraps = false; return false; } }, { key: "Tab", run: (v) => (tabTraps ? indentMore(v) : false), shift: (v) => (tabTraps ? indentLess(v) : false) },
        { key: "Mod-Enter", run: () => { onRun(); return true; } }, ...closeBracketsKeymap, ...defaultKeymap, ...searchKeymap, ...historyKeymap, ...foldKeymap, ...completionKeymap, ...lintKeymap]),
      EditorView.domEventHandlers({ keydown: (e) => { if (e.key !== "Escape" && e.key !== "Tab") tabTraps = true; return false; }, focus: () => { tabTraps = true; return false; } }),
      EditorView.updateListener.of((u) => { if (u.docChanged) onChange(u.state.doc.toString()); }),
      EditorView.contentAttributes.of({ "aria-label": lang + " source code editor" }),
    ],
  });
  const view = new EditorView({ parent, state: makeState("fsharp", "") });
  let current = "fsharp";
  return {
    /** Switch language; each language keeps its own undo history. */
    show(lang, doc) {
      states[current] = view.state;
      current = lang;
      view.setState(states[lang] ?? makeState(lang, doc));
    },
    setText(lang, doc) {
      delete states[lang];
      if (lang === current) view.setState(makeState(lang, doc));
    },
    text: () => view.state.doc.toString(),
    focus: () => view.focus(),
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
    reveal(line, column) {
      const doc = view.state.doc;
      if (line < 1 || line > doc.lines) return;
      const pos = Math.min(doc.line(line).to, doc.line(line).from + Math.max(0, column - 1));
      view.dispatch({ selection: { anchor: pos }, scrollIntoView: true });
      view.focus();
    },
  };
}
