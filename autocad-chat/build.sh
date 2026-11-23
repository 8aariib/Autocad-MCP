#!/usr/bin/env bash
# Builds dist/ClaudeForAutoCAD-<version>.zip: the AutoCAD bundle, the bridge, the installer.
# Needs the .NET 8 SDK and uv. Runs on Linux, macOS or Windows (Git Bash).
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
version="1.0.0"
out="$here/dist/ClaudeForAutoCAD"
bundle="$out/AutoCADChat.bundle"

echo "==> tests (bridge)"
(cd "$here/bridge" && uv run --frozen --extra test pytest -q -p no:cacheprovider)

echo "==> build (plugin)"
dotnet build "$here/plugin/AutoCADChat.csproj" -c Release -nologo -v quiet

echo "==> assemble"
rm -rf "$here/dist"
mkdir -p "$bundle/Contents/bridge"
cp "$here/bundle/PackageContents.xml" "$bundle/"
cp "$here/plugin/bin/Release/net8.0-windows/AutoCADChat.dll" "$bundle/Contents/"
(cd "$here/bridge" && cp pyproject.toml uv.lock .python-version "$bundle/Contents/bridge/" && \
  tar --exclude='__pycache__' -cf - src | tar -xf - -C "$bundle/Contents/bridge/")
cp "$here/bundle/install.ps1" "$here/bundle/uninstall.ps1" "$here/bundle/diagnose.ps1" "$out/"
cp "$here/README.md" "$out/README.md"
# PowerShell 5.1 reads BOM-less scripts as ANSI; the scripts are ASCII, so check that stays true.
if LC_ALL=C grep -qP '[^\x00-\x7F]' "$out"/*.ps1; then
  echo "install scripts must be ASCII" >&2; exit 1
fi
(cd "$here/dist" && rm -f "ClaudeForAutoCAD-$version.zip" && zip -qr "ClaudeForAutoCAD-$version.zip" ClaudeForAutoCAD)
echo "==> dist/ClaudeForAutoCAD-$version.zip"
unzip -l "$here/dist/ClaudeForAutoCAD-$version.zip" | tail -1
