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

Copyleft or commercial-restricted dependencies are rejected unless isolated by an ADR.
