// The command palette and quick open: one native <dialog> holding a text field and a list. The page decides what the
// rows are (files, commands, symbols, themes, projects); this file does the keyboard, the ARIA and the drawing.
import { el, fill } from "./dom.js";

const MAX_ROWS = 80;

/** `label` with the characters at `positions` wrapped in <mark>. */
export function highlighted(label, positions) {
  if (!positions || !positions.length) return [label];
  const on = new Set(positions);
  const out = [];
  let run = "", marked = false;
  const flush = () => { if (run) out.push(marked ? el("mark", {}, run) : run); run = ""; };
  for (let i = 0; i < label.length; i++) {
    const m = on.has(i);
    if (m !== marked) { flush(); marked = m; }
    run += label[i];
  }
  flush();
  return out;
}

/**
 * dialog: the <dialog>; input, list, hint, title: its parts.
 * open({ title, placeholder, value, provider(query) -> { rows, hint? }, pick(row, query, how), keepOpen })
 * A row is { id, label, positions?, detail?, keys?, badge?, disabled? }.
 */
export function createPalette({ dialog, input, list, hint, title }) {
  let session = null;
  let rows = [];
  let at = 0;
  let returnTo = null;
  let seq = 0;

  const optionId = (i) => "pal-opt-" + i;

  function draw() {
    const nodes = rows.slice(0, MAX_ROWS).map((r, i) => {
      const li = el("li", { role: "option", id: optionId(i), "aria-selected": String(i === at), "aria-disabled": r.disabled ? "true" : null, class: "pal-row" + (i === at ? " on" : "") + (r.disabled ? " off" : "") },
        el("span", { class: "pal-main" }, el("span", { class: "pal-label" }, highlighted(r.label, r.positions)), r.detail ? el("span", { class: "pal-detail" }, r.detail) : null),
        r.badge ? el("span", { class: "pal-badge" }, r.badge) : null,
        r.keys ? el("kbd", { class: "pal-keys" }, r.keys) : null);
      li.addEventListener("mousemove", () => { if (at !== i) { at = i; mark(); } });
      li.addEventListener("click", () => choose(i, "click"));
      return li;
    });
    fill(list, nodes);
    if (rows.length > MAX_ROWS) list.append(el("li", { class: "pal-more", role: "presentation" }, `${rows.length - MAX_ROWS} more: type to narrow the list`));
    mark();
  }

  /** Move the highlight without redrawing every row. */
  function mark() {
    [...list.querySelectorAll('[role="option"]')].forEach((n, i) => {
      const on = i === at;
      n.setAttribute("aria-selected", String(on));
      n.classList.toggle("on", on);
    });
    if (rows.length) { input.setAttribute("aria-activedescendant", optionId(at)); list.querySelector(`#${optionId(at)}`)?.scrollIntoView({ block: "nearest" }); }
    else input.removeAttribute("aria-activedescendant");
  }

  function refresh(keepSelection = false) {
    if (!session) return;
    const mine = ++seq;
    const out = session.provider(input.value);
    if (mine !== seq) return;
    rows = out.rows ?? [];
    if (!keepSelection || at >= rows.length) at = 0;
    const firstUsable = rows.findIndex((r) => !r.disabled);
    if (rows[at]?.disabled && firstUsable >= 0) at = firstUsable;
    hint.textContent = out.hint ?? session.hint ?? "";
    draw();
    // a screen reader hears how many rows there are without reading them all
    list.setAttribute("aria-label", rows.length ? `${rows.length} result${rows.length === 1 ? "" : "s"}` : "No results");
  }

  function choose(i, how) {
    const r = rows[i];
    if (!r || r.disabled) return;
    const s = session;
    const q = input.value;
    if (!s.keepOpen) close(false);
    s.pick(r, q, how);
  }

  function move(delta) {
    if (!rows.length) return;
    const shown = Math.min(rows.length, MAX_ROWS);
    for (let k = 0; k < shown; k++) {
      at = (at + delta + shown) % shown;
      if (!rows[at].disabled) break;
    }
    mark();
  }

  function close(restore = true) {
    if (!session) return;
    const s = session;
    session = null;
    seq++;
    if (dialog.open) dialog.close();
    if (restore && returnTo?.isConnected) returnTo.focus();
    s.onClose?.();
  }

  input.addEventListener("input", () => refresh());
  input.addEventListener("keydown", (e) => {
    if (e.key === "ArrowDown") { e.preventDefault(); move(1); }
    else if (e.key === "ArrowUp") { e.preventDefault(); move(-1); }
    else if (e.key === "PageDown") { e.preventDefault(); for (let k = 0; k < 8; k++) move(1); }
    else if (e.key === "PageUp") { e.preventDefault(); for (let k = 0; k < 8; k++) move(-1); }
    else if (e.key === "Enter") { e.preventDefault(); choose(at, "enter"); }
    else if (e.key === "Tab") { e.preventDefault(); move(e.shiftKey ? -1 : 1); } // the dialog holds focus; Tab walks the list
  });
  dialog.addEventListener("cancel", (e) => { e.preventDefault(); close(); });
  // a click on the backdrop (the dialog element itself) dismisses it
  dialog.addEventListener("mousedown", (e) => { if (e.target === dialog) close(); });
  dialog.addEventListener("close", () => { if (session) close(false); });

  return {
    isOpen: () => !!session,
    close,
    refresh,
    open({ title: heading, placeholder = "", value = "", provider, pick, keepOpen = false, hint: h = "", onClose, select = false }) {
      if (session) close(false);
      returnTo = document.activeElement;
      session = { provider, pick, keepOpen, hint: h, onClose };
      title.textContent = heading;
      input.placeholder = placeholder;
      input.value = value;
      at = 0;
      if (!dialog.open) dialog.showModal();
      refresh();
      input.focus();
      if (select) input.select(); else input.setSelectionRange(value.length, value.length);
    },
  };
}
