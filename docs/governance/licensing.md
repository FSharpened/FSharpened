# Licensing and Dependency Inventory

FSharpened is **Apache-2.0** (ADR-0013). Every dependency needs an entry here before it lands (Invariant 14); `check_docs.py` and the SBOM job fail otherwise. Procedure: `.agents/skills/update-license-inventory.md`.

Licenses below are the project's current understanding and **must be verified against each package's published license before first release**.

| Dependency | License | Compatibility | Obligations | Upstream tier |
|---|---|---|---|---|
| TorchSharp | MIT-style (verify) | Compatible | Attribution | B |
| DiffSharp | BSD-style (verify) | Compatible | Attribution | B |
| FsLangMCP | verify | Pending | Attribution | B |
| ONNX Runtime | MIT | Compatible | Attribution | C |
| ML.NET | MIT | Compatible | Attribution | C |
| ILGPU | University of Illinois/NCSA-style (verify) | Pending | Attribution | C |
| Expecto | Apache-2.0 | Compatible | Attribution | C |
| FsCheck | BSD-3-Clause | Compatible | Attribution | C |
| Stryker.NET | Apache-2.0 | Compatible | Attribution | C |
| DuckDB | MIT | Compatible | Attribution | C |
| Sigstore | Apache-2.0 | Compatible | Attribution | C |
| xunit (tests) | Apache-2.0 | Compatible | Attribution | C |
| Fable compiler (build tool) | MIT | Compatible | Attribution; not shipped | C |
| Fable.Core | MIT | Compatible | Attribution | C |
| CodeMirror 6 (`@codemirror/*`, `@lezer/*`) | MIT | Compatible | Attribution (shipped in `ide.js`) | C |
| esbuild (build tool) | MIT | Compatible | Not shipped | C |
| Playwright (`@playwright/test`, `ide/e2e` devDependency) | Apache-2.0 | Compatible | Not shipped; downloads Chromium in CI only | C |
| axe-core and `@axe-core/playwright` (`ide/e2e` devDependency) | MPL-2.0 | Compatible | Not shipped; used unmodified | C |
| Wrangler, Workers types (dev tools) | Apache-2.0 / MIT (verify) | Compatible | Not shipped | C |
| Compiler Explorer public API (service, not code) | Service terms apply (verify) | Pending | Attribution on the IDE page; keep rate limit; no abuse; plan self-hosting if usage grows | C |
| Wandbox public API (service, not code) | Service terms apply (verify) | Pending | Named on the IDE page next to Compiler Explorer; the gateway rate-limits and caches; plan self-hosting if usage grows (ADR-0020) | C |
| Rust toolchain (`cargo`, `rustc`; build tool) | MIT OR Apache-2.0 | Compatible | Not shipped; the reference crate has no dependencies (ADR-0019) | C |

Copyleft or commercial-restricted dependencies are rejected unless isolated by an ADR.
