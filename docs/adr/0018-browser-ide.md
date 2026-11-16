# ADR-0018: A browser IDE: an F# core compiled with Fable, executing through an allowlisting gateway

- Status: Accepted (maintainer approval given in chat, 2026-11-16: "Approve all others")
- Date: 2026-11-16

## Context
The public website needs a usable IDE for F#, C, C# and C++ that runs for anyone on any OS with nothing to install (Invariant 1: no platform is privileged), is built with F# and FSharpened itself, and costs nothing to operate at rest. Real compilers are required; we may not fork the F# compiler, and per ADR-0006 we wrap existing tools rather than rebuild them.

## Decision
1. **The IDE's logic is F#, tested natively, and compiled to JavaScript with Fable.** `tools/FSharpened.Ide` holds the language model, starter templates, workspace, statepoint-backed timeline (snapshot, restore, fork, diff), compiler-output parsing into `StructuredDiagnostic`, the request/report protocol, and the five-layer fallback chain (ADR-0012). It is unit-tested with xunit on .NET. `tools/FSharpened.Ide.Web` is the Fable entry point. The project links (does not copy) `Overlay.fs`, `Statepoints.fs` and `Diagnostics.fs`, so the browser runs the same statepoint code as every other consumer (ADR-0004).
2. **The shell is CodeMirror 6 plus a thin JS layer** (`ide/web`), bundled by esbuild into `site/public/ide/ide.js`. The bundle is generated and not committed.
3. **Execution goes through one seam: the gateway Worker** (`gateway/`, `ide.fsharpened.workers.dev`). It proxies to the Compiler Explorer public API. It enforces an origin allowlist, a compiler-id allowlist (kept equal to `Engines.fs` by a test), size caps, a token-by-token compiler-flag allowlist (no plugins, linker, pass or dump flags), a per-client rate limit, an upstream timeout, and a one-hour result cache keyed by a hash of the request. It stores no source code.
4. **Fallback chain.** Selected compiler, then sibling compilers, then a warm cached result for identical code, then offline local checks (bracket balance, missing entry point), then a structured escalation message. The IDE never ends at a bare "failed".
5. **Content-Security-Policy.** Every page keeps `style-src 'self'; script-src 'self'`. Only `/ide/*` additionally allows `style-src-elem 'unsafe-inline'` (CodeMirror injects its own `<style>` element) and `connect-src` to the gateway. Inline style attributes and inline scripts stay blocked.

## Consequences
- Runtime dependency on a third-party service. Mitigations: the fallback chain; the gateway is the only place the upstream URL lives (`UPSTREAM`), so moving to a self-hosted Compiler Explorer, or later to an in-browser compiler, changes one setting or one module.
- Source code leaves the browser when the user presses Run. The page says so next to the editor. Share links keep code in the URL fragment, which browsers never send to a server.
- The portable F# core is limited to what Fable supports (no `Array2D`, no `Stack` enumeration, `uint64` instead of `uint32` multiply for hashing). A pinned fingerprint test keeps .NET and JavaScript behaviour identical.
- Adds a deployable service and a front-end build. Both are optional for the rest of the repository; CI builds and tests them in `.github/workflows/ide.yml`.
- Dependencies recorded in `docs/governance/licensing.md`.

## Alternatives considered
- **Monaco plus a language server on our own servers**: needs always-on servers and a sandbox we would have to secure and pay for.
- **.NET WebAssembly hosting the F# compiler in the page**: tens of megabytes to download, cannot run C or C++, and ties us to a compiler build we would have to track. Deferred; the gateway seam keeps the door open.
- **Our own compile farm, or Judge0-style commercial sandboxes**: operational and security cost, or vendor limits, with no gain over wrapping an established public service.
