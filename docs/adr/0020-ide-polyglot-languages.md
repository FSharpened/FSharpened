# ADR-0020: A polyglot browser IDE: eighteen languages through two allowlisted execution backends

- **Status:** Proposed (requested by the maintainer in chat, 2026-11-16; needs formal acceptance)
- **Date:** 2026-11-16
- **Deciders:** maintainer (request in chat), Architect, Reviewer

## Context
F# is rarely used alone. Fable compiles it to JavaScript, TypeScript, Python, Rust and Dart; teams mix it with C#, VB.NET, R, SQL-adjacent scripting and native code. ADR-0018 gave the IDE F#, C, C# and C++; ADR-0019 added Rust. The maintainer asked that the IDE support the rest of what F# and FSharpened touch: JavaScript, TypeScript, VB.NET and the other .NET languages, Python, Dart, R, and similar.

Constraints:
- Real compilers and interpreters only. A tab that cannot run its language is worse than no tab (the page already says that code leaves the browser when Run is pressed).
- Invariant 1: nothing platform-privileged; the browser is the one platform every contributor has.
- ADR-0006: wrap an existing service. We do not host a compile farm.
- Invariant 14: every external dependency has a licence or terms entry.
- Compiler Explorer (CE), our backend since ADR-0018, runs C#, F#, VB.NET, Python, Dart, Java, Go, Ruby, Lua, Haskell and OCaml, but cannot run JavaScript (its JavaScript compilers do not execute) and has no R or PHP.
- Wandbox, a public community compile service, runs Node.js, TypeScript (real `tsc`, type-checked, then Node), R and PHP.

## Decision
1. **Eighteen languages**, in five groups shown as one tab strip: .NET (F#, C#, VB.NET), the Fable targets (JavaScript, TypeScript, Python, Rust, Dart), systems (C, C++, Go, Java), scripting and data (Ruby, Lua, PHP, R), functional (Haskell, OCaml).
2. **The gateway gains a second backend, Wandbox, behind the same seam.** `gateway/src/policy.ts` maps every allowed compiler id to a language and a backend (`ce` or `wandbox`); the page and the F# core still send one request shape. The gateway adapts the request and normalises the response to the one result shape the page already understands. The allowlist, origin check, size caps, rate limit, upstream timeout and one-hour cache apply to both. The Wandbox base URL is a variable (`WANDBOX`), like `UPSTREAM`.
3. **Compiler flags stay allowlisted per language.** Only C, C++ and Rust take flags; every other language takes none, exactly as F# and C# do today.
4. **Each language gets the same four things in the F# core**, tested natively: a language entry (id, aliases, file name, group), compilers, starter templates, and diagnostics parsing for its error format. Where a language reports an error in two lines (a location, then a message, or the reverse) a small table-driven parser pairs them; formats were captured from the real services, not guessed. Offline structural checks (layer 3) run only for languages whose comment and string rules the checker really implements (F#, C, C#, C++, Rust, Java); the others get entry-point checks only, because a false "unbalanced bracket" is worse than no check.
5. **Editor support** comes from CodeMirror's stream modes, plus a small PHP mode written here because none ships in `legacy-modes`.
6. **Not included, with the reason.** Kotlin and Julia: their public CE compilers fail on the error path (Kotlin reports "Assertion failed" for any compile error, Julia is killed for exceeding the time limit on a trivial exception), so errors would be unreadable. Scala, Swift, Zig, Erlang, Elixir and many more exist on CE or Wandbox and can be added by the procedure in `docs/operations/ide.md` when someone needs them. Running JavaScript or Python in the browser itself (no network) would remove the dependency for those two; the gateway seam keeps that open.

## Consequences
- A second runtime dependency on a third-party service. Wandbox rate-limits an address after a short burst (HTTP 429, observed after about nine calls in two seconds). The gateway already limits each client to 12 runs a minute and caches successful results for an hour; a 429 walks the IDE's fallback chain (sibling compiler, warm result, offline checks, escalation) like any other upstream failure. Moving to a self-hosted Wandbox or Compiler Explorer is one variable.
- Source code leaves the browser to **two** services now. The page's disclosure is updated to name both.
- Licence and terms entries for Wandbox are added to `docs/governance/licensing.md` (status: pending verification, like CE).
- `gateway/test/policy.test.ts` keeps the gateway allowlist equal to `tools/FSharpened.Ide/Engines.fs`, and now also checks, for every compiler, the language it serves, which backend serves it, and that only C, C++ and Rust may carry flags.
- The bundle grows by the extra editor modes (tens of kilobytes) and the page by the tab strip, which scrolls on narrow screens.
- Risk accepted: a language service can change its output format. Parsers degrade to "no editor marker" (the raw stderr is always shown), never to a wrong marker, because each requires its specific pattern.

## Alternatives considered
- **Run JavaScript, TypeScript, Python and R inside the browser (a Worker, Pyodide, webR, the TypeScript compiler).** Private and offline, but tens of megabytes of downloads, a CSP relaxation (`unsafe-eval`, `wasm-unsafe-eval`, remote script origins) that every page would have to justify, and one execution model per language. Deferred, not rejected: the seam allows it language by language.
- **One backend only (Compiler Explorer).** Cannot run JavaScript, R or PHP, and its TypeScript is a native-compiler experiment, not `tsc`.
- **Piston.** Its public instance requires a token since 2026; not usable by a static site.
- **Self-hosted compile farm.** Always-on servers and a sandbox to secure and pay for (see ADR-0018).
