// Accessibility checks with axe-core (WCAG 2.0 and 2.1, levels A and AA) on the IDE page in the light, dark and the two
// high-contrast themes, in the states a person actually sees: the editor, each sidebar tab, a dialog, the palette, a context
// menu, a run result. Plus the keyboard-only operation that axe cannot judge: tree, tabs, palette and dialogs.
//
// What axe cannot check, and what is left, is listed in docs/operations/ide.md ("Accessibility").
import fs from "node:fs";
import AxeBuilder from "@axe-core/playwright";
import { test, expect, okRun } from "../support/fixtures.mjs";

const THEMES = [
  { id: "light", scheme: "light" },
  { id: "dark", scheme: "dark" },
  { id: "hc-light", scheme: "light" },
  { id: "hc-dark", scheme: "dark" },
];
const TAGS = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"];

async function setTheme(ide, id, scheme) {
  await ide.page.emulateMedia({ colorScheme: scheme });
  await ide.page.locator("#btn-settings").click();
  await ide.page.locator("#set-theme").selectOption(id);
  await ide.page.keyboard.press("Escape");
  await expect(ide.page.locator("html")).toHaveAttribute("data-ide-theme", id);
}

async function scan(ide, label) {
  const results = await new AxeBuilder({ page: ide.page }).withTags(TAGS).analyze();
  const bad = results.violations.map((v) => ({
    rule: v.id, impact: v.impact, help: v.help,
    nodes: v.nodes.slice(0, 6).map((n) => ({ target: n.target.join(" "), summary: n.failureSummary?.split("\n").slice(0, 3).join(" ") })),
  }));
  if (process.env.E2E_AXE_DUMP) fs.appendFileSync(process.env.E2E_AXE_DUMP, JSON.stringify({ label, bad }) + "\n");
  expect(bad, `${label}: axe violations (WCAG 2.1 AA)`).toEqual([]);
}

/** The views to scan. Each leaves the page in the state it describes; `after` puts it back. */
const VIEWS = {
  "editor and file tree": async () => {},
  "search panel with results": async (ide) => {
    await ide.page.keyboard.press("Control+Shift+F");
    await ide.page.locator("#sr-q").fill("main");
    await expect(ide.page.locator("#sr-results .sr-group").first()).toBeVisible();
  },
  "outline": async (ide) => { await ide.showSide("outline"); },
  "projects list": async (ide) => { await ide.showSide("projects"); },
  "VS Code tab": async (ide) => { await ide.showSide("extensions"); },
  "problems and timeline panels": async (ide) => {
    await ide.page.locator('.ptabs [data-p="problems"]').click();
    await ide.page.locator('.ptabs [data-p="timeline"]').click();
  },
  "run result": async (ide) => {
    ide.gateway.reply = () => ({ status: 200, body: okRun("Hello\n") });
    await ide.page.locator("#btn-run").click();
    await expect(ide.page.locator("#stdout")).toContainText("Hello");
  },
  "live check on, with a squiggle and a problem": async (ide) => {
    await ide.page.locator("#btn-settings").click();
    await ide.page.locator("#set-live").check();
    await ide.page.keyboard.press("Escape");
    ide.gateway.reply = () => ({ status: 200, body: okRun("", { buildFailed: true, buildOutput: "<source>:1:5: error: expected ';'\n", exitCode: 1 }) });
    await ide.page.locator("#f-new").click();
    await ide.answer("bad.c");
    await ide.setText("int a\n");
    await ide.page.locator('.ptabs [data-p="problems"]').click();
    await expect(ide.page.locator("#problems .prob")).toHaveCount(1, { timeout: 8000 });
    await expect(ide.page.locator("#livenote")).toContainText("Checked on");
  },
  "outline with declarations": async (ide) => {
    await ide.page.locator("#f-new").click();
    await ide.answer("shapes.js");
    await ide.setText("function area() {}\nclass Shape {}\n");
    await ide.showSide("outline");
    await expect(ide.page.locator(".spanel[data-s=outline] .ol-row").first()).toBeVisible();
  },
  "command palette": async (ide) => { await ide.palette("theme"); await expect(ide.page.locator("#pal-list [role=option]").first()).toBeVisible(); },
  "settings dialog": async (ide) => { await ide.page.locator("#btn-settings").click(); await expect(ide.page.locator("#settings")).toBeVisible(); },
  "file context menu": async (ide) => {
    // scroll the row into view first and let the scroll settle: a scroll while a menu is open closes it, by design
    await ide.treeItem("main.rs").scrollIntoViewIfNeeded();
    await ide.page.waitForTimeout(150);
    await ide.treeItem("main.rs").click({ button: "right" });
    await expect(ide.page.locator('[role="menu"]').first()).toBeVisible();
  },
  "inline name form": async (ide) => { await ide.page.locator("#f-new").click(); await expect(ide.page.locator("#askbar")).toBeVisible(); },
};

