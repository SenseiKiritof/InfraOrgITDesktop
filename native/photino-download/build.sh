#!/usr/bin/env bash
# Build a universal (arm64 + x86_64) Photino.Native.dylib from the exact
# upstream v4.0.22 tag with the macOS download patch and the opt-in compile
# flag enabled.
#
# Output: native/photino-download/dist/Photino.Native.dylib
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PATCH="$ROOT/patches/photino-native-4.0.22-macos-downloads.patch"
REPO="${PHOTINO_NATIVE_REPO:-https://github.com/tryphotino/Photino.Native.git}"
TAG="v4.0.22"
COMMIT="3ba4b937d6337b5c58344445db08a5a9a63f07c8"
WORK="${PHOTINO_NATIVE_WORK:-$ROOT/build}"
TARGET="${MACOSX_DEPLOYMENT_TARGET:-13.0}"
DIST="$ROOT/dist"
DYLIB_NAME="Photino.Native.dylib"

for tool in git make c++ lipo otool xcrun shasum; do
    command -v "$tool" >/dev/null 2>&1 || {
        echo "missing required tool: $tool" >&2
        exit 2
    }
done

if [[ "$(uname -s)" != "Darwin" ]]; then
    echo "this patch only builds on macOS" >&2
    exit 2
fi

if [[ -n "${PHOTINO_NATIVE_SRC:-}" ]]; then
    SRC="$(cd "$PHOTINO_NATIVE_SRC" && pwd)"
else
    SRC="$WORK/Photino.Native"
    mkdir -p "$WORK"
    if [[ ! -d "$SRC/.git" ]]; then
        echo "cloning $REPO at $TAG"
        git clone --branch "$TAG" --depth 1 "$REPO" "$SRC"
    fi
fi

[[ -d "$SRC/.git" ]] || {
    echo "not a git checkout: $SRC" >&2
    exit 2
}

HEAD="$(git -C "$SRC" rev-parse HEAD)"
if [[ "$HEAD" != "$COMMIT" && "${ALLOW_OTHER_COMMIT:-0}" != "1" ]]; then
    cat >&2 <<EOF
unexpected source commit: $HEAD
expected v4.0.22:          $COMMIT
Use ALLOW_OTHER_COMMIT=1 only after reviewing the upstream diff.
EOF
    exit 2
fi

if git -C "$SRC" apply --reverse --check "$PATCH" >/dev/null 2>&1; then
    echo "patch already applied: $SRC"
else
    git -C "$SRC" apply --check "$PATCH"
    git -C "$SRC" apply "$PATCH"
    echo "patch applied: $SRC"
fi

echo "building universal dylib (minimum macOS $TARGET)"
make -C "$SRC" mac-universal \
    CFLAGS="-std=c++2a -Wall -O2 -shared -fPIC -mmacosx-version-min=$TARGET -DPHOTINO_MAC_DOWNLOADS=1"

BUILT="$SRC/lib/x64/$DYLIB_NAME"
[[ -f "$BUILT" ]] || {
    echo "build did not produce $BUILT" >&2
    exit 2
}

lipo "$BUILT" -verify_arch arm64
lipo "$BUILT" -verify_arch x86_64
mkdir -p "$DIST"
cp -f "$BUILT" "$DIST/$DYLIB_NAME"

MINOS="$(otool -l "$DIST/$DYLIB_NAME" | awk '/LC_BUILD_VERSION/{found=1} found && /minos/{print $2; exit}')"
SIZE="$(stat -f '%z' "$DIST/$DYLIB_NAME")"
HASH="$(shasum -a 256 "$DIST/$DYLIB_NAME" | awk '{print $1}')"

echo
echo "artifact: $DIST/$DYLIB_NAME"
echo "arches:   $(lipo -archs "$DIST/$DYLIB_NAME")"
echo "minos:    ${MINOS:-unknown}"
echo "size:     $SIZE bytes"
echo "sha256:   $HASH"
echo
echo "Next: test/run.sh (uses this artifact), then wire it behind a macOS-only feature flag."
