// The built IDE page in a real browser: files, editing, palette, quick open, search and replace, themes, settings,
// share links, ZIP export and import, switching projects, and Run against a mocked gateway.
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { test, expect, okRun, GATEWAY } from "../support/fixtures.mjs";

const read = async (ide) => (await ide.editor.innerText()).replace(/ /g, " ");

/** A project of two small JavaScript files, made through the page like a person would. */
async function makeProject(ide, name, files) {
  await ide.showSide("projects");
  await ide.page.locator("#p-new").click();
  await ide.answer(name);
  await ide.showSide("files");
  for (const [file, text] of Object.entries(files)) {
    await ide.page.locator("#f-new").click();
    await ide.answer(file);
    await ide.setText(text);
  }
}

test.describe("the page", () => {
  test("opens on the starter project with every language and a ready status", async ({ ide }) => {
    await ide.fresh();
    await expect(ide.page).toHaveTitle(/IDE/);
    await expect(ide.page.locator("#proj-name")).toHaveText("Starter examples");
    await expect(ide.page.locator("#tree [role=treeitem]")).toHaveCount(18);
    await expect(ide.tab("Program.fs")).toHaveAttribute("aria-selected", "true");
    await expect(ide.page.locator("#status")).toContainText("ready");
    expect(ide.consoleErrors, "no console errors while loading").toEqual([]);
  });

  test("is served with a Content-Security-Policy that blocks inline scripts, and stays quiet", async ({ ide }) => {
    const res = await ide.page.goto(ide.url);
    const csp = res.headers()["content-security-policy"];
    expect(csp).toContain("script-src 'self'");
    expect(csp).not.toMatch(/script-src[^;]*unsafe-inline/);
    expect(csp).toContain("connect-src 'self' " + GATEWAY);
    await expect(ide.page.locator("#ide.ready")).toBeVisible();
    // the policy is really enforced: an injected inline script does not run and is reported as a violation
    const ran = await ide.page.evaluate(() => {
      window.__csp = [];
      window.__injected = false;
      const s = document.createElement("script");
      s.textContent = "window.__injected = true";
      document.head.append(s);
      return window.__injected;
    });
    expect(ran).toBe(false);
    await expect.poll(() => ide.page.evaluate(() => window.__csp.length)).toBeGreaterThan(0);
    await ide.page.evaluate(() => { window.__csp = []; });
    expect(ide.consoleErrors.filter((m) => !/Content Security Policy/.test(m)), "no other console errors").toEqual([]);
  });
});

test.describe("files", () => {
  test("create, rename and delete a file, with the tree, the tabs and the project following", async ({ ide }) => {
    await ide.fresh();
    await ide.page.locator("#f-new").click();
    await ide.answer("src/util.js");
    await expect(ide.tab("util.js")).toHaveAttribute("aria-selected", "true");
    await expect(ide.treeItem("src")).toBeVisible();
    await expect(ide.treeItem("util.js")).toBeVisible();

    await ide.treeItem("util.js").click();
    await ide.page.locator("#f-rename").click();
    await ide.answer("src/helpers.js");
    await expect(ide.tab("helpers.js")).toBeVisible();
    await expect(ide.tab("util.js")).toHaveCount(0);

    await ide.treeItem("helpers.js").click();
    await ide.page.locator("#f-delete").click();
    await expect(ide.tab("helpers.js")).toHaveCount(0);
    await expect(ide.page.locator("#toast")).toContainText("Deleted src/helpers.js");
  });

  test("an invalid name is refused with the reason and nothing is made", async ({ ide }) => {
    await ide.fresh();
    await ide.page.locator("#f-new").click();
    await ide.answer("../escape.js");
    await expect(ide.page.locator("#ask-error")).toBeVisible();
    await expect(ide.page.locator("#ask-error")).not.toBeEmpty();
    await ide.page.locator("#ask-cancel").click();
    await expect(ide.page.locator("#tree [role=treeitem]")).toHaveCount(18);
  });

  test("an edit is kept across a reload", async ({ ide }) => {
    await ide.fresh();
    await ide.page.locator("#f-new").click();
    await ide.answer("notes.txt");
    await ide.setText("remember this line");
    await ide.page.waitForTimeout(700); // the save after a pause in typing
    await ide.open();
    await expect(ide.tab("notes.txt")).toHaveAttribute("aria-selected", "true");
    await expect.poll(() => read(ide)).toContain("remember this line");
  });
});

