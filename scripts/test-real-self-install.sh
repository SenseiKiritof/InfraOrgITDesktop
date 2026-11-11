#!/usr/bin/env bash
# Real-bundle self-install test for macOS.
#
# Builds two published app bundles with scripts/publish.sh in temporary
# DIST_ROOT folders, copies the old one into a disposable "Applications"
# folder, and runs UpdateInstallerRealBundleTests against the real generated
# helper. The helper launch is replaced by a recorder and the app binary is
# killed as soon as the helper has proven it waits for the PID, so no GUI
# session is required and nothing is installed in /Applications.
#
# Run from anywhere:
#   scripts/test-real-self-install.sh
#
# Optional overrides: RID (defaults to the host architecture), OLD_VERSION,
# NEW_VERSION. This complements the fake-layout tests in `dotnet test`; it is
# not part of CI because it publishes two full bundles.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

if [ "$(uname -s)" != "Darwin" ]; then
  echo "This test only runs on macOS." >&2
  exit 2
fi

case "$(uname -m)" in
  arm64) DEFAULT_RID=osx-arm64 ;;
  x86_64) DEFAULT_RID=osx-x64 ;;
  *)
    echo "Unsupported macOS architecture: $(uname -m)" >&2
    exit 2
    ;;
esac
RID="${RID:-$DEFAULT_RID}"
case "$RID" in
  osx-*) ;;
  *)
    echo "RID must be osx-arm64 or osx-x64 on macOS, got: $RID" >&2
    exit 2
    ;;
esac

OLD_VERSION="${OLD_VERSION:-0.1.0}"
NEW_VERSION="${NEW_VERSION:-0.0.2-test}"
if [ "$OLD_VERSION" = "$NEW_VERSION" ]; then
  echo "OLD_VERSION and NEW_VERSION must differ." >&2
  exit 2
fi

for tool in /bin/sh /usr/bin/ditto /usr/libexec/PlistBuddy; do
  if [ ! -x "$tool" ]; then
    echo "Missing required tool: $tool" >&2
    exit 2
  fi
done

TMP="$(mktemp -d "${TMPDIR:-/tmp}/infraorgit-real-self-install.XXXXXX")"
TMP="$(cd "$TMP" && pwd -P)"
cleanup() {
  # Kill anything still running from the disposable tree before deleting it.
  for pid in $(pgrep -f "$TMP" 2>/dev/null || true); do
    kill "$pid" 2>/dev/null || true
  done
  rm -rf "$TMP"
}
trap cleanup EXIT

APPS="$TMP/Applications"
RESTORE_APPS="$TMP/Applications-restore"
OLD_APP="$TMP/dist-old/InfraOrgIT Desktop.app"
NEW_APP="$TMP/dist-new/InfraOrgIT Desktop.app"
NEW_ARCHIVE="$TMP/dist-new/InfraOrgIT-Desktop-$NEW_VERSION-$RID.zip"
TARGET_APP="$APPS/InfraOrgIT Desktop.app"
RESTORE_APP="$RESTORE_APPS/InfraOrgIT Desktop.app"

echo "Temp root: $TMP"
echo "Building published $OLD_VERSION bundle with scripts/publish.sh $RID..."
VERSION="$OLD_VERSION" DIST_ROOT="$TMP/dist-old" \
  "$ROOT/scripts/publish.sh" "$RID"

echo "Building published $NEW_VERSION update bundle with scripts/publish.sh $RID..."
VERSION="$NEW_VERSION" DIST_ROOT="$TMP/dist-new" \
  "$ROOT/scripts/publish.sh" "$RID"

for app in "$OLD_APP" "$NEW_APP"; do
  if [ ! -d "$app/Contents/MacOS" ]; then
    echo "Published bundle missing: $app" >&2
    exit 1
  fi
  identifier="$(/usr/libexec/PlistBuddy \
    -c 'Print :CFBundleIdentifier' "$app/Contents/Info.plist")"
  if [ "$identifier" != "it.infraorgit.desktop" ]; then
    echo "Unexpected CFBundleIdentifier in $app: $identifier" >&2
    exit 1
  fi
done
if [ ! -f "$NEW_ARCHIVE" ]; then
  echo "Published update archive missing: $NEW_ARCHIVE" >&2
  exit 1
fi
old_built_version="$(/usr/libexec/PlistBuddy \
  -c 'Print :CFBundleShortVersionString' "$OLD_APP/Contents/Info.plist")"
new_built_version="$(/usr/libexec/PlistBuddy \
  -c 'Print :CFBundleShortVersionString' "$NEW_APP/Contents/Info.plist")"
if [ "$old_built_version" != "$OLD_VERSION" ] ||
   [ "$new_built_version" != "$NEW_VERSION" ]; then
  echo "Published versions do not match the request: " \
       "old=$old_built_version new=$new_built_version" >&2
  exit 1
fi

# The old bundle is replaced in a disposable folder, never in /Applications.
mkdir -p "$APPS" "$RESTORE_APPS"
/usr/bin/ditto "$OLD_APP" "$TARGET_APP"
/usr/bin/ditto "$OLD_APP" "$RESTORE_APP"

echo "Building and running the real-bundle tests..."
dotnet build "$ROOT/InfraOrgITDesktop.slnx" -warnaserror --nologo

export INFRAORGIT_REAL_OLD_APP="$TARGET_APP"
export INFRAORGIT_REAL_RESTORE_APP="$RESTORE_APP"
export INFRAORGIT_REAL_NEW_ARCHIVE="$NEW_ARCHIVE"
export INFRAORGIT_REAL_OLD_VERSION="$OLD_VERSION"
export INFRAORGIT_REAL_NEW_VERSION="$NEW_VERSION"
dotnet test "$ROOT/InfraOrgITDesktop.slnx" --no-build --nologo \
  --filter "FullyQualifiedName~UpdateInstallerRealBundleTests" \
  --logger "console;verbosity=normal"

final_version="$(/usr/libexec/PlistBuddy \
  -c 'Print :CFBundleShortVersionString' "$TARGET_APP/Contents/Info.plist")"
final_marker="$(cat "$TARGET_APP/Contents/real-self-install-marker.txt" 2>/dev/null || true)"
restored_version="$(/usr/libexec/PlistBuddy \
  -c 'Print :CFBundleShortVersionString' "$RESTORE_APP/Contents/Info.plist")"
restored_marker="$(cat "$RESTORE_APP/Contents/real-self-install-marker.txt" 2>/dev/null || true)"

# Independent checks: dotnet test skips without the variables, so a silent
# skip must fail here instead of passing with the old bundle unchanged.
if [ "$final_version" != "$NEW_VERSION" ] ||
   [ "$final_marker" != "new-published-bundle" ]; then
  echo "The Applications bundle was not replaced (version=$final_version " \
       "marker=$final_marker)." >&2
  exit 1
fi
if [ "$restored_version" != "$OLD_VERSION" ] ||
   [ "$restored_marker" != "old-published-bundle" ]; then
  echo "The failed copy did not restore the old bundle " \
       "(version=$restored_version marker=$restored_marker)." >&2
  exit 1
fi
if find "$APPS" "$RESTORE_APPS" -maxdepth 2 -name '*.old.*' -print -quit |
   grep -q .; then
  echo "The helper left an old bundle backup behind." >&2
  exit 1
fi

echo
echo "Real self-install test passed."
echo "  replace:  $OLD_VERSION -> $final_version ($final_marker)"
echo "  relaunch: recorder called with the Applications bundle path"
echo "  cleanup:  old backup and helper work directory removed"
echo "  restore:  failed copy kept $restored_version ($restored_marker)"
