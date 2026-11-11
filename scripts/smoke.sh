#!/usr/bin/env bash
# Headless startup smoke test for the desktop app. Linux only: it needs Xvfb
# and the WebKitGTK runtime used by Photino.
#
# Build first, then run:
#   dotnet build InfraOrgITDesktop.slnx -warnaserror
#   scripts/smoke.sh
set -euo pipefail

cd "$(dirname "$0")/.."

hold="${SMOKE_HOLD_SECONDS:-8}"

for tool in xvfb-run timeout; do
    if ! command -v "$tool" >/dev/null; then
        echo "Missing $tool: this smoke test only runs on Linux with Xvfb installed." >&2
        exit 2
    fi
done

# Software rendering: the CI runner has no GPU and no DRI3, and these keep
# WebKitGTK from failing the EGL/DMABUF setup before the window appears.
export GDK_BACKEND="${GDK_BACKEND:-x11}"
export WEBKIT_DISABLE_COMPOSITING_MODE="${WEBKIT_DISABLE_COMPOSITING_MODE:-1}"
export WEBKIT_DISABLE_DMABUF_RENDERER="${WEBKIT_DISABLE_DMABUF_RENDERER:-1}"

log="$(mktemp)"
trap 'rm -f "$log"' EXIT

status=0
# timeout returns 124 when the app is still running after the hold, which is
# exactly the success case: the window stayed up. Any other status means the
# process exited early and the smoke test failed.
xvfb-run -a timeout --signal=TERM --kill-after=5s "${hold}s" \
    dotnet run --project src/InfraOrgIt.Desktop --no-build \
    >"$log" 2>&1 || status=$?

if [[ "$status" -ne 124 ]]; then
    echo "GUI smoke test failed: the app exited before ${hold}s (status $status)." >&2
    cat "$log" >&2
    exit 1
fi

echo "GUI smoke test passed: the app stayed up for ${hold}s."