for (const theme of THEMES) {
  test.describe(`axe, theme ${theme.id}`, () => {
    for (const [name, prepare] of Object.entries(VIEWS)) {
      test(name, async ({ ide }) => {
        await ide.fresh();
        await setTheme(ide, theme.id, theme.scheme);
        await prepare(ide);
        await scan(ide, `${theme.id} / ${name}`);
      });
    }
  });
}

// The other built-in themes: the editor, the tree and the chrome in each.
for (const id of ["light-plus", "dark-plus", "monokai", "dracula"]) {
  test(`axe, theme ${id}: editor and file tree`, async ({ ide }) => {
    await ide.fresh();
    await setTheme(ide, id, id === "light-plus" ? "light" : "dark");
    await scan(ide, `${id} / editor and file tree`);
  });
}

test("every built-in theme's text meets WCAG AA on its editor background (high contrast: AAA), as computed by the core", async ({ ide }) => {
  await ide.fresh();
  await ide.page.locator("#btn-settings").click();
  const options = await ide.page.locator("#set-theme option").evaluateAll((os) => os.map((o) => ({ id: o.value, name: o.textContent })));
  expect(options.length).toBeGreaterThanOrEqual(9);
  for (const o of options.filter((x) => x.id !== "auto")) {
    await ide.page.locator("#set-theme").selectOption(o.id);
    const note = await ide.page.locator("#set-theme-note").innerText();
    expect(note, `${o.name}: ${note}`).toMatch(/meets (AA|AAA)/);
    if (/high contrast/i.test(o.name)) expect(note, `${o.name}: ${note}`).toMatch(/meets AAA/);
  }
});

