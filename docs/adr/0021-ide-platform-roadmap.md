# ADR-0021: The IDE as a platform: projects, community, tooling, pre-compilation and a code-only agent

- **Status:** Proposed (requested by the maintainer in chat, 2026-11-16; needs formal acceptance)
- **Date:** 2026-11-16
- **Deciders:** maintainer (request in chat), Architect, Reviewer

## Context
ADR-0018 to ADR-0020 gave the browser IDE eighteen languages, each as one file run through an allowlisting gateway. The maintainer now asks for the next layer, in one message:

1. Polyglot **projects**: many files, folders, several languages, linked together; saved and managed; copy, download, import and export.
2. A cross-language **linter**, a complete **tooling system** and a **pipelines** framework around the IDE.
3. A **live code view** beside the editor: resizable, minimisable, maximisable; shows compiled output after a build and instant results for languages that need no compile.
4. A **polyglot chess game** as the default project, using all eighteen languages working together.
5. **Community Projects**: publish from the IDE or GitHub, automatic scanning for bugs and malicious code, an approval queue in the hub console with comments and reject-for-resubmission, then a public gallery the IDE can open.
6. **Cloudflare Workers AI** (free models) as a Worker for the scanning and assistance features.
7. Integrate **rust-rtpc** and build **fsharpened-rtpc**, a real-time pre-compiler for every supported language, in its own repository.
8. Make **all of FSharpened** atomic and hot-reloadable with parallel state, snapshots, self-recovery, and deterministic failover.
9. A from-scratch, **recursively self-training MoE/MoA agent** of 20 to 100 (later hundreds to thousands) hyper-specialised models that understand only computer science, plus a **desktop training GUI** with manual training, telemetry and a live "stream of thought" view.

This is many quarters of work, not one change. Some parts are limited by facts, not effort; those are stated below so that nobody builds on a promise the project cannot keep.

## Decision
Build it in the order below. Each phase ends in something a person can use and a test that proves it, and each phase gets its own ADR when it makes a structural choice (a new service, a new trust boundary, a new dependency). This ADR fixes the order, the shape, and the limits.

