# Browser IDE

The IDE at https://fsharpened.pages.dev/ide/ (ADR-0018). Operations, build and deploy: `docs/operations/ide.md`.

- `web/` is the CodeMirror 6 shell. `npm run build` compiles the F# core with Fable (`../tools/FSharpened.Ide.Web`) and bundles everything to `site/dist/ide.js`; `python tools/site/build.py --require-ide` fingerprints it into the site.
- The logic lives in F#: `tools/FSharpened.Ide` (tested by `tests/FSharpened.Ide.Tests`). Do not re-implement it in JavaScript; extend the F# and export it from `Api.fs`.
- The gateway Worker is in `../gateway`. It routes each compiler id to Compiler Explorer or Wandbox (ADR-0020); the language table, compilers, templates and error parsers all live in `tools/FSharpened.Ide`.
- `web/src` is split by job: `main.js` wires the page, `editor.js` is CodeMirror with one state per file, `tree.js` the file tree, `store.js` the saved projects (testable in Node with a fake storage), `archive.js` and `projectio.js` the ZIP format and project import and export, `transfer.js` downloads, file pickers and drag and drop, `dom.js` the small DOM helpers. The VS Code-style layer (ADR-0022) is `workbench.js` (wires everything and the shortcuts), `commands.js` (the command list), `palette.js`, `contextmenu.js` and `menus.js` (right-click menus), `searchpanel.js`, `outline.js`, `settings.js` (settings, themes and imported packs, with their limits), `extpanel.js` with `vsix.js` and `jsonc.js` (importing `.vsix`, themes, snippets and `settings.json` as data; nothing from an extension runs), `shortcuts.js`.
- `npm test` in `web/` runs the compiled core against real compiler output. Adding a language: `docs/operations/ide.md`, section "Adding a language or a compiler".

Fable limits to remember: no `Array2D`, no `Stack` enumeration, use `uint64` for hashing arithmetic, give class members `[<AttachMembers>]`, and declare `TargetFramework` in the project file itself.
