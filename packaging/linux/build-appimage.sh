#!/usr/bin/env bash
# Wraps the unpacked desktop archive in an AppImage. A person runs this (packaging/README.md); the pipeline never does.
# usage: build-appimage.sh <unpacked-archive-folder> <version> <output.AppImage>
# Needs `appimagetool` on PATH (downloaded and checked by the person). Does not sign: pass --sign to appimagetool yourself
# with a key you own. Nothing is uploaded.
set -euo pipefail
src="${1:?unpacked archive folder}"; version="${2:?version}"; out="${3:?output file}"
here="$(cd "$(dirname "$0")" && pwd)"
appdir="$(mktemp -d)/FSharpened.AppDir"
mkdir -p "$appdir/usr/bin" "$appdir/usr/share/applications" "$appdir/usr/share/metainfo"
cp -a "$src/." "$appdir/usr/bin/"
sed "s/{{VERSION}}/$version/; s/{{DATE}}/$(date -u +%F)/" "$here/fsharpened.metainfo.xml" > "$appdir/usr/share/metainfo/io.fsharpened.desktop.appdata.xml"
cp "$here/fsharpened.desktop" "$appdir/usr/share/applications/fsharpened.desktop"
cp "$here/fsharpened.desktop" "$appdir/fsharpened.desktop"
cp "$here/../../branding/icons/icon-512.png" "$appdir/fsharpened.png"
cat > "$appdir/AppRun" <<'RUN'
#!/bin/sh
here="$(dirname "$(readlink -f "$0")")"
exec "$here/usr/bin/FSharpened.Ide.Desktop" "$@"
RUN
chmod +x "$appdir/AppRun" "$appdir/usr/bin/FSharpened.Ide.Desktop"
appimagetool "$appdir" "$out"
