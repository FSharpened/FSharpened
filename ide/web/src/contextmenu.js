// An accessible context menu (WAI-ARIA "menu"): arrow keys, Home/End, type-ahead, Right/Left for sub-menus, Enter or
// Space to choose, Escape to close with focus handed back. The page decides the items; this file draws and drives them.
//
// item   = { label, run, keys?, disabled?, checked?, danger?, submenu?: item[] }
// "-"    = a separator
import { el } from "./dom.js";

const EDGE = 6; // px kept free around the menu

export function createContextMenu() {
  /** The open menus, outermost first: [{ node, items, parentItem }] */
  let stack = [];
  let returnTo = null;

  const rowsOf = (node) => [...node.querySelectorAll(':scope > [role^="menuitem"]')];
  const enabled = (node) => rowsOf(node).filter((r) => r.getAttribute("aria-disabled") !== "true");

  function build(items, labelledBy) {
    const node = el("div", { class: "ctx", role: "menu", "aria-label": labelledBy, tabindex: "-1" });
    let lastSep = true; // no separator first, none twice in a row
    for (const it of items) {
      if (it === "-") {
        if (!lastSep) node.append(el("div", { class: "ctx-sep", role: "separator" }));
        lastSep = true;
        continue;
      }
      lastSep = false;
      const role = it.checked === undefined ? "menuitem" : "menuitemcheckbox";
      const b = el("button", {
        type: "button", role, tabindex: "-1", class: "ctx-item" + (it.danger ? " danger" : ""),
        "aria-disabled": it.disabled ? "true" : null, "aria-checked": it.checked === undefined ? null : String(!!it.checked),
        "aria-haspopup": it.submenu ? "menu" : null, "aria-expanded": it.submenu ? "false" : null,
      }, el("span", { class: "ctx-check", "aria-hidden": "true" }, it.checked ? "✓" : ""), el("span", { class: "ctx-label" }, it.label),
      it.submenu ? el("span", { class: "ctx-arrow", "aria-hidden": "true" }, "▸") : it.keys ? el("span", { class: "ctx-keys" }, it.keys) : null);
      b._item = it;
      node.append(b);
    }
    // a separator left at the end (the items after it were all dropped) is not wanted
    while (node.lastElementChild?.classList.contains("ctx-sep")) node.lastElementChild.remove();
    return node;
  }

  function place(node, x, y, anchor) {
    node.style.left = "0px";
    node.style.top = "0px";
    node.hidden = false;
    const w = node.offsetWidth, h = node.offsetHeight;
    const vw = document.documentElement.clientWidth, vh = document.documentElement.clientHeight;
    let left = x, top = y;
    if (anchor) { // a sub-menu: right of its parent row, or left when there is no room
      left = anchor.right - 2;
      top = anchor.top - 4;
      if (left + w > vw - EDGE) left = Math.max(EDGE, anchor.left - w + 2);
    }
    if (left + w > vw - EDGE) left = Math.max(EDGE, vw - w - EDGE);
    if (top + h > vh - EDGE) top = Math.max(EDGE, vh - h - EDGE);
    node.style.left = left + "px";
    node.style.top = top + "px";
    node.style.maxHeight = vh - 2 * EDGE + "px";
  }

  function closeFrom(level) {
    while (stack.length > level) {
      const m = stack.pop();
      m.parentRow?.setAttribute("aria-expanded", "false");
      m.node.remove();
    }
  }

  function openSub(row, focusFirst) {
    const level = stack.findIndex((m) => m.node === row.parentElement);
    if (level < 0 || !row._item?.submenu) return;
    if (stack[level + 1]?.parentRow === row) { if (focusFirst) enabled(stack[level + 1].node)[0]?.focus(); return; }
    closeFrom(level + 1);
    const node = build(row._item.submenu, row._item.label);
    node.classList.add("sub");
    document.body.append(node);
    place(node, 0, 0, row.getBoundingClientRect());
    row.setAttribute("aria-expanded", "true");
    stack.push({ node, parentRow: row });
    if (focusFirst) enabled(node)[0]?.focus();
  }

  function close(restore = true) {
    if (!stack.length) return;
    closeFrom(0);
    document.removeEventListener("pointerdown", onOutside, true);
    document.removeEventListener("keydown", onKey, true);
    removeEventListener("blur", onAway);
    removeEventListener("resize", onAway);
    removeEventListener("scroll", onAway, true);
    removeEventListener("wheel", onAway, true);
    const back = returnTo;
    returnTo = null;
    if (restore && back?.isConnected) back.focus({ preventScroll: true });
  }

  function choose(row) {
    const it = row._item;
    if (!it || it.disabled) return;
    if (it.submenu) { openSub(row, true); return; }
    close();
    // after the menu is gone and focus is back, so a command that moves focus (a dialog, the editor) wins
    queueMicrotask(() => it.run?.());
  }

  function onOutside(e) { if (!stack.some((m) => m.node.contains(e.target))) close(false); }
  // scrolling or wheeling INSIDE a long menu is fine; anything else closes it
  function onAway(e) { if (e?.target?.nodeType && stack.some((m) => m.node.contains(e.target))) return; close(false); }

  function onKey(e) {
    if (!stack.length) return;
    const level = stack.findIndex((m) => m.node.contains(document.activeElement));
    const cur = stack[level >= 0 ? level : 0];
    const rows = enabled(cur.node);
    const i = rows.indexOf(document.activeElement);
    const go = (n) => { if (rows.length) rows[(n + rows.length) % rows.length].focus(); };
    switch (e.key) {
      case "ArrowDown": go(i < 0 ? 0 : i + 1); break;
      case "ArrowUp": go(i < 0 ? rows.length - 1 : i - 1); break;
      case "Home": go(0); break;
      case "End": go(rows.length - 1); break;
      case "ArrowRight": if (i >= 0 && rows[i]._item?.submenu) openSub(rows[i], true); else return; break;
      case "ArrowLeft": if (level > 0) { const p = stack[level].parentRow; closeFrom(level); p.focus(); } else return; break;
      case "Enter": case " ": if (i >= 0) choose(rows[i]); break;
      case "Escape": if (level > 0) { const p = stack[level].parentRow; closeFrom(level); p.focus(); } else close(); break;
      case "Tab": close(); break;
      default: {
        // type-ahead: the next item whose label starts with the letter
        if (e.key.length === 1 && !e.ctrlKey && !e.metaKey && !e.altKey) {
          const k = e.key.toLowerCase();
          const order = [...rows.slice(i + 1), ...rows.slice(0, i + 1)];
          order.find((r) => r._item.label.toLowerCase().startsWith(k))?.focus();
        } else return;
      }
    }
    e.preventDefault();
    e.stopPropagation();
  }

  // pointer: hover moves focus (and opens a sub-menu); a click chooses
  document.addEventListener("mouseover", (e) => {
    if (!stack.length) return;
    const row = e.target.closest?.('[role^="menuitem"]');
    if (!row || !stack.some((m) => m.node.contains(row))) return;
    if (row.getAttribute("aria-disabled") === "true") { row.focus({ preventScroll: true }); return; }
    row.focus({ preventScroll: true });
    if (row._item?.submenu) openSub(row, false);
    else closeFrom(stack.findIndex((m) => m.node === row.parentElement) + 1);
  });
  document.addEventListener("click", (e) => {
    if (!stack.length) return;
    const row = e.target.closest?.('[role^="menuitem"]');
    if (row && stack.some((m) => m.node.contains(row))) { e.preventDefault(); choose(row); }
  });

  return {
    isOpen: () => stack.length > 0,
    close,
    /** Show a menu with its top-left corner near (x, y). `from` is the element that gets focus back. */
    open({ x, y, items, label = "Context menu", from = document.activeElement, focusFirst = false }) {
      close(false);
      const usable = items.filter((it) => it === "-" || it);
      if (!usable.some((it) => it !== "-")) return;
      returnTo = from;
      const node = build(usable, label);
      document.body.append(node);
      place(node, x, y);
      stack = [{ node, parentRow: null }];
      document.addEventListener("pointerdown", onOutside, true);
      document.addEventListener("keydown", onKey, true);
      addEventListener("blur", onAway);
      addEventListener("resize", onAway);
      addEventListener("scroll", onAway, true);
      addEventListener("wheel", onAway, { capture: true, passive: true });
      // a mouse-opened menu waits for the first arrow key; a keyboard-opened one starts on its first item
      if (focusFirst) enabled(node)[0]?.focus(); else node.focus({ preventScroll: true });
    },
  };
}
