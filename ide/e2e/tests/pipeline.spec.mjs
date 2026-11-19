// Multi-file runs and the project pipeline in a real browser (ADR-0024). The gateway is mocked: what the page sends is
// recorded and asserted, and no real compiler or interpreter is called.
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

test.describe("a program of several files", () => {
  test("Run sends the file and the files it imports as one source", async ({ ide }) => {
    await makeProject(ide, "Multi", { "lib.js": "export const x = 41;\n", "main.js": 'import { x } from "./lib.js";\nconsole.log(x + 1);\n' });
    ide.gateway.reply = () => ({ status: 200, body: okRun("42\n") });
    await ide.page.locator("#btn-run").click();
    await expect(ide.page.locator("#stdout")).toContainText("42");
    expect(ide.gateway.calls).toHaveLength(1);
    const sent = ide.gateway.calls[0];
    expect(sent.language).toBe("javascript");
    expect(sent.source).toContain('__r("lib.js")');
    expect(sent.source).toContain("exports,\"x\"");
    expect(sent.source).not.toContain("./lib.js\";\nconsole");
    await expect(ide.page.locator("#runinfo")).toContainText("Bundled 2 files into one source: lib.js, main.js");
  });

  test("a project of one file is sent exactly as written", async ({ ide }) => {
    await makeProject(ide, "Single", { "solo.js": "console.log('solo');\n" });
    await ide.page.locator("#btn-run").click();
    await expect(ide.page.locator("#stdout")).toContainText("Hello from the mocked gateway");
    expect(ide.gateway.calls[0].source).toBe("console.log('solo');\n");
  });

  test("an import that cannot be found stops the run before the gateway and says where", async ({ ide }) => {
    await makeProject(ide, "Broken", { "main.js": 'console.log(1);\nimport { y } from "./missing";\n' });
    await ide.page.locator("#btn-run").click();
    await expect(ide.page.locator("#summary")).toContainText("cannot find \"./missing\"");
    await expect(ide.page.locator(".ptabs [data-p=problems]")).toHaveAttribute("aria-selected", "true");
    await expect(ide.page.locator("#problems .prob")).toHaveCount(1);
    await expect(ide.page.locator("#problems .prob .loc")).toHaveText("2:1");
    expect(ide.gateway.calls).toHaveLength(0);
  });

  test("an error the compiler reports in an included file is shown in that file", async ({ ide }) => {
    await makeProject(ide, "Errors", { "util.h": "#pragma once\nint twice(int x);\nint broken(void);\n", "main.c": '#include "util.h"\nint main(void) { return twice(1); }\n' });
    ide.gateway.reply = (body) => {
      const line = body.source.split("\n").findIndex((l) => l.startsWith("int broken")) + 1;
      return { status: 200, body: okRun("", { buildFailed: true, buildOutput: `<source>:${line}:5: error: boom\n`, exitCode: 1 }) };
    };
    await ide.page.locator("#btn-run").click();
    await expect(ide.page.locator("#problems .prob")).toHaveCount(1);
    await expect(ide.page.locator("#problems .prob .loc")).toHaveText("util.h:3:5");
    await ide.page.locator("#problems .prob").click();
    await expect(ide.tab("util.h")).toHaveAttribute("aria-selected", "true");
  });
});

test.describe("the pipeline", () => {
  test("steps run in order, each one's output is the next one's input, and the results are listed", async ({ ide }) => {
    await makeProject(ide, "Pipe", { "gen.py": "print('abc')\n" });
    await ide.showSide("projects");
    await ide.page.locator("#pipe-add").click();
    await ide.showSide("files");
    await ide.page.locator("#f-new").click();
    await ide.answer("up.js");
    await ide.setText("console.log(require('fs').readFileSync(0, 'utf8').toUpperCase());\n");
    await ide.showSide("projects");
    await ide.page.locator("#pipe-add").click();
    await expect(ide.page.locator("#pipe-list li")).toHaveCount(2);
    ide.gateway.reply = (body, n) => ({ status: 200, body: okRun(n === 1 ? "abc\n" : body.stdin.toUpperCase()) });
    await ide.page.locator("#pipe-run").click();
    await expect(ide.page.locator("#summary")).toHaveText("All 2 steps finished");
    expect(ide.gateway.calls.map((c) => [c.language, c.stdin])).toEqual([["python", ""], ["javascript", "abc\n"]]);
    await expect(ide.page.locator("#steps li")).toHaveCount(2);
    await expect(ide.page.locator("#steps li.ok")).toHaveCount(2);
    await expect(ide.page.locator("#stdout")).toContainText("ABC");
  });

  test("a failing step stops the pipeline and the later steps do not run", async ({ ide }) => {
    await makeProject(ide, "Stop", { "a.py": "raise SystemExit(2)\n" });
    await ide.showSide("projects");
    await ide.page.locator("#pipe-add").click();
    await ide.showSide("files");
    await ide.page.locator("#f-new").click();
    await ide.answer("b.js");
    await ide.setText("console.log(1);\n");
    await ide.showSide("projects");
    await ide.page.locator("#pipe-add").click();
    ide.gateway.reply = () => ({ status: 200, body: okRun("", { exitCode: 2, stderr: "bad\n" }) });
    await ide.page.locator("#pipe-run").click();
    await expect(ide.page.locator("#summary")).toContainText("Step 1 of 2 (a.py) failed");
    await expect(ide.page.locator("#summary")).toContainText("1 step not run");
    await expect(ide.page.locator("#summary")).toHaveClass(/bad/);
    expect(ide.gateway.calls).toHaveLength(1);
    await expect(ide.page.locator("#steps li.fail")).toHaveCount(1);
  });

  test("steps can be reordered and removed, and the pipeline survives a reload", async ({ ide }) => {
    await makeProject(ide, "Order", { "one.py": "print(1)\n" });
    await ide.showSide("projects");
    await ide.page.locator("#pipe-add").click();
    await ide.showSide("files");
    await ide.page.locator("#f-new").click();
    await ide.answer("two.py");
    await ide.setText("print(2)\n");
    await ide.showSide("projects");
    await ide.page.locator("#pipe-add").click();
    await ide.page.getByRole("button", { name: "Move two.py up" }).click();
    await expect(ide.page.locator("#pipe-list li").first()).toContainText("two.py");
    await ide.open();
    await ide.showSide("projects");
    await expect(ide.page.locator("#pipe-list li").first()).toContainText("two.py");
    await ide.page.getByRole("button", { name: "Remove two.py from the pipeline" }).click();
    await expect(ide.page.locator("#pipe-list li")).toHaveCount(1);
  });

  test("the table says which languages merge several files", async ({ ide }) => {
    await ide.fresh();
    await ide.showSide("projects");
    await ide.page.locator(".pipe-support summary").click();
    await expect(ide.page.locator("#pipe-support tr", { hasText: "Python" })).toContainText("merged");
    await expect(ide.page.locator("#pipe-support tr", { hasText: "F#" })).toContainText("entry only");
  });
});