test.describe("finding things", () => {
  test("the command palette lists commands, filters them and runs one", async ({ ide }) => {
    await ide.fresh();
    await ide.palette("word wrap");
    const row = ide.page.locator('#pal-list [role=option]').first();
    await expect(row).toContainText("Toggle Word Wrap");
    await ide.page.keyboard.press("Enter");
    await expect(ide.page.locator("#palette")).not.toBeVisible();
    await expect(ide.page.locator("#toast")).toContainText("Word wrap on");
    await expect(ide.page.locator("#editor .cm-lineWrapping")).toHaveCount(1);
    // Escape closes it and focus returns to where it came from
    await ide.palette();
    await ide.page.keyboard.press("Escape");
    await expect(ide.page.locator("#palette")).not.toBeVisible();
  });

  test("quick open jumps to a file, and : goes to a line", async ({ ide }) => {
    await ide.fresh();
    await ide.page.keyboard.press("Control+P");
    await expect(ide.page.locator("#palette")).toBeVisible();
    await ide.page.locator("#pal-input").fill("main.rs");
    await expect(ide.page.locator("#pal-list [role=option]").first()).toContainText("main.rs");
    await ide.page.keyboard.press("Enter");
    await expect(ide.tab("main.rs")).toHaveAttribute("aria-selected", "true");
    await expect.poll(() => read(ide)).toContain("fn main");
    await ide.page.keyboard.press("Control+G");
    await ide.page.locator("#pal-input").fill(":3");
    await ide.page.keyboard.press("Enter");
    await expect(ide.page.locator("#st-pos")).toContainText("Ln 3");
  });

  test("search finds across files, and replace all asks first, applies, and can be undone from the timeline", async ({ ide }) => {
    await ide.fresh();
    await makeProject(ide, "Search demo", { "a.js": "const foo = 1; foo++;", "b.js": "console.log(foo);" });
    await ide.page.keyboard.press("Control+Shift+F");
    await expect(ide.page.locator('.spanel[data-s="search"]')).toBeVisible();
    await ide.page.locator("#sr-q").fill("foo");
    await expect(ide.page.locator("#sr-sum")).toContainText("3");
    await expect(ide.page.locator("#sr-results .sr-group")).toHaveCount(2);
    await ide.page.locator("#sr-r").fill("bar");
    await ide.page.locator("#sr-replace-all").click();
    await expect(ide.page.locator("#ask-label")).toContainText("bar");
    await ide.answer();
    await ide.page.locator(".ptabs [data-p=timeline]").click();
    await expect(ide.page.locator("#timeline li").first()).toContainText("Before replace in project");
    await ide.tab("a.js").click();
    await expect.poll(() => read(ide)).toContain("const bar = 1; bar++;");
    await ide.tab("b.js").click();
    await expect.poll(() => read(ide)).toContain("console.log(bar);");
    // undo through the statepoint that was made before the replacement
    await ide.page.locator("#timeline li").filter({ hasText: "Before replace in project" }).getByRole("button", { name: "Restore" }).click();
    await ide.tab("b.js").click();
    await expect.poll(() => read(ide)).toContain("console.log(foo);");
  });
});

