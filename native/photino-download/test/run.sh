#!/usr/bin/env bash
# Run the disposable Photino.NET harness against a Photino.Native.dylib.
#
#   ./test/run.sh                    # uses dist/Photino.Native.dylib
#   ./test/run.sh /path/to/stock.dylib
#
# The harness exits 0 when both the <a download> file and the
# Content-Disposition attachment were saved, 2 otherwise. A stock
# Photino.Native.dylib therefore fails by design.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/.." && pwd)"
DYLIB="${1:-$ROOT/dist/Photino.Native.dylib}"
TIMEOUT="${HARNESS_TIMEOUT_SECONDS:-20}"

[[ "$(uname -s)" == "Darwin" ]] || {
    echo "this harness only runs on macOS" >&2
    exit 2
}
[[ -f "$DYLIB" ]] || {
    echo "missing dylib: $DYLIB (run ../build.sh first)" >&2
    exit 2
}

dotnet build "$ROOT/test/DownloadHarness.csproj" -c Release --nologo

OUT="$ROOT/test/bin/Release/net10.0"
for rid in osx-arm64 osx-x64; do
    mkdir -p "$OUT/runtimes/$rid/native"
    cp -f "$DYLIB" "$OUT/runtimes/$rid/native/Photino.Native.dylib"
done

RUN_DIR="$ROOT/test/.run/$(date +%Y%m%d-%H%M%S)"
LOG="$RUN_DIR/harness.log"
mkdir -p "$RUN_DIR"

echo "dylib:  $DYLIB"
echo "arches: $(lipo -archs "$DYLIB")"
echo "log:    $LOG"

set +e
PHOTINO_DOWNLOAD_DIR="$RUN_DIR" HARNESS_TIMEOUT_SECONDS="$TIMEOUT" \
    dotnet "$OUT/DownloadHarness.dll" 2>&1 | tee "$LOG"
STATUS="${PIPESTATUS[0]}"
set -e

echo "downloads: $RUN_DIR"
ls -l "$RUN_DIR"/*.txt 2>/dev/null || true
exit "$STATUS"
