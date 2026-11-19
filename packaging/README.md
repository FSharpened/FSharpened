# Desktop packaging inputs (ADR-0023)

What is here: plain descriptions an installer tool reads. What is **not** here: any installer, signature, certificate, secret, store account or upload. Those are **human gates** (AGENTS.md: never publish; ADR-0023 item 9). Nothing in this folder is run by CI.

## What the pipeline already gives you
`python tools/ci/fsh_ci.py run --profile release` writes `artifacts/release/<commit>-<rid>/desktop/fsharpened-desktop-<version>-<rid>.<zip|tar.gz>`: the self-contained desktop app (language packs, grammars, the Rust libraries it loads), smoke-run with `--selftest`, **unsigned**. `fsh_ci.py verify` checks it against `SHA256SUMS`. The files below describe how to wrap that same folder.

| Platform | Input here | Tool a person runs | Human gate before anyone can install it without a warning |
|---|---|---|---|
| Windows (MSI) | `windows/FSharpened.wxs` | WiX Toolset 5 (`wix build`) | Authenticode code-signing certificate (OV/EV or Azure Trusted Signing); SmartScreen reputation |
| Windows (MSIX) | `windows/AppxManifest.xml` | `makeappx pack`, `signtool sign` | A code-signing certificate whose subject equals `Identity/Publisher`; Microsoft Partner Center account for the Store |
| Linux (AppImage / `.desktop`) | `linux/fsharpened.desktop`, `linux/fsharpened.metainfo.xml`, `linux/build-appimage.sh` | `appimagetool` (downloaded by the person) | Optional GPG signature; Flathub/Snap submissions need their own accounts |
| macOS | `macos/Info.plist` | `codesign`, `notarytool`, `stapler` | Apple Developer Program membership, Developer ID certificate, notarisation. **macOS is deferred** in this project; this file is a starting point only |

## Steps for a person (none of them is automated or attempted by the pipeline)
1. Build and verify the bundle on the machine of the target RID: `python tools/ci/fsh_ci.py run --profile release`, then `python tools/ci/fsh_ci.py verify`. Or download the bundles the manual workflow `release-dry-run.yml` uploads and run `verify` on each.
2. Unpack `desktop/fsharpened-desktop-*.zip|tar.gz`. The folder holds `FSharpened.Ide.Desktop(.exe)` and everything it loads.
3. Fill the placeholders in the description for your platform (`{{VERSION}}` is the `<Version>` of `Directory.Build.props`, `{{SOURCE_DIR}}` the unpacked folder). WiX needs a four-part numeric version; `0.0.1-skeleton` becomes `0.0.1.0`.
4. Build the installer with the tool in the table.
5. **Sign** it with a certificate you own, in your own signing environment. The pipeline never sees a certificate, a password or a token; do not put one in the repository, a workflow or a log.
6. **Notarise** (macOS only) with your Apple account.
7. Smoke-test the installed app: `FSharpened.Ide.Desktop --selftest` must print `"ok":true`; open a folder and press F5 on a small program.
8. Publish only after the maintainer approves (never merged to `main` or uploaded by an agent).

## Known limits of these inputs (checked, not guessed)
- Not built or validated with the real tools here: WiX, MSIX, appimagetool and macOS tooling were not run. The self-tests only check that each file is well formed and names the right executable.
- The app is not trimmed and not NativeAOT (see `docs/operations/desktop.md`), so the folder is about 70 MB compressed.
- `branding/icons/` has PNG and ICO icons; an MSIX needs the logo sizes it names (44, 150) generated from `branding/mark.svg`; that is a person's step.
