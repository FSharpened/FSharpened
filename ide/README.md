# Browser IDE

The IDE at https://fsharpened.pages.dev/ide/ (ADR-0018). Operations, build and deploy: `docs/operations/ide.md`.

- `web/` is the CodeMirror 6 shell. `npm run build` compiles the F# core with Fable (`../tools/FSharpened.Ide.Web`) and bundles everything to `site/public/ide/ide.js`.
- The logic lives in F#: `tools/FSharpened.Ide` (tested by `tests/FSharpened.Ide.Tests`). Do not re-implement it in JavaScript; extend the F# and export it from `Api.fs`.
- The gateway Worker is in `../gateway`.

Fable limits to remember: no `Array2D`, no `Stack` enumeration, use `uint64` for hashing arithmetic, give class members `[<AttachMembers>]`, and declare `TargetFramework` in the project file itself.
