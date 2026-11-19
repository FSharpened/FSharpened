# ADR-0025: Live check in the browser IDE, opt in and rate budgeted

- **Status:** Proposed (needs formal acceptance)
- **Date:** 2026-11-18
- **Deciders:** maintainer (task given in chat), Architect, Reviewer, QA/Release

## Context
Problems and squiggles in the page came from two places: offline checks in the F# core (`Core.localChecks`, brackets and a few rules) and the result of pressing Run. The request was to check with the compiler as you type, using the existing Compiler Explorer path and its limits, and to add go to definition and an outline where the core has the data.

What the backend can do decides the design. The gateway has one endpoint, `/api/run`, and it **always executes**: there is no compile-only mode, and none was probed (no request to Compiler Explorer or Wandbox was made). A live check is therefore a run of the program with the person's standard input, on the same sandboxes, counted against the same per-client limit (12 a minute, shared with the Run button) and served from the same one-hour result cache. A program that prints, loops or sleeps does so on every check.

## Decision
**1. It is off until the person turns it on** (Settings, "Live check: ask the compiler as I type", or the command palette). The page used to say code is sent when Run is pressed; the status bar and the setting now say that, with Live check on, code is also sent a moment after typing stops and is *run*, the same as Run. The choice is a single flag in `localStorage` (`fsharpened.ide.livecheck.v1`), kept apart from the editor settings so the shared F# settings record, which the desktop app also builds, did not change. "Reset to defaults" turns it off.

**2. When a check may start is decided by one small module** (`ide/web/src/livecheck.js`, tested on a fake clock):
- *Debounce:* 1.5 s after the last edit.
- *Cancellation:* a new edit aborts the check in flight (`AbortController`, passed to `fetch`) and replaces the one waiting. An answer for text that is no longer in the editor, a project that is no longer open, or a run that began meanwhile is dropped.
- *Budget:* at most 4 checks start in any 60 s, a third of the gateway's allowance, so typing cannot use up the runs Run needs. When the budget is spent the newest request waits for a free slot instead of being lost.
- *Backoff:* an HTTP 429 pauses checking for the server's `retry-after` or 60 s, whichever is longer, doubling on repeats up to 5 minutes, and says so in the page.
- *Dedupe:* text that was just checked is not sent again.
- *Other failures* (unreachable, 5xx, a refused request) are reported once. Nothing retries on its own, and there is **no fallback** to the second engine: a live check uses the preferred compiler only.
- Run and the pipeline cancel it and it does not start while they run.

**3. The result goes where a run's result goes.** It is mapped back to project files like a run's (ADR-0024), shown as squiggles and Problems rows, and remembered as "what is on screen for this exact text" so it is not overwritten by the offline checks until the text changes. The Output panel is left alone; the live state is one line under the panel tabs.

**4. Offline, always, without any request:** a relative import that cannot be found (and the other bundle refusals of ADR-0024) now appears as a Problem with a squiggle while typing, not only when Run is pressed.

**5. Go to definition and the outline were already in the core** (`Core.symbolsDefinitions`, `symbolsOutline`; F12, the Outline tab, the breadcrumbs). Nothing was rebuilt; they now have browser tests. Ctrl+click for definition was not added: Ctrl+click already adds a caret in the editor.

## Consequences
- Anyone who turns it on sends code to the sandboxes much more often than before, and the code runs there. The setting says so before it is used; the default keeps the earlier promise.
- Results of live checks reuse the one-hour cache, so pressing Run on text that was just checked is usually answered at once.
- Live checks can only be as good as a run: a program that needs input the person has not typed, runs long or reads the clock reports whatever it does. A compile-only mode in the gateway would be the right fix and is a gateway ADR.
- Verification: unit tests of the scheduler (fake clock), and browser tests with the gateway mocked (off by default sends nothing, one check after a pause, bursts send one, an edit cancels the check in flight, 429 pauses, a failure is reported once, Run takes over, bundled results land in the right file), plus axe checks of the new states in four themes. No real Compiler Explorer or Wandbox request was made.

## Not decided here
A compile-only gateway mode, a language server per language for hover and completion, and cross-file rename.
