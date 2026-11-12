# Photino.Native 4.0.22 — macOS downloads (option B)

**Status: experimental, local-only native patch. Not wired into the desktop
build and not enabled by default.** This directory exists so the desktop
owner can review and opt in behind a macOS-only feature flag.

Upstream: [`tryphotino/Photino.Native`](https://github.com/tryphotino/Photino.Native)
tag `v4.0.22`, commit `3ba4b937d6337b5c58344445db08a5a9a63f07c8`.
License: Apache-2.0 (the patch keeps the upstream license).

## Why

Photino's macOS `NavigationDelegate` only handles certificate challenges. A
top-level navigation to a server response with `Content-Disposition:
attachment` therefore does nothing in WKWebView: no `WKDownload` is created,
no file is saved, and the instance SPA stays on the same page. This is the
path used by the desktop's *native* download route:

```ts
// FileBrowserModal.tsx (native route)
window.location.assign(serverFileDownloadUrl(serverId, path));
```

The API ticket endpoint answers with `Content-Disposition: attachment`
(`DesktopDownloadEndpoints.DownloadAsync`), so the missing piece is entirely
in the macOS webview delegate.

## What the patch does

Only `Photino.Mac.NavigationDelegate.{h,mm}` change. The code is compiled only
with `-DPHOTINO_MAC_DOWNLOADS=1`; a normal `make mac-universal` build keeps the
upstream behaviour (verified by compiling with the flag off).

1. `webView:decidePolicyForNavigationAction:decisionHandler:`
   returns `WKNavigationActionPolicyDownload` when WebKit marks the action as a
   download (`shouldPerformDownload`, e.g. `<a download>`).
2. `webView:decidePolicyForNavigationResponse:decisionHandler:`
   returns the download policy for a **main-frame** response when:
   - `Content-Disposition` is `attachment` (case-insensitive, first token), or
   - the MIME type is `application/octet-stream`, or
   - `canShowMIMEType` is `NO`.
   Everything else returns `Allow`, so normal SPA navigation is unchanged.
3. `webView:navigationAction:didBecomeDownload:` and
   `webView:navigationResponse:didBecomeDownload:` attach a
   `WKDownloadDelegate`.
4. The delegate picks a destination:
   - default: native `NSSavePanel`, suggested name from WebKit, starting in
     `~/Downloads`; cancel cancels the download;
   - `PHOTINO_DOWNLOAD_DIR` in the environment: unattended save into that
     directory with a unique name (`file (1).ext`). This is for smoke tests and
     managed deployments, not a user-facing setting.
5. `downloadDidFinish:` / `download:didFailWithError:resumeData:` log to stderr
   with a `[photino-download]` prefix and release the delegate.
6. No exported C ABI, no managed Photino.NET API and no other source file
   changes. The patched dylib is drop-in for Photino.Native 4.0.22.

Minimum macOS for the produced dylib is **13.0** (matches the desktop app's
`LSMinimumSystemVersion`). The APIs used are available since macOS 11.3.

## Blob-URL limitation

This patch targets HTTP(S) attachment responses. Blob URLs do not have an HTTP
response and are not covered by the response policy.

**Bottom line: this patch does not add blob support. Do not use it for
`blob:` URLs, and keep the existing desktop blob bridge for generated files.**

- Direct `location.href = URL.createObjectURL(blob)` is **not** downloaded.
  Verified on macOS 27: no delegate callback and no file.
- An `<a download href="blob:…">` click **did** download on macOS 27 through
  the action policy (WebKit synthesised a response). This is OS/WebKit
  dependent and must not be relied on for the app's supported macOS range.
- Therefore the app's existing `desktop.download.blob` bridge remains the
  reliable path for generated data (API results, CSV exports, clipboard
  downloads). Do not replace it with the native route.

The native route is a good fit for server files and signed ticket URLs, which
are real HTTP responses with `Content-Disposition: attachment`.

## Build

Requirements: macOS, Xcode command line tools with the WKDownload API, `git`,
`make`, `dotnet` only for the harness.

```sh
cd native/photino-download   # path relative to InfraOrgITDesktop
./build.sh
```

`build.sh`:

- clones `v4.0.22` into `build/Photino.Native` (or uses `PHOTINO_NATIVE_SRC`);
- refuses a different commit unless `ALLOW_OTHER_COMMIT=1`;
- applies `patches/photino-native-4.0.22-macos-downloads.patch` idempotently;
- runs `make mac-universal` with
  `-mmacosx-version-min=13.0 -DPHOTINO_MAC_DOWNLOADS=1`;
- verifies both slices with `lipo`;
- writes `dist/Photino.Native.dylib` and prints minos, size and SHA-256.

The upstream `make mac-universal` target emits four harmless `json.hpp`
deprecation warnings with current clang; the patch adds none.

Artifact produced by this run (local, git-ignored):

```
path:   native/photino-download/dist/Photino.Native.dylib
arches: x86_64 arm64
minos:  13.0
size:   498584 bytes
sha256: 7bd012e1cf9578b2e548bb0c9d6822c8e8d480bcd9e2b3f820341282ae05875e
```

The SHA changes with the Xcode/SDK used; do not treat it as a release hash.
The binary is intentionally not committed: the repo has no native-asset
convention, and this is an experiment. `dist/` is ignored.

## Test

The disposable harness uses **Photino.NET 4.0.16**, the same managed package as
the desktop app. It serves a normal page, clicks an `<a download>` link, then
navigates to an `attachment` response, and asserts both files and their
contents.

```sh
cd native/photino-download
./test/run.sh
```

Expected output (excerpt from the real run):

```
[photino-download] navigation action requests download
[photino-download] saving inline.txt
[photino-download] finished download
[photino-download] attachment navigation response becomes download (report.txt)
[photino-download] saving report.txt
[photino-download] finished download
RESULT: PASS (inline anchor and attachment saved)
```

The native log deliberately omits request URLs: signed desktop download tickets
are embedded in the path. It logs only the suggested file name and errors.
WebKit on macOS 27 also prints a `sandbox_extension_issue_file failed` line;
the stock dylib prints it too and it does not block the save.

The same harness against the stock NuGet dylib (`test/run.sh
<stock-dylib>`) reaches `/`, `/inline.txt` and `/attachment` on the server but
saves nothing and exits `2`:

```
RESULT: FAIL (files not saved before timeout)
server requests: /, /inline.txt, /attachment
```

Tested on macOS 27.0.1 (Xcode 27.1, SDK 27), Apple Silicon. The universal
x86_64 slice was built and lipo-verified but not executed under Rosetta.
The `NSSavePanel` branch was compile-checked and the unattended
`PHOTINO_DOWNLOAD_DIR` branch was exercised; the interactive panel still needs
a manual click-through on the target macOS versions.

Integration resolver rehearsal (disposable harness, not committed): with the
stock NuGet native asset untouched, a
`NativeLibrary.SetDllImportResolver(typeof(PhotinoWindow).Assembly, …)` that
loads this dylib from a local `native/` directory produced the same PASS. That
is the recommended opt-in mechanism.

## Integration note — opt-in, macOS only

Do **not** flip the app's capability globally. The existing
`DownloadManager.NativeDownloadAvailable` is a `const false` and is owned by the
desktop agent; enabling it on Windows/Linux would advertise a route that this
patch does not implement.

1. Ship `Photino.Native.dylib` next to the macOS app (for example
   `Contents/MacOS/native/Photino.Native.dylib` or a `native/` folder under the
   publish directory). Keep it in the distribution only for macOS.
2. Register a P/Invoke resolver before the first `PhotinoWindow` is created,
   behind an explicit feature flag and platform check:

   ```csharp
   using System.Reflection;
   using System.Runtime.InteropServices;
   using Photino.NET;

   internal static class PatchedPhotinoNative
   {
       public static bool TryRegister(string path)
       {
           if (!OperatingSystem.IsMacOS() || !File.Exists(path))
               return false;

           NativeLibrary.SetDllImportResolver(
               typeof(PhotinoWindow).Assembly,
               (name, _, _) => name == "Photino.Native"
                   ? NativeLibrary.Load(path)
                   : IntPtr.Zero);
           return true;
       }
   }
   ```

   Call it once, before `new PhotinoWindow()`. `SetDllImportResolver` can only
   be called once per assembly. The resolver rehearsal above verifies this path
   with Photino.NET 4.0.16.
3. Only when registration succeeded and the feature flag is on, advertise the
   native route (`NativeDownloadAvailable` or a macOS-specific capability).
   Keep `NativeDownloadAvailable = false` as the default. This is the only
   change needed on the web side: `resolveDesktopDownloadRoute` already returns
   `native` and `FileBrowserModal` already navigates to the URL.
4. Keep the bridge route for blob/API result downloads. The native route saves
   server attachments only.
5. Signing: the locally built dylib is unsigned. When bundling, sign it with
   the Developer ID and hardened runtime before the app is signed, then verify
   with `codesign --verify --deep --strict` and notarize the whole app as
   usual. `publish.sh` currently signs with `--deep`; adding an explicit dylib
   signature is safer and reviewable.
6. Single-file publish: the app uses
   `PublishSingleFile`/`IncludeNativeLibrariesForSelfExtract`. The resolver
   approach is deliberate: copying a file over the NuGet asset before bundling
   requires removing the resolved native item from MSBuild and needs its own
   publish verification. Copy the local dylib next to the executable instead.

### UX trade-off

Downloads that take the native route show a native save panel and then save
silently; there is no in-app progress/cancel UI and no "reveal in Finder".
The managed bridge still has that UX. If native route becomes the default, a
future patch revision should forward `NSProgress`/finish events to the host
(for example through a new `Photino` callback) so the manager UI can show
progress. This patch deliberately keeps the native ABI unchanged.

## Risks / maintenance

- **Fork drift.** Upstream `NavigationDelegate.mm` is small but not API-stable.
  Rebase the patch on every Photino.Native bump and rerun the harness.
- **SDK enum rename.** SDK 27 spells the response policy
  `WKNavigationResponsePolicyDownload`; older SDKs use
  `WKNavigationResponsePolicyBecomeDownload`. The patch uses the new name when
  available and the numeric value `2` otherwise. Recheck on the CI Xcode.
- **OS coverage.** Built and run only on macOS 27. WKDownload exists since
  11.3, but blob behaviour and save-panel behaviour need checks on macOS 13,
  14 and 15, plus Intel/Rosetta.
- **No progress/resume/cancel UI.** Cancel is only the save panel's Cancel;
  interrupted downloads are not resumed. Overwrite removes the chosen existing
  file after the panel confirms, then starts a fresh download; a failure after
  that point loses the old file. Browsers avoid this with a temp file and
  rename; a follow-up can do the same.
- **Environment override.** `PHOTINO_DOWNLOAD_DIR` bypasses the panel. It is
  intended for tests/managed installs; do not expose it as a user preference
  without validating the path and permissions.
- **Not a security boundary.** The webview can request any download the
  existing page can navigate to. The API's ticket/auth checks remain the
  authorization boundary.
- **Signing/notarization.** An unsigned or ad-hoc-signed dylib inside a
  Developer ID app fails notarization/stapling checks. Treat it as app code.
- **License.** Apache-2.0; keep the upstream license notice when distributing.

## Files

```
native/photino-download/
  README.md
  build.sh
  patches/photino-native-4.0.22-macos-downloads.patch
  test/DownloadHarness.csproj
  test/Program.cs
  test/run.sh
  dist/Photino.Native.dylib        (ignored, produced by build.sh)
```

## Next step

Review the patch, run `build.sh` + `test/run.sh` on the desktop CI macOS image
and on the oldest supported macOS, then decide whether to:
1. ship the dylib + resolver behind a macOS-only flag and enable the native
   route, or
2. keep the managed bridge as the only route and archive this experiment.