test.describe("appearance", () => {
  test("a theme change applies at once, shows its contrast, and is remembered", async ({ ide }) => {
    await ide.fresh();
    await ide.page.locator("#btn-settings").click();
    await expect(ide.page.locator("#settings")).toBeVisible();
    await ide.page.locator("#set-theme").selectOption("dracula");
    await expect(ide.page.locator("html")).toHaveAttribute("data-ide-theme", "dracula");
    await expect(ide.page.locator("#set-theme-note")).toContainText("to 1");
    await ide.page.keyboard.press("Escape");
    await expect(ide.page.locator("#settings")).not.toBeVisible();
    await ide.open();
    await expect(ide.page.locator("html")).toHaveAttribute("data-ide-theme", "dracula");
  });

  test("settings change the editor and survive a reload", async ({ ide }) => {
    await ide.fresh();
    await ide.page.locator("#btn-settings").click();
    await ide.page.locator("#set-font").fill("20");
    await ide.page.locator("#set-font").dispatchEvent("change");
    await ide.page.locator("#set-lines").uncheck();
    await expect(ide.page.locator("#editor .cm-lineNumbers")).toHaveCount(0);
    await ide.page.keyboard.press("Escape");
    expect(await ide.page.evaluate(() => getComputedStyle(document.documentElement).getPropertyValue("--ed-fs").trim())).toBe("20px");
    await ide.open();
    await expect(ide.page.locator("#editor .cm-lineNumbers")).toHaveCount(0);
    expect(await ide.page.evaluate(() => getComputedStyle(document.documentElement).getPropertyValue("--ed-fs").trim())).toBe("20px");
  });
});

test.describe("moving projects around", () => {
  test("a share link opens the project as a new one for a first-time visitor", async ({ ide, context }) => {
    await context.grantPermissions(["clipboard-read", "clipboard-write"], { origin: ide.origin });
    await ide.fresh();
    await makeProject(ide, "Shared demo", { "hello.py": "print('shared ok')" });
    await ide.page.locator("#btn-share").click();
    await expect(ide.page.locator("#toast")).toContainText(/Link copied|address bar/);
    const link = await ide.page.evaluate(() => navigator.clipboard.readText());
    expect(link).toContain("#s=");
    expect(link.length).toBeLessThan(12000);

    await ide.fresh(); // a visitor with nothing stored
    await ide.page.goto("about:blank");
    await ide.page.goto(link);
    await expect(ide.page.locator("#ide.ready")).toBeVisible();
    await expect(ide.page.locator("#proj-name")).toHaveText("Shared demo");
    await expect(ide.tab("hello.py")).toBeVisible();
    await expect.poll(() => read(ide)).toContain("print('shared ok')");
    await expect(ide.page.locator("#summary")).toContainText("shared project");
    expect(new URL(ide.page.url()).hash, "the fragment is cleared so a reload does not import it twice").toBe("");
    await ide.showSide("projects");
    await expect(ide.page.locator("#plist li")).toHaveCount(2); // the starter and the shared one
  });

  test("a project is exported as a .zip and imported again as a new project", async ({ ide }) => {
    await ide.fresh();
    await makeProject(ide, "Zip demo", { "src/app.js": "export const n = 42;", "README.md": "# zip demo" });
    await ide.showSide("projects");
    const [download] = await Promise.all([ide.page.waitForEvent("download"), ide.page.locator("#p-zip").click()]);
    expect(download.suggestedFilename()).toMatch(/\.zip$/);
    const file = path.join(os.tmpdir(), `fsh-e2e-${process.pid}-${Date.now()}.zip`);
    await download.saveAs(file);
    const bytes = fs.readFileSync(file);
    expect(bytes.subarray(0, 2).toString()).toBe("PK");
    expect(bytes.includes("fsharpened.project.json")).toBe(true);
    expect(bytes.includes("export const n = 42;")).toBe(true);

    const [chooser] = await Promise.all([ide.page.waitForEvent("filechooser"), ide.page.locator("#p-import-zip").click()]);
    await chooser.setFiles(file);
    await expect(ide.page.locator("#summary")).toContainText("Imported");
    await expect(ide.page.locator("#proj-name")).toHaveText("Zip demo");
    await expect(ide.treeItem("app.js")).toBeVisible();
    await expect(ide.treeItem("README.md")).toBeVisible();
    await ide.showSide("projects");
    await expect(ide.page.locator("#plist li")).toHaveCount(3); // starter, the original, the import
    fs.rmSync(file, { force: true });
  });

  test("switching projects shows each project's own files, tabs and text", async ({ ide }) => {
    await ide.fresh();
    await makeProject(ide, "First", { "one.js": "// first project" });
    await makeProject(ide, "Second", { "two.js": "// second project" });
    await expect(ide.page.locator("#proj-name")).toHaveText("Second");
    await ide.showSide("projects");
    await ide.page.locator("#plist button", { hasText: "First" }).click();
    await expect(ide.page.locator("#proj-name")).toHaveText("First");
    await expect(ide.tab("one.js")).toBeVisible();
    await expect(ide.tab("two.js")).toHaveCount(0);
    await expect.poll(() => read(ide)).toContain("// first project");
    await ide.open(); // and the choice of project is remembered
    await expect(ide.page.locator("#proj-name")).toHaveText("First");
  });
});

