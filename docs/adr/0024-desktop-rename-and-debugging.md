# ADR-0024: Rename and debugging in the desktop IDE go through the protocols, with the adapter named by the language pack

- **Status:** Proposed (builds on ADR-0023; needs formal acceptance)
- **Date:** 2026-11-17
- **Deciders:** Tooling Builder, Architect, Security

## Context
ADR-0023 gave the desktop IDE a minimal language-server client (diagnostics, completion, definition, hover) and said that debugging and rename were next (PLAN P7.8). Both are protocols with a standard server on the other end (`textDocument/rename`, the Debug Adapter Protocol), so the question is only where the logic lives, what runs when, and what the person is asked.

## Decision
1. **Rename is LSP.** `Lsp.fs` gains `prepareRename` and `rename`; the answer (`WorkspaceEdit`, `changes` or `documentChanges`) is applied by pure code in `Rename.fs` (`Edits`): ranges are resolved to offsets in the file's own text, sorted, checked, and applied last-to-first. An edit that runs backwards, leaves the file or overlaps another refuses the **whole** rename. File operations in a `WorkspaceEdit` (create, rename, delete) are refused with a message, not half done. Files outside the opened folder are never written.
2. **The person sees what changes before it changes.** F2 asks for the new name, then shows a preview (files, number of changes, the lines before and after) with a confirm button. Files open in the editor are edited in place as one undoable edit and stay unsaved; the others are written through `DiskWorkspace`.
3. **Debugging is a DAP client in F#** (`Dap.fs`, in the host library, no UI types), framed like LSP. Everything an adapter sends is data: the requests an adapter may make of a client (`runInTerminal`, `startDebugging`) are answered as unsupported, and the client says so in `initialize`.
4. **The adapter is data too.** A pack's `debug` block names the adapter program (a bare name on the PATH), its arguments, how it is reached (`stdio`, or `tcp` with a `{port}` argument), an optional build step, and a launch template (JSON whose strings may use `{file}`, `{root}`, `{dir}`, `{out}`, `{project}`, `{projectdir}`, `{projectfile}` and `{newest:glob}`). Nothing runs because a pack says so: the person starts a session, in a trusted folder, with the same trust prompt Run uses. A missing adapter is reported before any build, with the pack's install hint.
5. **Keys follow the convention people have.** F5 starts debugging (or continues a paused program), Shift+F5 stops, F9 toggles a breakpoint, F10, F11 and Shift+F11 step. Running without a debugger moves to Ctrl+F5 (it was F5). F2 renames.

## Consequences
- Rename and debugging work for any language whose server or adapter speaks the protocol; adding one is a pack.
- The client is tested against a scripted adapter (stdio and TCP), a scripted language server, and, where it is installed, a real one (`debugpy`). Adapters not tried on a machine are marked as such in `docs/operations/desktop.md`; shipping a pack for one is not a claim that it was run.
- Launch templates are a small language of their own; they are validated when the pack loads (unknown placeholders and paths are refused), not when a session starts.
- Debugging runs the person's own code with their permissions, exactly as Run does; the sandbox story is unchanged (there is none).

## Not decided here
JavaScript and TypeScript through js-debug (it needs `startDebugging` child sessions), attach to a process (the protocol client sends `attach`, the UI has no way to pick a process), conditional breakpoints, watch expressions, and hot reload (ADR-0008).
