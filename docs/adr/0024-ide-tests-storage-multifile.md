# ADR-0024: Browser tests and accessibility checks, project storage in IndexedDB, and multi-file runs

- **Status:** Proposed (needs formal acceptance)
- **Date:** 2026-11-18
- **Deciders:** maintainer (task given in chat), Architect, Reviewer, QA/Release

## Context
ADR-0020 to ADR-0022 left four gaps in the browser IDE: nothing exercised the built page in a real browser, accessibility had never been measured, projects lived in `localStorage` (about 5 MB, synchronous, failing silently when full), and a "project" still ran one file at a time although ADR-0021 promised ordered pipelines (PLAN P6.A5).

## Decision

**1. Browser end-to-end tests (`ide/e2e`).** Playwright (pinned `@playwright/test`, Chromium only) drives the built page from `site/public` through a small static server that applies the real `site/static/_headers`. Every request that is not that server or the gateway is blocked and recorded, and the gateway is answered by a mock, so a test run never reaches Compiler Explorer, Wandbox or any network. Each test fails on a CSP violation, a console error or an uncaught page error. CI gets a `web-e2e` stage in `fsh_ci.py` (in the `all` profile, not in `full`, so `full` stays what `ci.yml` runs) and an `e2e` job in `ide.yml`. The stage skips with a warning when Chromium is not installed and fails with `--require-browser`.

**2. Accessibility checks.** `@axe-core/playwright` runs the WCAG 2.1 A and AA rule sets over eleven views in the light, dark and both high-contrast themes (and the other built-in themes once), plus keyboard-only tests for the tree, tabs, palette and dialogs. Every serious or critical violation axe found is fixed or listed in `docs/operations/ide.md`. Axe cannot judge everything; that page says what is left for a person.

**3. Storage in IndexedDB.** Database `fsharpened-ide`, one record per project in `projects`, the index (current project and order) and a migration note in `meta`. A project's record and the index are written in one transaction, so a crash cannot leave an index naming a project that is not there. Writes are queued, report their real outcome (including `QuotaExceededError`, which the page explains), and a small journal in `localStorage` covers a page closed before a transaction commits. The migration from `fsharpened.ide.v2*` copies everything in one transaction, reads it back and compares it with what was read, and only then deletes the old keys; any failure clears the database and leaves `localStorage` untouched. When IndexedDB is missing, blocked or does not answer, the store falls back to the previous `localStorage` layout. `fsharpened.ide.v1` is never touched. "One record per project" is the interpretation of the brief: it is the unit of storage, not a limit on how many files a project holds.

**4. Multi-file runs.** The gateway takes one source in one language (65,536 characters, ADR-0020) and that does not change: no new network service and no gateway endpoint. Instead the page turns a project into **one source per step before it asks the gateway**, in the F# core (`Bundle*.fs`, `Scan.fs`), and a **pipeline runner** chains the steps.

- *Bundling is text work with a small lexical scanner* (`Scan.mask`): it only finds comments and string literals, so `import`, `mod` and `#include` inside them are never read as code. It is not a parser, and where that matters the bundler refuses instead of guessing.
- *A file that needs no other file is sent byte for byte*, so nothing changes for one-file projects.
- *Errors are explicit:* an unresolved relative import, a module with no file, an include path that leaves the project, a dynamic `import()` of a project file, and a bundle over the gateway limit each stop the run before it starts, naming the file and line. Nothing is truncated.
- *Diagnostics are mapped back:* the bundle carries a map from its lines to project files, so a compiler error in `util.h` appears in `util.h`. Python embeds each file and compiles it under its own name, so its tracebacks already name the real file and line.
- *The pipeline* is `Project.Pipeline` (ADR-0021): ordered steps; each step is bundled on its own; the standard output of one is the standard input of the next; the first failing step stops the run and the rest are reported as not run. Output longer than the gateway's 4,096-character input limit stops the run with a message instead of being cut. Results are kept per step. This is the meaning ADR-0021 gave `fsh-pipe/1`, without a server-side endpoint.

| Language | Several files | How, and what to know |
|---|---|---|
| JavaScript | merged | Files become functions in a module table, so each keeps its own scope. `import`, `export` (including `export default`, `export { a as b }`, `export * from`), `require("./x")` and JSON files work; package names (`fs`, `node:readline`) stay `require` calls. Bindings are copied when imported (a circular import of a non-function sees `undefined`); top-level `await` and dynamic `import()` of project files are not supported. |
| TypeScript | merged | Imported files are joined into one scope (imported first) with `import` and `export` removed, which keeps every type so the compiler checks the whole program. Two files must not declare the same name; `import { a as b }`, `import * as ns` and `export =` are refused. Package imports are hoisted to the top, once. |
| Python | merged | The files are embedded and an import hook finds them: `import util`, `from pkg import mod`, packages with and without `__init__.py`, relative imports. The entry runs as `__main__`. Modules are looked up below the entry's folder. An absolute import of a name that is not in the project is left to Python. |
| Rust | merged | `mod name;` becomes `mod name { ... }` with the text of `name.rs` or `name/mod.rs`, nested as the compiler would find them. `#[path = ...]` is refused. |
| Go | merged | The files of the entry's folder in the same package are merged into one file with one import block. A sibling with its own `main` is left out and said so; imports of packages inside the project are refused. |
| Java | merged | The classes the entry names (by file name) are merged into one file in the default package; only the entry keeps `public`; `package` lines and project imports are dropped, other imports are hoisted. |
| C, C++ | merged | `#include "x.h"` is replaced by the file once; the matching `x.c`, `x.cpp`, `x.cc` or `x.cxx` follows the entry. An include that is not in the project is left to the compiler and noted. Names that clash across files are the compiler's to report. |
| F#, C#, VB.NET, Dart, Ruby, Lua, PHP, R, Haskell, OCaml | entry only | The entry file runs alone; the notes name the files that stayed behind. F# depends on file order and project files, and C# and VB.NET need usings, namespaces and partial types merged by a real parser; the rest are simply not implemented. |

**Backend multi-file support was not used.** Compiler Explorer's API can take several files, but the gateway protocol carries one `source`, and widening it means changing the gateway and its allowlist. Whether and how well Compiler Explorer's multi-file mode works was not probed; no request was made to Compiler Explorer or Wandbox while building this. If it is wanted later, it belongs in a gateway ADR.

## Consequences
- Pages, tests and CI got heavier: Playwright and axe are devDependencies of `ide/e2e` only (not shipped; MIT and MPL-2.0 respectively), and the browser download is a CI step. The `web-e2e` stage is skipped, with a warning, where no browser is installed.
- A bundle can differ from what a person would compile on their own machine (one scope for TypeScript, one file for Go and Java). The Output panel says when files were bundled and which were left out, and the table above is shown in the page.
- IndexedDB changes where projects live. The migration is verified and reversible until the old keys are deleted; a person who downgrades the page finds their projects only in the new store (the old keys are gone after a verified copy). Download `.zip` remains the way out.
- Verification: the bundles were run with real `node`, `python`, `go`, `rustc`, `javac` and, for TypeScript, `esbuild` after stripping types. C and C++ bundles were checked as text only (no C compiler on the test machine). The browser flows run against the mocked gateway only.

## Not decided here
A gateway pipeline endpoint (server-side `fsh-pipe/1`), gateway-side multi-file requests, a real parser for C#/F#, and bundling for the languages listed as entry only.