test.describe("running (the gateway is mocked: Compiler Explorer and Wandbox are never called)", () => {
  test("Run sends the open file to the gateway and shows its output", async ({ ide }) => {
    await ide.fresh();
    ide.gateway.reply = () => ({ status: 200, body: okRun("Hello, F#!\nHello, FSharpened!\n") });
    await ide.page.locator("#btn-run").click();
    await expect(ide.page.locator("#stdout")).toContainText("Hello, FSharpened!");
    await expect(ide.page.locator("#summary")).toHaveClass(/ok/);
    expect(ide.gateway.calls).toHaveLength(1);
    expect(ide.gateway.calls[0]).toMatchObject({ language: "fsharp" });
    expect(ide.gateway.calls[0].source).toContain("EntryPoint");
  });

  test("Ctrl+Enter in the editor runs, and a build error becomes a Problems entry with a marker", async ({ ide }) => {
    await ide.fresh();
    ide.gateway.reply = () => ({ status: 200, body: okRun("", { buildFailed: true, buildOutput: "<source>(4,13): error FS0039: The value or constructor 'prnt' is not defined.\n", exitCode: 1 }) });
    await ide.editor.click();
    await ide.page.keyboard.press("Control+Enter");
    await expect(ide.page.locator('.ptabs [data-p="problems"]')).toHaveAttribute("aria-selected", "true");
    await expect(ide.page.locator("#problems .prob")).toHaveCount(1);
    await expect(ide.page.locator("#problems .prob")).toContainText("prnt");
    await expect(ide.page.locator("#problems .prob .loc")).toHaveText("4:13");
    await expect(ide.page.locator("#editor .cm-lintRange-error, #editor .cm-lint-marker-error").first()).toBeVisible();
  });

  test("a busy gateway is reported, not hidden", async ({ ide }) => {
    await ide.fresh();
    ide.gateway.reply = () => ({ status: 429, body: { error: "the execution service is busy, try again in a few seconds" }, headers: { "retry-after": "10" } });
    await ide.page.locator("#btn-run").click();
    await expect(ide.page.locator("#summary")).toContainText("busy");
    await expect(ide.page.locator("#btn-run")).toBeEnabled();
  });

  test("with the gateway unreachable the offline checks still answer", async ({ ide }) => {
    await ide.fresh();
    await ide.page.locator("#f-new").click();
    await ide.answer("broken.rs");
    await ide.setText("fn main() { let x = (1; }");
    ide.gateway.reply = () => ({ status: 503, body: { error: "unavailable" } });
    await ide.page.locator("#btn-run").click();
    await expect(ide.page.locator("#summary")).toContainText(/offline|Could not reach/i);
    await expect(ide.page.locator("#btn-run")).toBeEnabled();
  });

  test("each language goes to the compiler chosen for it", async ({ ide }) => {
    await ide.fresh();
    await ide.tab("Program.fs").click();
    await ide.treeItem("main.py").dblclick();
    await expect(ide.tab("main.py")).toHaveAttribute("aria-selected", "true");
    ide.gateway.reply = () => ({ status: 200, body: okRun("py\n") });
    await ide.page.locator("#btn-run").click();
    await expect(ide.page.locator("#stdout")).toContainText("py");
    expect(ide.gateway.calls[0]).toMatchObject({ language: "python" });
  });
});
