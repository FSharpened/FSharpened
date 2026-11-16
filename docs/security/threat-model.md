# Threat Model (STRIDE)

Reviewed by the Architect on every ADR touching a trust boundary (`review-threat-model`) and by the Reviewer on every PR touching a security-sensitive component. Status: Mitigated / Planned / Accepted / N/A. "Planned" means designed, not implemented.

## Trust boundaries
Compiler bridge and cache; statepoint runtime; ML pipeline and registry; agent operating system; supply chain; telemetry export.

| # | Boundary | Threat (STRIDE) | Mitigation | Status | Residual |
|---|---|---|---|---|---|
| 1 | CompilerBridge | **S** spoofed FCS diagnostics | Diagnostics produced in-process from FCS only; no ingestion of external diagnostic files without verification | Planned | Low |
| 2 | Per-file cache | **T** tampered cache entries | Content-addressed keys (file hash + options + references); verify hash on read; cache is untrusted input | Planned | Medium |
| 3 | Telemetry | **I** information disclosure | Local-only default; opt-in export; redact paths and source; feedback is privacy-preserving | Planned | Low |
| 4 | Statepoints | **E** elevation via forked processes | Forks run through `ISandbox` with least privilege | Planned | Medium |
| 5 | Statepoints | **T** tampered checkpoint files | Checkpoints content-addressed; signed when persisted for models | Planned | Medium |
| 6 | ML pipeline | **T** model poisoning | Frozen versioned datasets; lineage attestation; frozen judges | Planned | Medium |
| 7 | ML pipeline | **I** training-data exfiltration | Local-first stores; no mandatory remote service | Planned | Low |
| 8 | ML serving | **D/T** adversarial inputs | Input validation from `ML.Data.Validation`; drift detection; rollback | Planned | Medium |
| 9 | Model registry | **S/T** unsigned or swapped artifact | Sigstore signature over weights + judge config + data hash; runtime refuses mismatch in production | Planned | Low |
| 10 | Agent OS | **T/E** prompt injection via issue/PR text | Observed content is data; human-gated irreversible acts; Reviewer independent of author | Mitigated (process) | Medium |
| 11 | Agent OS | **E** privilege escalation via skill execution | Delegation matrix; skills cannot widen authority | Mitigated (process) | Low |
| 12 | Supply chain | **S** dependency confusion / typosquatting | Locked-mode restore, central package management, source mapping | Planned | Medium |
| 13 | Supply chain | **T** compromised upstream package | SBOM every build, signed releases, upstream tiering, license + provenance review | Planned | Medium |
| 14 | Wrapped tools | **T** malicious/changed wrapped tool | Pin versions; verify hashes; Tier C tracking | Planned | Medium |

Not defended against: a compromised developer machine, a malicious human maintainer, kernel/driver-level GPU vulnerabilities.
