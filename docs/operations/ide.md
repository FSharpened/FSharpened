# Browser IDE operations (ADR-0018, ADR-0019, ADR-0020, ADR-0021)

Live: https://fsharpened.pages.dev/ide/ (gateway: https://ide.fsharpened.workers.dev).


## Languages
Eighteen, in five groups. A file's language comes from its extension, so one project can mix them; the Compiler, Flags and Examples controls follow the open file. Every language really compiles or interprets and runs; none is simulated.

| Group | Language | Backend | Compilers (first is the default) |
|---|---|---|---|
| .NET | F#, C#, VB.NET | Compiler Explorer | .NET 9, then .NET 8 |
| Fable targets | JavaScript | Wandbox | Node.js 20, 18 |
| | TypeScript | Wandbox | `tsc` 5.6 (type-checks, then runs on Node.js 20) |
| | Python | Compiler Explorer | 3.14, 3.13, 3.12 |
| | Rust | Compiler Explorer | 1.99, 1.96, 1.90 (see [Rust](rust.md)) |
| | Dart | Compiler Explorer | 3.13, 3.12, 3.11 |
| Systems | C, C++ | Compiler Explorer | GCC 14.2, Clang 19.1, GCC 13.2 |
| | Go | Compiler Explorer | 1.26, 1.25, 1.24 |
| | Java | Compiler Explorer | JDK 27, 25, 21 |
| Scripting and data | Ruby | Compiler Explorer | 4.0, 3.4, 3.3 |
| | Lua | Compiler Explorer | 5.5, 5.4, 5.3 |
| | PHP | Wandbox | 8.3, 8.2 |
| | R | Wandbox | 4.4 |
| Functional | Haskell | Compiler Explorer | GHC 9.12, 9.10, 9.8 |
| | OCaml | Compiler Explorer | 5.5, 5.4, 5.3 |

45 compilers in all. Only C, C++ and Rust take compiler flags; the Flags field is hidden for the rest and the gateway drops any flags sent for them. Each language has one or more starter programs (34 in all, loaded from the Examples list).

**Why not Kotlin, Julia, Scala, Swift, Zig, Erlang or Elixir.** Kotlin and Julia are on Compiler Explorer, but their error paths are unusable there (Kotlin reports "Assertion failed" for any compile error; Julia is killed for exceeding the time limit on a trivial exception), so a mistake would give no readable message. The others exist on Compiler Explorer or Wandbox and are not in because nobody has asked yet; the procedure below adds one in under an hour. Details in [ADR-0020](../adr/0020-ide-polyglot-languages.md).

## Pieces
| Part | Path | Role |
|---|---|---|
| F# core | `tools/FSharpened.Ide`, tests in `tests/FSharpened.Ide.Tests` | language table, project model, whole-project timeline, diagnostics parsing, protocol, fallback chain, starter templates |
| Fable entry | `tools/FSharpened.Ide.Web` | exports the core to JavaScript |
| UI | `ide/web` | CodeMirror 6 shell, bundled to `site/dist/ide.js` (generated, git-ignored); the site build fingerprints it into `site/public/assets/h/`. `ide/web/test` runs the compiled core against captured compiler output, and the project store and the ZIP codec against fakes |
| Page | `tools/site/build.py` | generates `site/public/ide/index.html`; `site/src/ide.css` is hand-written (see `docs/operations/site.md`) |
| Gateway | `gateway/` | Worker: validates, rate-limits and proxies to Compiler Explorer or Wandbox, and normalises both to one result shape |

## Projects (ADR-0021)
A workspace is a **project**: files in folders, any mix of the eighteen languages, one **entry** file, and an ordered **pipeline** (stored, not yet run; see PLAN P6.A5). The rules live in the F# core (`Project.fs`): paths are normalised (forward slashes, no `..`, no absolute paths, no Windows-reserved names, at most 8 deep), a project has at most 200 files, 65,536 characters per file (the gateway's own source limit) and 2,000,000 in all. The page reads the limits from `Core.projectLimits()`.

