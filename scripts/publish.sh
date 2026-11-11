#!/usr/bin/env bash
# Publish a self-contained desktop build and package it under dist/.
# macOS signing is opt-in:
#   CODESIGN_IDENTITY="Developer ID Application: ..." scripts/publish.sh osx-arm64
# The publish workflow sets CODESIGN_IDENTITY after importing the Developer ID
# certificate, then notarizes and staples the .app. Without the variable the
# build stays unsigned, which is what local runs and forks get.
# Set DIST_ROOT to write the build elsewhere (used by the real self-install
# test): DIST_ROOT=/tmp/build scripts/publish.sh osx-arm64.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RID="${1:-}"
if [ -z "$RID" ]; then
  case "$(uname -s)-$(uname -m)" in
    Darwin-arm64) RID=osx-arm64 ;;
    Darwin-x86_64) RID=osx-x64 ;;
    Linux-x86_64) RID=linux-x64 ;;
    MINGW*|MSYS*|CYGWIN*) RID=win-x64 ;;
    *) echo "RID non riconosciuto; passalo come argomento." >&2; exit 2 ;;
  esac
fi

VERSION="${VERSION:-0.1.0}"
DIST_ROOT="${DIST_ROOT:-$ROOT/dist}"
OUT="$DIST_ROOT/$RID"
ARCHIVE_BASE="$DIST_ROOT/InfraOrgIT-Desktop-$VERSION-$RID"
rm -rf "$OUT" "$ARCHIVE_BASE" "$ARCHIVE_BASE.zip" "$ARCHIVE_BASE.tar.gz"

dotnet publish "$ROOT/src/InfraOrgIt.Desktop/InfraOrgIt.Desktop.csproj" \
  -c Release -r "$RID" --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:Version="$VERSION" \
  -o "$OUT"

if [ "$(uname -s)" = "Darwin" ] && [[ "$RID" == osx-* ]]; then
  APP="$DIST_ROOT/InfraOrgIT Desktop.app"
  rm -rf "$APP"
  mkdir -p "$APP/Contents/MacOS"
  cp -R "$OUT/." "$APP/Contents/MacOS/"
  cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>InfraOrgIT Desktop</string>
  <key>CFBundleDisplayName</key><string>InfraOrgIT Desktop</string>
  <key>CFBundleIdentifier</key><string>it.infraorgit.desktop</string>
  <key>CFBundleExecutable</key><string>InfraOrgIt.Desktop</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>CFBundleVersion</key><string>1</string>
  <key>LSMinimumSystemVersion</key><string>13.0</string>
  <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
PLIST

  if [ -n "${CODESIGN_IDENTITY:-}" ]; then
    codesign --force --deep --options runtime --timestamp \
      --sign "$CODESIGN_IDENTITY" "$APP"
  fi

  ditto -c -k --keepParent "$APP" "$ARCHIVE_BASE.zip"
  echo "Bundle: $APP"
  echo "Archivio: $ARCHIVE_BASE.zip"
else
  cp -R "$OUT" "$ARCHIVE_BASE"
  case "$RID" in
    win-*)
      if command -v powershell.exe >/dev/null 2>&1; then
        powershell.exe -NoProfile -Command \
          "Compress-Archive -LiteralPath '$(cygpath -w "$ARCHIVE_BASE")' -DestinationPath '$(cygpath -w "$ARCHIVE_BASE.zip")' -Force"
        echo "Archivio: $ARCHIVE_BASE.zip"
      elif command -v zip >/dev/null 2>&1; then
        (cd "$DIST_ROOT" && zip -qr "$(basename "$ARCHIVE_BASE").zip" "$(basename "$ARCHIVE_BASE")")
        echo "Archivio: $ARCHIVE_BASE.zip"
      else
        echo "Serve powershell.exe o zip per creare l'archivio Windows." >&2
        exit 2
      fi
      ;;
    *)
      tar -czf "$ARCHIVE_BASE.tar.gz" -C "$DIST_ROOT" "$(basename "$ARCHIVE_BASE")"
      echo "Archivio: $ARCHIVE_BASE.tar.gz"
      ;;
  esac
  rm -rf "$ARCHIVE_BASE"
  echo "Pubblicato: $OUT"
fi
