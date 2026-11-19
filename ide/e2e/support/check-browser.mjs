// Exit 0 when Playwright's Chromium is installed, 3 (with the command that installs it) when it is not.
// tools/ci/fsh_ci.py runs this to decide between running the browser tests and skipping them with a warning.
import fs from "node:fs";
import { chromium } from "@playwright/test";

const exe = chromium.executablePath();
if (exe && fs.existsSync(exe)) {
  console.log("chromium: " + exe);
  process.exit(0);
}
console.log("Playwright's Chromium is not installed. Install it with: cd ide/e2e && npx playwright install chromium");
process.exit(3);