**The page.** A sidebar with two tabs. *Files*: the project's name, the tree (folders first; the entry file is marked; each file shows its language) and a toolbar: New file (folders in the path are created as needed; there are no empty folders), Rename (also moves a file or a whole folder; the entry and pipeline follow), Delete, Set entry, Copy (the file's text), Download (a file, or a selected folder as a `.zip`). *Projects*: the saved projects, New project, Examples (a project with one starter program per language), Duplicate, Rename, Delete (asks first, cannot be undone), Download .zip, Import .zip, Import folder, Add files. Dropping a `.zip`, a folder or files anywhere on the editor imports them too (a `.zip` or a folder becomes a new project; loose files are added to the open project and replace a file of the same name). Narrow windows turn the sidebar into a drawer that starts closed; wider ones keep it open (Ctrl+B or the Files button toggles it). The tree is a WAI-ARIA tree: arrow keys move, Right and Left unfold and fold, Enter opens, Home and End jump. Open files are tabs; Delete or the x closes one.

**Run** starts the open file when it is a source file, otherwise the project's entry file (the first conventional or runnable file when none was chosen). The backends take one source in one language, so a run is one file; files do not import each other across the network sandbox yet (that is the pipeline, P6.A5). The summary names the file that ran.

**Statepoints** capture the whole project (every file, the entry, the pipeline, the open file and the input) through `FSharpened.Statepoints` (`ProjectTimeline`). A statepoint is made on Run and Snapshot (Ctrl+S) and before a delete, a rename, loading an example or adding files, unless nothing changed since the latest one. Restore, Fork and Diff work on the whole project; Diff lists the files that were added, removed or changed and shows the line diff of one. History is in memory for the life of the page; the project itself is saved.

**Storage.** `localStorage` keys: `fsharpened.ide.v2` (the index: current project and order) and `fsharpened.ide.v2.p.<id>` (one record per project: files, entry, pipeline, open tabs, folded folders, input, compiler and flag choices), so one large project cannot stop the others from saving. Typing saves after a 300 ms pause; leaving the page saves at once. If the browser refuses (storage full or blocked) the page says so and the project stays usable in memory; Download .zip is the way out. Records are validated when read: a damaged one is repaired or dropped, never trusted. `fsharpened.ide.v1` (the single-language workspace of earlier versions) is read once when there is no v2 data, becomes the project "My workspace" (its programs, input and compiler choices), and is never rewritten or deleted.

**Export and import.** *Download .zip* writes the files inside a folder named after the project plus `fsharpened.project.json` (format `fsharpened.project/1`: name, entry, pipeline), stored (no compression). Import reads stored and deflated entries (a real archive from another tool is a test fixture), refuses encrypted, zip64 and unknown-method entries with a reason, checks each CRC, and enforces limits against zip bombs (2000 entries, 5 MB per entry, 24 MB in all, a compression ratio of 200). Binary files, paths that escape the project, reserved names and files over a limit are left out, each with its reason, and the report is shown in the Projects tab. A single top-level wrapper folder is dropped, and the manifest is applied when present.

**Share links** carry the whole project in the URL fragment (`#s=`, deflate then base64url), which is never sent to a server. Opening one makes a **new project** and clears the fragment, so your own projects are untouched and a reload does not import it twice (the same link opens the project it made before). Links from before projects (one program) still open, as a project named "Shared program". Large projects make long links; the page warns past about 12,000 characters.

**Not yet:** pipelines and their runner, import from a GitHub URL, empty folders, drag-and-drop moves in the tree, a diff of binary or non-text data (the IDE holds text only).

## Workbench: VS Code-style features (ADR-0022, Proposed)
The page has the editing features people expect from VS Code and other IDEs. The logic that can live in F# does (fuzzy ranking, search and replace, symbols, snippet templates, themes, settings; `Core.*` in `tools/FSharpened.Ide`, tested there); the DOM wiring is in `ide/web/src` (`workbench.js`, `commands.js`, `palette.js`, `contextmenu.js`, `menus.js`, `searchpanel.js`, `outline.js`, `settings.js`, `extpanel.js`, `vsix.js`).

