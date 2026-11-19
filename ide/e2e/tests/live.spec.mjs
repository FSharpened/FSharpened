// Live diagnostics (ADR-0025) and the editing aids around them: the opt-in compiler check as you type, offline import problems,
// go to definition and the outline. The gateway is mocked: every request the page makes is recorded and answered by the test,
// so no real Compiler Explorer or Wandbox is ever called.
import { test, expect, okRun } from "../support/fixtures.mjs";

/** A new project holding the given files, made through the page the way a person would. The last file stays open. */
async function makeProject(ide, name, files) {
  await ide.fresh();
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

async function setLive(ide, on) {
  await ide.page.locator("#btn-settings").click();
  await expect(ide.page.locator("#settings")).toBeVisible();
  const box = ide.page.locator("#set-live");
  if ((await box.isChecked()) !== on) await box.setChecked(on);
  await ide.page.keyboard.press("Escape");
  await expect(ide.page.locator("#settings")).toBeHidden();
}

const cError = (line, message = "boom") => ({ status: 200, body: okRun("", { buildFailed: true, buildOutput: `<source>:${line}:5: error: ${message}\n`, exitCode: 1 }) });
const settle = (ide, ms = 2200) => ide.page.waitForTimeout(ms);   // longer than the 1.5 s pause before a check starts

test.describe("live check is opt in", () => {
  test("it is off by default: typing sends nothing and the setting says what it would do", async ({ ide }) => {
    await makeProject(ide, "Quiet", { "main.c": "int main(void) { return 0; }\n" });
    await settle(ide);
    expect(ide.gateway.calls).toHaveLength(0);
    await expect(ide.page.locator("#livenote")).toBeHidden();
    await ide.page.locator("#btn-settings").click();
    await expect(ide.page.locator("#set-live")).not.toBeChecked();
    await expect(ide.page.locator("#set-live-note")).toContainText("sent to Compiler Explorer or Wandbox and run there");
  });

  test("turning it on is announced, survives a reload, and Reset to defaults turns it off", async ({ ide }) => {
    await makeProject(ide, "Remember", { "main.c": "int x;\n" });
    await setLive(ide, true);
    await expect(ide.page.locator("#livenote")).toBeVisible();
    await ide.open();
    await expect(ide.page.locator("#livenote")).toBeVisible();
    await ide.page.locator("#btn-settings").click();
    await expect(ide.page.locator("#set-live")).toBeChecked();
    await ide.page.locator("#set-reset").click();
    await expect(ide.page.locator("#set-live")).not.toBeChecked();
    await ide.page.keyboard.press("Escape");
    await expect(ide.page.locator("#livenote")).toBeHidden();
  });

  test("the command palette can switch it", async ({ ide }) => {
    await ide.fresh();
    await ide.palette("live check");
    await ide.page.keyboard.press("Enter");
    await expect(ide.page.locator("#livenote")).toBeVisible();
  });
});

test.describe("checking as you type", () => {
  test("after a pause one check is sent, and its errors become a squiggle and a Problems row without leaving the Output tab", async ({ ide }) => {
    await makeProject(ide, "Live", { "main.c": "int main(void) { return 0; }\n" });
    await setLive(ide, true);
    ide.gateway.reply = () => cError(1, "expected ';'");
    await ide.setText("int main(void) { return 1 }\n");
    await expect(ide.page.locator("#problems .prob")).toHaveCount(1, { timeout: 8000 });
    await expect(ide.page.locator("#problems .prob .loc")).toHaveText("1:5");
    await expect(ide.page.locator("#problems .prob")).toContainText("expected ';'");
    await expect(ide.page.locator("#editor .cm-lintRange-error, #editor .cm-lint-marker-error").first()).toBeVisible();
    await expect(ide.page.locator("#livenote")).toContainText("Checked on");
    await expect(ide.page.locator('.ptabs [data-p="output"]')).toHaveAttribute("aria-selected", "true");
    expect(ide.gateway.calls).toHaveLength(1);
    expect(ide.gateway.calls[0].source).toBe("int main(void) { return 1 }\n");
    // the same text is not asked about twice
    await ide.page.locator("#editor .cm-content").click();
    await ide.page.keyboard.press("End");
    await settle(ide);
    expect(ide.gateway.calls).toHaveLength(1);
  });

  test("typing in bursts sends only the final text", async ({ ide }) => {
    await makeProject(ide, "Burst", { "main.c": "int a;\n" });
    await setLive(ide, true);
    await ide.editor.click();
    await ide.page.keyboard.press("Control+End");
    for (const piece of ["int b;", "\nint c;", "\nint d;"]) { await ide.page.keyboard.type(piece); await ide.page.waitForTimeout(400); }
    await expect(ide.page.locator("#livenote")).toContainText("Checked on", { timeout: 8000 });
    expect(ide.gateway.calls).toHaveLength(1);
    expect(ide.gateway.calls[0].source).toContain("int d;");
  });

  test("an edit while a check is on its way cancels it: the old answer never reaches the screen", async ({ ide }) => {
    await makeProject(ide, "Cancel", { "main.c": "int a;\n" });
    await setLive(ide, true);
    ide.gateway.reply = async (body, n) => {
      if (n === 1) { await new Promise((r) => setTimeout(r, 2500)); return cError(1, "STALE"); }
      return cError(1, "FRESH");
    };
    await ide.editor.click();
    await ide.page.keyboard.press("Control+End");
    await ide.page.keyboard.type("int first;");
    await expect.poll(() => ide.gateway.calls.length, { timeout: 6000 }).toBe(1);
    await ide.page.keyboard.type(" int second;");
    await expect(ide.page.locator("#problems .prob")).toContainText("FRESH", { timeout: 9000 });
    await settle(ide, 3000);
    await expect(ide.page.locator("#problems .prob")).toHaveCount(1);
    await expect(ide.page.locator("#problems")).not.toContainText("STALE");
    expect(ide.gateway.calls).toHaveLength(2);
  });

  test("a rate-limited answer pauses checking and says so; further edits send nothing", async ({ ide }) => {
    await makeProject(ide, "Limited", { "main.c": "int a;\n" });
    await setLive(ide, true);
    ide.gateway.reply = () => ({ status: 429, body: { error: "rate limited: 12 runs per minute" }, headers: { "retry-after": "30" } });
    await ide.editor.click();
    await ide.page.keyboard.press("Control+End");
    await ide.page.keyboard.type("int b;");
    await expect(ide.page.locator("#livenote")).toContainText("paused", { timeout: 8000 });
    await ide.page.keyboard.type(" int c;");
    await settle(ide);
    await ide.page.keyboard.type(" int d;");
    await settle(ide);
    expect(ide.gateway.calls).toHaveLength(1);
  });

  test("a service that is down is reported once, and the editor keeps working", async ({ ide }) => {
    await makeProject(ide, "Down", { "main.c": "int a;\n" });
    await setLive(ide, true);
    ide.gateway.reply = () => ({ status: 503, body: { error: "the execution service is busy" } });
    await ide.editor.click();
    await ide.page.keyboard.press("Control+End");
    await ide.page.keyboard.type("int b;");
    await expect(ide.page.locator("#livenote")).toContainText("unavailable", { timeout: 8000 });
    await expect(ide.page.locator("#problems")).not.toContainText("busy");
    await settle(ide);
    expect(ide.gateway.calls).toHaveLength(1);
  });

  test("pressing Run takes over: the waiting check is dropped and Run is sent once", async ({ ide }) => {
    await makeProject(ide, "Takeover", { "main.c": "int a;\n" });
    await setLive(ide, true);
    await ide.editor.click();
    await ide.page.keyboard.press("Control+End");
    await ide.page.keyboard.type("int b;");
    await ide.page.locator("#btn-run").click();
    await expect(ide.page.locator("#summary")).not.toBeEmpty();
    await settle(ide);
    expect(ide.gateway.calls).toHaveLength(1);
  });

  test("with live check off, a project import that cannot be found is still shown while typing, and nothing is sent", async ({ ide }) => {
    await makeProject(ide, "Offline", { "main.js": 'import { y } from "./missing";\n' });
    await expect(ide.page.locator("#problems .prob")).toHaveCount(1);
    await expect(ide.page.locator("#problems .prob")).toContainText('cannot find "./missing"');
    await expect(ide.page.locator("#editor .cm-lintRange-error, #editor .cm-lint-marker-error").first()).toBeVisible();
    await settle(ide);
    expect(ide.gateway.calls).toHaveLength(0);
  });

  test("a live result for a bundled project is placed in the file the compiler named", async ({ ide }) => {
    await makeProject(ide, "Bundled", { "util.h": "#pragma once\nint twice(int x);\nint broken(void);\n", "main.c": '#include "util.h"\nint main(void) { return twice(1); }\n' });
    await setLive(ide, true);
    ide.gateway.reply = (body) => cError(body.source.split("\n").findIndex((l) => l.startsWith("int broken")) + 1);
    await ide.editor.click();
    await ide.page.keyboard.press("Control+End");
    await ide.page.keyboard.type("// edited");
    await expect(ide.page.locator("#problems .prob .loc")).toHaveText("util.h:3:5", { timeout: 8000 });
    expect(ide.gateway.calls).toHaveLength(1);
  });
});

test.describe("go to definition and the outline", () => {
  test("F12 jumps to the declaration in the same file", async ({ ide }) => {
    await makeProject(ide, "Same", { "main.js": "function helper() {}\n\nhelper" });
    await ide.editor.click();
    await ide.page.keyboard.press("F12");
    await expect(ide.page.locator("#st-pos")).toHaveText(/Ln 1,/);
  });

  test("F12 opens the other file that declares the name", async ({ ide }) => {
    await makeProject(ide, "Across", { "lib.js": "export function helper() {}\n", "main.js": 'import { helper } from "./lib.js";\nhelper' });
    await ide.editor.click();
    await ide.page.keyboard.press("F12");
    await expect(ide.tab("lib.js")).toHaveAttribute("aria-selected", "true");
    await expect(ide.page.locator("#st-pos")).toHaveText(/Ln 1,/);
  });

  test("a name with no declaration says so instead of doing nothing", async ({ ide }) => {
    await makeProject(ide, "None", { "main.js": "console.log" });
    await ide.editor.click();
    await ide.page.keyboard.press("F12");
    await expect(ide.page.locator(".toast, #toast, [role=status]").filter({ hasText: "No declaration" }).first()).toBeVisible();
  });

  test("the outline lists the file's declarations and clicking one moves the caret there", async ({ ide }) => {
    await makeProject(ide, "Outline", { "main.js": "function alpha() {}\n\nfunction beta() {}\n\nclass Gamma {}\n" });
    await ide.showSide("outline");
    const rows = ide.page.locator("#side .spanel[data-s=outline] .ol-row");
    await expect(rows).toHaveCount(3);
    await rows.filter({ hasText: "beta" }).click();
    await expect(ide.page.locator("#st-pos")).toHaveText(/Ln 3,/);
  });
});
