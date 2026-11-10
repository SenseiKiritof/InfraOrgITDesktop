#!/usr/bin/env bash
# Publish a self-contained desktop build. Signing is opt-in:
#   CODESIGN_IDENTITY="Developer ID Application: ..." scripts/publish.sh osx-arm64
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
OUT="$ROOT/dist/$RID"
rm -rf "$OUT"

dotnet publish "$ROOT/src/InfraOrgIt.Desktop/InfraOrgIt.Desktop.csproj" \
  -c Release -r "$RID" --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -o "$OUT"

if [ "$(uname -s)" = "Darwin" ] && [[ "$RID" == osx-* ]]; then
  APP="$ROOT/dist/InfraOrgIT Desktop.app"
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
    codesign --force --deep --options runtime \
      --sign "$CODESIGN_IDENTITY" "$APP"
  fi

  ditto -c -k --keepParent "$APP" \
    "$ROOT/dist/InfraOrgIT-Desktop-$VERSION-$RID.zip"
  echo "Bundle: $APP"
  echo "Archivio: $ROOT/dist/InfraOrgIT-Desktop-$VERSION-$RID.zip"
else
  echo "Pubblicato: $OUT"
fi