| Feature | How to reach it |
|---|---|
| Command palette (about 100 commands, most recent first) | F1, Ctrl/⌘+Shift+P, or the Commands button |
| Quick open a file; `>` commands, `@` symbols, `:` line | Ctrl/⌘+P |
| Go to symbol, line and column, definition (F12 finds declarations of the word under the caret in any file of the project, by name; it is a text scan, not a language server, so it knows nothing about types or libraries) | Ctrl/⌘+Shift+O, Ctrl/⌘+G, F12 |
| Search and replace in the project (case, whole word, regex, include and exclude globs, replace one, one file or all, with a confirmation) | Ctrl/⌘+Shift+F, the Search tab |
| Outline of the open file and breadcrumbs | the Outline tab, the bar above the editor |
| Several cursors: Ctrl/⌘+D, select all matches, Alt+click, Alt+Shift+drag for a column, add a cursor above or below | Edit menu, shortcuts |
| Line commands: move, duplicate, delete, join, sort, unique, case, trim trailing space, re-indent, convert indentation, toggle line and block comment, fold all or none | commands, shortcuts |
| Snippets for every language, tab stops and placeholders | typing a prefix, or Insert Snippet |
| Settings (theme, font size, tab size, spaces or tabs, word wrap, line numbers, whitespace, bracket closing, breadcrumbs) | Ctrl/⌘+, |
| Status bar: line and column, selection and cursor count, indentation, language, line ending, encoding | click an item to change it |
| Right-click menus in the editor, file tree, tabs, project list and output | right-click, Menu key or Shift+F10; Shift+right-click keeps the browser's own menu |

**Right-click menus** are WAI-ARIA menus (arrow keys, Home and End, type-ahead, Right and Left for submenus, Enter, Escape; focus returns to where it was). They only offer what applies: Close to the Right is disabled on the last tab, Open is disabled for the open project, Delete is marked as dangerous. The pick lists (themes, indentation, line ending, snippets, projects) end with the caret back in the editor.

**Themes.** Built in: Match the site (follows its light or dark setting), FSharpened Light and Dark, Light+, Dark+, Monokai, Dracula, and High Contrast Dark and Light. A theme is a complete set of colour roles; the contrast of its text against its background is computed and shown (`Core.themeContrast`).

**What comes from VS Code, and what cannot.** Everything below is read as *data*; no extension code is ever run.

| From VS Code | Result |
|---|---|
| `.vsix` (a zip) | its colour themes (up to 12, `include` chains up to 3 deep) and snippets are read, `%key%` titles resolved from `package.nls.json`; what it contributes that is left out (commands, language servers, debuggers, webviews, code) is listed |
| theme `.json` (comments and trailing commas allowed) | a colour theme |
| `*.code-snippets`, `snippets/*.json` | snippets, mapped to this IDE's languages (the rest are reported as unused) |
| `settings.json` | the settings this IDE has (`editor.tabSize`, `editor.fontSize`, `editor.wordWrap`, `workbench.colorTheme`, and so on); the rest are listed as not used |

Limits (`settings.js`, `vsix.js`): at most 40 imported themes, 2,000 snippets and 30 packs; a pack over a limit is refused whole, with the reason. A `.vsix` may hold up to 20,000 entries, 2 MB per entry and 24 MB in all; only small JSON files are inflated. Imported packs are saved in `localStorage` (`fsharpened.ide.packs.v1`) and re-validated on every load (ids, colour values, completeness), so a damaged or edited record is dropped, not trusted. Settings are `fsharpened.ide.settings.v1`; recently used commands `fsharpened.ide.recent.v1`.

**GitHub Copilot and other extensions that run code cannot run in this page.** A VS Code extension is a program written against VS Code's own API and runs in its extension host. Copilot is GitHub's proprietary extension, needs GitHub sign-in, and its suggestions come from GitHub's service. The page also refuses to run code it did not ship (CSP `script-src 'self'`), which is what keeps projects safe from a bad extension. So the IDE does not pretend: **Download for VS Code** (Projects tab, the project's right-click menu, or the VS Code tab) writes `<project>-vscode.zip` with the project, `.vscode/settings.json` from the IDE's settings, `.vscode/extensions.json` recommending the language extensions for the languages in the project and `GitHub.copilot` and `GitHub.copilot-chat`, and a `.code-workspace`. Opening it in VS Code offers to install them. Designed and not built (ADR-0022, PLAN P7.4 to P7.6): a language-server bridge (`fsh lsp-bridge`), FSharpened as a VS Code extension, and an AI completion provider interface (Workers AI first).

## Build the front end
```bash
dotnet tool restore
cd ide/web && npm ci && npm run build      # Fable, then esbuild -> site/dist/ide.js
npm test                                   # the compiled core against real compiler output (needs the Fable output)
cd ../.. && python tools/site/build.py --require-ide   # regenerate pages and fingerprint the bundle into site/public
```
`IDE_GATEWAY=https://your-gateway node build.mjs` points a local build at another gateway.