### Phase A: projects (browser only, no new service)
*Status 2026-11-16: the model, whole-project snapshots, the file tree and tabs, saved projects, download, import and share are built, tested and deployed (2026-11-16). Pipelines, the GitHub import, the chess project and the live view are not.*
- A **project** is a tree of files and folders, each file with a language (from its extension), plus a manifest `fsharpened.project.json` (name, entry points, pipeline, links). The F# core owns the model, validation and manifest codec; the page renders it.
- **Save and manage:** several projects kept in the browser (rename, duplicate, delete, import, export); the existing statepoint timeline snapshots the whole project, not one buffer.
- **Move data in and out:** download one file, copy a file's text, download a folder or a whole project as a standard `.zip`, import a `.zip`, a folder or several files, import from a public GitHub repository URL (read-only fetch of the archive through the gateway, size-capped).
- **Linking files:** two mechanisms, both data in the manifest and both checked by the linter: *imports* inside one language (the language's own `import`/`use`/`#include`, resolved against project files by the run packager), and *pipes* between languages (below).
- **Polyglot execution:** the execution services run one program in one language in a sandbox with no network, so languages cannot call each other directly. They cooperate through a **pipe**: stage N's standard output becomes stage N+1's standard input, as line-delimited JSON (`fsh-pipe/1`). A pipeline is an ordered list of stages in the manifest. The gateway gains one endpoint that runs a pipeline server-side in one rate-limit unit with a hard stage and time cap, so a chess move through many languages does not need many client round trips. Honest cost: stages are separate cold processes (hundreds of milliseconds to seconds each; Wandbox stages several seconds).
- **Default project:** a polyglot chess game. Each language owns one real part of the work (for example move generation, evaluation, search, opening book, rules and notation, rendering, test oracle), exchanging positions and moves as `fsh-pipe/1` messages, with the board drawn in the live view. It is a demonstration of the mechanism, not an engine competing with Stockfish.
- **Live code view:** a side panel (dock, resize, minimise, maximise) with tabs: *Structure* (outline and cross-file link graph, updated on every keystroke, no compile needed), *Output* (rendered result of the last run, such as the chess board), and *Compiled* (the compiled artefact after a build, where the backend can produce one: assembly for C, C++, Rust, Go and Java bytecode through Compiler Explorer; JavaScript from Fable for F# when the F# core can offer it). It never claims to show compiled output it does not have.

### Phase B: linting and tooling
- A **rule engine in the F# core**: rules are data (id, languages, severity, matcher, message, fix text) plus a few F# matchers; they run offline in the page on every change. Cross-language rules look at the project: a pipe stage whose output is not valid `fsh-pipe/1`, two stages disagreeing on a message schema, an entry point missing, a file no stage or import reaches, a secret-shaped string, a banned call.
- A **pipelines framework**: project pipelines (build, test, lint, run) declared in the manifest, executed by the page or the gateway, cached by content hash, reported as the same diagnostics the editor shows.
- Invariant 11 applies: wrap existing linters where a real service exists (compiler warnings already come through the backends); write rules only for what no tool provides across languages.

### Phase C: Community Projects
- **Flow:** *submit* (from the IDE, or by adding a GitHub topic or opening a pull request to a submissions repository) leads to *scan* (static rules, secret scan, banned-API scan, size and licence checks, plus a Workers AI review) and then to the **approval queue** in the hub console, which shows the project, the scan findings with file and line, the AI summary labelled as a model's opinion, and the diff against the previous version. The maintainer approves, comments, or rejects for resubmission. **Approved** projects appear in the public gallery on the site and open in the IDE.
- **Where it lives:** the hub Worker (ADR-0017) gains the submission API, a D1 schema (submissions, versions, findings, reviews, audit) and the console pages; R2 holds the project archives; the public site reads a published, static JSON index. GitHub is the identity and the source: submissions reference a repository and commit, and the scanner fetches that exact commit.
- **Safety:** a submission is untrusted input end to end. Scanning never executes it. The IDE never auto-runs a community project; it opens it and runs only on Run, in the same sandboxed backends. Nothing is published without a human approval (AGENTS.md: human approval is required). Findings from a model never approve or reject anything by themselves.

### Phase D: Workers AI (free models)
- A `ai` module in the hub Worker calls Workers AI through the `AI` binding with a fixed allowlist of models, per-day budgets under the free allocation, caching by content hash, and a hard fallback: with no AI available, scanning still completes using the static rules (ADR-0012: every model-backed feature has a deterministic fallback first). Model output is always data, never instructions, and is shown to the human labelled as such.

### Phase E: rust-rtpc and fsharpened-rtpc
- **rust-rtpc** (https://github.com/LoopyLuci/rust-rtpc) is a Rust daemon with a single `DaemonService` contract over line-delimited JSON (`apply`, `query`, `poll`, diagnostics), a transactional VFS, and MCP and ACP bindings. It is currently unlicensed, which means all rights are reserved; the maintainer must add a licence before any code is copied into this repository. Until then FSharpened integrates by **wrapping the daemon's wire protocol**, not by vendoring its code.
- **fsharpened-rtpc** is a new repository defining one language-neutral *pre-compile daemon contract* (the same shape: apply changes, query diagnostics, revision, cancel, poll), with adapters per language family: F# through FSharp.Compiler.Service (ADR-0001, no compiler fork), Rust through rust-rtpc, and for every other language a thin adapter around that language's own incremental checker or language server. Each adapter reports honestly what it can do (check only, or build) and its latency; the IDE uses the daemon where one is reachable and falls back to the gateway otherwise (ADR-0012).
- Creating a public repository is an outward-facing action and waits for explicit maintainer approval of the name, owner and licence.

### Phase F: atomic, hot-reloadable, self-healing FSharpened
- Define the **atomicity contract** once (a document and a test kit in `FSharpened.Testing`): every stateful pillar exposes its state as a Statepoint-able value, applies changes as all-or-nothing transactions, can be hot-swapped through the Harmony statepoint session (ADR-0008), and recovers by restoring the last good statepoint with a deterministic rule for what to try next (ADR-0012). Then audit pillars against it one by one and record, per pillar, verified or not. Self-healing means a supervisor that detects a failed component, restores a statepoint, retries under a budget, and escalates; it never rewrites code on its own.
- This phase is a programme of work across the 29 pillars, tracked in `PLAN.md`; it cannot be declared done by one change.

### Phase G: the FSharpened Agent and the training desktop
- **Shape.** A router (the "mixture") dispatches each request to small specialist models or to deterministic tools, and an orchestrator (the "agents") composes their results and verifies them with real tools (compiler, tests, linter) before answering. Start with 20 specialists chosen by task (lint triage, bug explanation, navigation, completion, refactoring suggestion, performance hints, hardware selection, test generation, and so on), each behind the ML.Hardware resolver (ADR-0007) and each with a symbolic fallback first (ADR-0012).
- **Training.** The existing rules apply: frozen judges, step-level verification, canary and rollback, per-model recursion budgets reviewed by a human (ADR-0015). Training data is permissively licensed code and documentation with recorded provenance; nothing is trained on user code without opt-in. Model artefacts are never published without human approval (AGENTS.md).
- **Scope guard.** The agent is limited to computer science and code by three independent layers: an input classifier that refuses off-topic requests, a system policy, and an output check; an evaluation set of off-topic probes must pass before any model is promoted. The agent has no persona and no emotional language.
- **Training desktop GUI** (a desktop application built on `FSharpened.Desktop`, own ADR for the UI toolkit): datasets, runs, manual labelling and correction, evaluation, promotion gates, and telemetry (loss, metrics, router load, budgets, regressions). The **stream of thought** is a structured, logged trace of what the system does, not a model's private reasoning: router decisions with scores, which specialists ran, retrieved context, tool calls and results, verifier verdicts, and the final answer, rendered live as a timeline and graph beside the telemetry. It is faithful because it is produced by the harness, not narrated by a model.

## Limits that this ADR states on purpose
- **"Accurate 100% of the time" cannot be promised by anyone.** The target is an evaluated, published error rate per task, a promotion gate that blocks regressions, and verification by real tools (a suggested fix is compiled and tested before it is shown). The agent reports uncertainty and the evidence, not confidence theatre.
- **"20 to 100, then thousands of custom models trained from scratch" is a compute and data programme.** The first deliverable is the training harness, evaluation suite, registry, router and the first handful of small specialists; scale follows measured results, not the other way round.
- **Off-topic refusal is enforced and tested, not proven.** Layered checks and an adversarial test set reduce leakage; they cannot reduce it to zero.
- **Workers AI free models** are third-party, rate-limited, and change; they are an optional assistant, never the authority.
- **Atomic, hot-reloadable everywhere** is a property verified pillar by pillar; some pillars (a native Rust library, a GPU backend) can only be hot-swapped at a restart boundary, and the contract says so per pillar.

## Consequences
- New services and trust boundaries arrive one at a time, each with an ADR, threat-model review and a deployable, testable slice. Nothing in phases C to G ships before Phase A and B exist, because they all need the project model.
- Source code leaves the browser to more places over time; each addition updates the on-page disclosure and `docs/governance/licensing.md`.
- The default project changes from a one-file starter to a multi-file polyglot project; existing saved single-file buffers are migrated into a project, never lost.
- `PLAN.md` gains Phase 6 with these items so agents can claim them (Invariant: agents are permanent maintainers, ADR-0011).

## Alternatives considered
- **One mega-change.** Rejected: unreviewable, untestable, and the parts have different trust boundaries.
- **Calling a hosted frontier model for the agent.** Rejected by the request itself (custom, code-only models) and by ADR-0007's portability goals; hosted free models stay optional.
- **Letting languages call each other in-process (a shared runtime such as WebAssembly components).** The right long-term answer for some pairs, but not available for all eighteen languages in a browser today; pipes work with every backend now and do not preclude it later.
- **Vendoring rust-rtpc into this repository.** Blocked by its missing licence and unnecessary: its wire protocol is the integration surface.
