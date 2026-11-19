// Not a test of behaviour: with E2E_SCREENSHOTS=1 it saves pictures of the built page (a multi-file run and a pipeline run,
// in a light and a dark theme) under artifacts/ide-e2e/screenshots, so a person can look at what the tests drove.
import path from "node:path";
import { fileURLToPath } from "node:url";
import { test, expect, okRun } from "../support/fixtures.mjs";

const out = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../../../artifacts/ide-e2e/screenshots");

test.skip(!process.env.E2E_SCREENSHOTS, "set E2E_SCREENSHOTS=1 to save screenshots");

test("screenshots of a multi-file run and a pipeline run in two themes", async ({ ide }) => {
  await ide.page.setViewportSize({ width: 1280, height: 800 });
  await ide.fresh();
  await ide.showSide("projects");
  await ide.page.locator("#p-new").click();
  await ide.answer("Words");
  await ide.showSide("files");
  for (const [file, text] of Object.entries({
    "words.py": "def shout(s):\n    return s.upper()\n",
    "main.py": "import words\nprint(words.shout(input()))\n",
    "count.js": "const s = require('fs').readFileSync(0, 'utf8');\nconsole.log(s.trim().length + ' letters');\n",
  })) {
    await ide.page.locator("#f-new").click();
    await ide.answer(file);
    await ide.setText(text);
  }
  await ide.page.locator("#stdin, .ptabs [data-p=stdin]").first().click();
  await ide.tab("main.py").click();
  await ide.showSide("projects");
  await ide.page.locator("#pipe-add").click();
  await ide.tab("count.js").click();
  await ide.page.locator("#pipe-add").click();
  ide.gateway.reply = (body, n) => ({ status: 200, body: okRun(n === 1 ? "HELLO\n" : "5 letters\n") });
  await ide.page.locator("#pipe-run").click();
  await expect(ide.page.locator("#steps li.ok")).toHaveCount(2);
  for (const theme of ["light", "dracula"]) {
    await ide.page.locator("#btn-settings").click();
    await ide.page.locator("#set-theme").selectOption(theme);
    await ide.page.keyboard.press("Escape");
    await ide.page.screenshot({ path: path.join(out, `ide-pipeline-${theme}.png`) });
  }
});