## Deploy (maintainer)
```bash
cd gateway && npm ci && npm test && npx wrangler deploy
npx wrangler pages deploy site/public --project-name fsharpened --branch main
```
Deploy the gateway first when compiler ids or limits change; `gateway/test/policy.test.ts` fails if its allowlist and `tools/FSharpened.Ide/Engines.fs` disagree (ids, languages, flag rules and backends).

## Operate
- **Rate limit:** 12 runs per minute per client (`RUN_LIMITER` in `gateway/wrangler.jsonc`).
- **Origins:** `ALLOWED_ORIGINS` must list every site origin that hosts the IDE; others get 403.
- **Cache:** successful results are cached for one hour under a hash of the request. Nothing else is stored; Worker logs hold request metadata only.
- **Backends:** `UPSTREAM` (Compiler Explorer, `https://godbolt.org`) and `WANDBOX` (`https://wandbox.org`) in `gateway/wrangler.jsonc`. Point either at a self-hosted instance by changing the variable.
- **Upstream busy:** both services rate-limit an address after a burst (Wandbox answered HTTP 429 after about nine calls in two seconds). The gateway turns an upstream 429 into its own 429 with `retry-after: 10`, which the IDE shows as "the execution service is busy"; other failures walk the fallback chain (sibling compiler, warm result, offline checks, escalation). Wandbox serves one compiler per language except JavaScript and PHP, so for TypeScript and R the chain goes straight to offline checks.
- **Upstream down:** same chain. The offline checks (bracket balance for F#, C, C#, C++, Rust and Java; a missing entry point for C, C++, Rust, Go, Java, Dart, VB.NET and Haskell) keep working without any network.
- **CSP:** the `/ide/*` block in `site/static/_headers` detaches the site-wide policy and relaxes only `style-src-elem` and `connect-src`. If the gateway URL changes, update it there. Wandbox is called by the Worker, never by the page, so it needs no CSP entry.

## Adding a language or a compiler
A compiler for an existing language: add it to `Engines.fs` and to `COMPILERS` in `gateway/src/policy.ts` together (same id, same language, the right backend); the test enforces it.

A new language, in this order:
1. Probe the backend first. Run a hello, a runtime error and a compile error through the real service (Compiler Explorer's `POST /api/compiler/<id>/compile` with `executorRequest`, or Wandbox's `POST /api/compile.json`) and keep the raw outputs. If the error path is unreadable, stop (that is why Kotlin and Julia are out).
2. `tools/FSharpened.Ide/Languages.fs`: a case in `Language` and a row in `Language.infos` (id, aliases, file name, group, whether it takes flags); and `Project.fs`, the extensions that map to it (`ProjectPath.byExtension`), so files of that language are recognised in a project.
3. `Engines.fs`: its compilers, newest first, with two or three siblings.
4. `Problems.fs`: one regular expression for the one-line error format, or a `Trace` entry when the location and the message are on different lines. Add tests from the raw outputs you kept. If the language prints errors on stdout (PHP does), handle it in `Protocol.interpret`. Only add it to `bracketLanguages` if `bracketCheckCore` really understands its comments and strings.
5. `Templates.fs`: at least one starter that runs; the "balanced and has an entry point" test checks it offline.
6. `gateway/src/policy.ts` (`COMPILERS`, and `FLAG_LANGUAGES` only if it takes flags) and `gateway/test`.
7. `ide/web/src/editor.js`: a CodeMirror mode from `@codemirror/legacy-modes` (or a small custom one) and a keyword list.
8. Build (`npm run build`), run `npm test` in `ide/web`, rebuild the site, deploy the gateway, then the site, and run the live check below.

## Live check
After a deploy, run every language's first starter through the real gateway from an allowed origin, then one compile error and one runtime error for a few languages. A language is "supported" only when the real answer comes back. The latest result is recorded in `docs/STATUS.md`.

## Privacy
Code is sent to Compiler Explorer or Wandbox (whichever serves the language) when the user presses Run; the page says so, naming both. Projects (files, tabs, input and compiler choices) live in the browser's local storage; snapshots live in memory until the page closes. Share links keep code in the URL fragment, which is never sent to a server. A `.vsix`, theme, snippets file or `settings.json` that you import is read inside the page and never uploaded; what is kept (themes, snippets, settings, recent commands) stays in this browser's local storage. Download for VS Code builds its zip in the page.