test.describe("keyboard only", () => {
  test("the page has one main landmark, a skip link and a heading", async ({ ide }) => {
    await ide.fresh();
    await expect(ide.page.locator("main")).toHaveCount(1);
    await expect(ide.page.locator("h1")).toHaveCount(1);
    await ide.page.keyboard.press("Tab");
    await expect(ide.page.locator("a.skip")).toBeFocused();
  });

  test("the file tree: arrows move, Right and Left fold, Enter opens, and there is exactly one tab stop", async ({ ide }) => {
    await ide.fresh();
    await ide.page.locator("#f-new").click();
    await ide.answer("src/deep/inner.js");
    await ide.page.locator("#tree").getByRole("treeitem").first().focus();
    const stops = await ide.page.locator('#tree [role="treeitem"][tabindex="0"]').count();
    expect(stops, "a roving tabindex: one item in the tab order").toBe(1);
    await ide.page.keyboard.press("Home");
    const first = await ide.page.evaluate(() => document.activeElement?.dataset.path);
    await ide.page.keyboard.press("ArrowDown");
    const second = await ide.page.evaluate(() => document.activeElement?.dataset.path);
    expect(second).not.toBe(first);
    await ide.page.keyboard.press("End");
    await ide.page.keyboard.press("Enter");
    await expect(ide.page.locator('#ftabs [role="tab"][aria-selected="true"]')).toBeVisible();
    // fold and unfold a folder with Left and Right
    const folder = ide.page.locator('#tree [role="treeitem"][aria-expanded="true"]').first();
    await folder.focus();
    await ide.page.keyboard.press("ArrowLeft");
    await expect(ide.page.locator('#tree [role="treeitem"][aria-expanded="false"]').first()).toBeVisible();
    await ide.page.keyboard.press("ArrowRight");
    await expect(ide.page.locator('#tree [role="treeitem"][aria-expanded="true"]').first()).toBeVisible();
  });

  test("tabs: arrows move between open files and activate them, Delete closes", async ({ ide }) => {
    await ide.fresh();
    await ide.treeItem("main.py").click();
    await ide.treeItem("main.rs").click();
    await ide.tab("main.rs").focus();
    await ide.page.keyboard.press("ArrowLeft");
    await expect(ide.tab("main.py")).toHaveAttribute("aria-selected", "true");
    await expect(ide.tab("main.py")).toBeFocused();
    await ide.page.keyboard.press("Delete");
    await expect(ide.tab("main.py")).toHaveCount(0);
  });

  test("the palette is fully keyboard operable and returns focus", async ({ ide }) => {
    await ide.fresh();
    await ide.editor.click();
    await ide.page.keyboard.press("F1");
    await expect(ide.page.locator("#pal-input")).toBeFocused();
    await expect(ide.page.locator("#pal-input")).toHaveAttribute("role", "combobox");
    await ide.page.keyboard.type("line numbers");
    await expect(ide.page.locator('#pal-list [role=option][aria-selected="true"]')).toContainText("Line Numbers");
    const active = await ide.page.locator("#pal-input").getAttribute("aria-activedescendant");
    expect(active).toMatch(/^pal-opt-/);
    await ide.page.keyboard.press("Escape");
    await expect(ide.page.locator("#palette")).not.toBeVisible();
    await expect(ide.editor).toBeFocused();
  });

  test("a dialog traps focus, closes with Escape and gives focus back", async ({ ide }) => {
    await ide.fresh();
    await ide.page.locator("#btn-settings").focus();
    await ide.page.keyboard.press("Enter");
    await expect(ide.page.locator("#settings")).toBeVisible();
    for (let i = 0; i < 14; i++) {
      await ide.page.keyboard.press("Tab");
      // inside the dialog, or out on the browser's own interface (activeElement is then the body): never on the page behind it
      const where = await ide.page.evaluate(() => { const a = document.activeElement; return !a || a === document.body ? "browser" : a.closest("#settings") ? "dialog" : "page"; });
      expect(where, "the page behind an open modal dialog never gets focus").not.toBe("page");
    }
    await ide.page.keyboard.press("Escape");
    await expect(ide.page.locator("#settings")).not.toBeVisible();
    await expect(ide.page.locator("#btn-settings")).toBeFocused();
  });

  test("the focused control always has a visible focus indicator", async ({ ide }) => {
    await ide.fresh();
    const hidden = [];
    for (let i = 0; i < 22; i++) {
      await ide.page.keyboard.press("Tab");
      const info = await ide.page.evaluate(() => {
        const e = document.activeElement;
        if (!e || e === document.body) return null;
        const cs = getComputedStyle(e);
        const outline = cs.outlineStyle !== "none" && parseFloat(cs.outlineWidth) > 0;
        const shadow = cs.boxShadow && cs.boxShadow !== "none";
        const inEditor = !!e.closest(".cm-editor");
        return { id: e.id || e.className || e.tagName, ok: outline || shadow || inEditor };
      });
      if (info && !info.ok) hidden.push(info.id);
    }
    expect(hidden, "elements reached by Tab with no outline or ring").toEqual([]);
  });

  test("run results are announced: the summary and the status are live regions", async ({ ide }) => {
    await ide.fresh();
    expect(await ide.page.locator("#summary").getAttribute("role")).toBe("status");
    expect(await ide.page.locator("#status").getAttribute("aria-live")).toBe("polite");
    expect(await ide.page.locator("#toast").getAttribute("aria-live")).toBe("polite");
  });

  test("with reduced motion requested nothing animates or transitions", async ({ ide }) => {
    await ide.page.emulateMedia({ reducedMotion: "reduce" });
    await ide.fresh();
    const moving = await ide.page.evaluate(() => {
      const out = [];
      for (const e of document.querySelectorAll("body *")) {
        const cs = getComputedStyle(e);
        const t = cs.transitionDuration.split(",").some((d) => parseFloat(d) > 0.01);
        const a = cs.animationName !== "none" && cs.animationDuration.split(",").some((d) => parseFloat(d) > 0.01);
        if (t || a) out.push((e.id ? "#" + e.id : e.tagName.toLowerCase() + "." + e.className).slice(0, 60));
        if (out.length > 8) break;
      }
      return out;
    });
    expect(moving).toEqual([]);
  });
});
