# Phone ↔ Mac File Transfer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Share files and images from Android to the Mac (share sheet "Send to Mac") and from the Mac to Android (Finder Quick Action "Send to phone" and a drop zone on the settings page), over the existing bridge link.

**Architecture:** Two new switches in `BridgeConfig`. Phone → Mac is one streaming `PUT` per file into `~/Downloads/PhoneBox/`. Mac → phone queues the file in a private outbox, announces it with a `file` event on the existing `/api/bridge/stream`, and the phone pulls it (`GET`, range-capable) and confirms (`POST …/done`). The Mac-side trigger lives on the local port under `/api/transfer/*` (not under `/api/bridge`, never on the Wi-Fi allow-list) and accepts only the settings page (same origin) or a local key read from a 0600 file.

**Tech Stack:** .NET 10 agent (xUnit, Kestrel), Kotlin Android app (minSdk 21, targetSdk 35; JVM checks in `tools/kotlin-tests`), `settings.html` + `i18n.js` locale packs, macOS (`osascript`, `xattr`, launchd, Finder Quick Actions).

**Spec:** `/Volumes/T7 MAC/Code/phonebox/docs/superpowers/specs/2026-11-24-file-transfer-design.md` (untracked in the main folder; the worktree below does not contain it, so read it by that absolute path). Read the spec before any task.

**Working place:** a new worktree cut from the personal branch:
`git -C "/Volumes/T7 MAC/Code/pb-personal" worktree add "/Volumes/T7 MAC/Code/pb-files" -b personal/file-transfer personal/phone-bridge`.
All paths below are relative to `/Volumes/T7 MAC/Code/pb-files`. Never edit `/Volumes/T7 MAC/Code/phonebox` (shared folder) or `pb-personal` (another session works there). Never push, publish a release, or install on the Mac/phone without the owner saying so.

## Global Constraints

- Both new switches default **off**: `BridgeConfig.FilesFromPhone` ("Receive files from the phone"), `BridgeConfig.FilesToPhone` ("Send files to the phone"); JSON names `filesFromPhone`, `filesToPhone`.
- Limits: 2 GiB per file (`FileLimits.MaxFileBytes = 2L << 30`); free space must stay ≥ 1 GiB after the write (`SpaceMarginBytes = 1L << 30`); outbox total 4 GiB (`OutboxTotalBytes = 4L << 30`); outbox TTL 1 hour (`OutboxTtl`); at most 2 concurrent uploads (`MaxConcurrentUploads = 2`, 429 above).
- Received files go to `~/Downloads/PhoneBox/`, written as hidden `.part` then renamed atomically; never overwrite (`name (1).ext`); name ≤ 200 bytes with the extension kept; `com.apple.quarantine` set on every received file; mode 0644.
- Check order on phone-facing endpoints: who (403) → platform (501) → switch (403) → size/space/busy (413/507/429) → body.
- File names and contents are never logged (counts only). The agent never reads a path a caller names.
- Phone-facing paths: `PUT /api/bridge/files/in`, `GET /api/bridge/files/out`, `GET /api/bridge/files/out/{id}`, `POST /api/bridge/files/out/{id}/done`; all on the Wi-Fi allow-list (`Lan.AllowedOverLan`). Local-only: `PUT /api/transfer/send`, `GET /api/transfer/status`; NOT on the allow-list. `IsBridgePath` local refusal stays unchanged.
- Local key header `X-PhoneBox-Transfer-Key`; key file `~/Library/Application Support/PhoneBox/transfer.json` (`{"port":N,"key":"…"}`, mode 0600), present only while `FilesToPhone` is on.
- Share target accepts only `content://` URIs whose authority is not this app's own; confirmation dialog before sending; `filterTouchesWhenObscured`.
- Android: MediaStore from API 29 (images → `Pictures/PhoneBox`, video → `Movies/PhoneBox`, everything else → `Download/PhoneBox`, `IS_PENDING` until complete); below API 29 the app's own `getExternalFilesDir(Environment.DIRECTORY_DOWNLOADS)`. Upload service type `dataSync` (`FOREGROUND_SERVICE_DATA_SYNC`).
- Every new setting needs: default, all three `agent/PhoneBox.Agent/config.default{,.linux,.macos}.json`, settings card, strings in all 23 languages (`tools/check-release.py` enforces), `PRIVACY.md` line. `CHANGELOG.md` is not touched. No Co-Authored-By trailer in commits (owner preference). `python3 tools/check-release.py` stays green.
- Run agent tests with `cd agent && DOTNET_ROLL_FORWARD=Major dotnet test PhoneBox.Agent.Tests --filter "<name>"` (project is net10.0, only .NET 11 installed).
- Live tests: owner present, no screenshots, no adb swipes, demo agent in its own `HOME` with a minimal `PATH` and a config whose `adbPath` does not exist (so it does not take the phone from the installed agent); record the phone's volume/playback state before and restore it.

## Review Focus

1. A hostile name from the phone (`../../.ssh/authorized_keys`, `..`, `a/b\c`, NUL and control characters, 5 000 characters, only dots, emoji) lands as a harmless file inside `~/Downloads/PhoneBox/` or is refused — pinned in Task 2.
2. The phone loses the connection halfway (or the body is shorter/longer than `size`): no visible partial file remains and no `.part` is left — Task 3.
3. `size` missing or lying, chunked body past 2 GiB: the agent stops and deletes at the limit — Task 3.
4. A phone with only a file switch on opens the bridge stream: it must receive no clipboard events and must not start the adb clipboard helper (`AdbReverse.HelperWanted`) — Task 12.
5. A file name from the Mac with spaces, quotes, a newline or a leading `-` reaches the Quick Action's script intact and cannot become a command or a curl option — Task 15.
6. A header-less `curl` to `/api/transfer/send` (no Origin, no key) is refused; the same path through the Wi-Fi/USB ports is refused — Task 11.

---

## Part 0 — Setup and spikes

### Task 0: Worktree and baseline

**Files:** none changed.

- [ ] **Step 1: Create the worktree** with the command in the header. Verify `git -C "/Volumes/T7 MAC/Code/pb-files" log --oneline -1` shows the tip of `personal/phone-bridge`.
- [ ] **Step 2: Baseline.** In the worktree run `cd agent && DOTNET_ROLL_FORWARD=Major dotnet test PhoneBox.Agent.Tests` (expect all pass, ~2965 tests), `python3 tools/check-release.py` (expect `PASS`), `python3 tools/test-bridge-checks.py` (expect `PASS`). Note the counts in your task notes (not in the repo); any failure here is pre-existing and is reported, not fixed.

### Task 1: Spike S1 — can a launchd job write to `~/Downloads`? (throwaway, ask the owner first)

**Files:** scratch only, outside the repo (scratchpad directory). Nothing committed.

- [ ] **Step 1: Ask the owner** before running (it adds and removes a launchd job).
- [ ] **Step 2:** Make a tiny ad-hoc-signed executable (a `dotnet publish`ed console app, or a signed `sh` wrapper is NOT enough: it must be a binary signed ad hoc like `PhoneBoxAgent`) that creates `~/Downloads/PhoneBox-spike/x.txt`, load it as a user LaunchAgent labelled `net.oxog.phonebox.spike`, run it, then `launchctl bootout` and delete the plist and the folder.
- [ ] **Step 3: Record** in the ledger: did the file appear, was a prompt shown, did a rebuilt binary (new ad hoc signature) get a new prompt. **If it failed or prompts every build:** stop and report to the owner with the candidate folders from the spec; the save location is theirs to choose. Tasks 3–4 wait for the answer.

### Task 2a: Spike S2 — a `.workflow` that Finder lists (throwaway, ask the owner first)

**Files:** scratch only.

- [ ] **Step 1:** Hand-write `~/Library/Services/PhoneBox Spike.workflow/Contents/{Info.plist,document.wflow}` for a "Files/Folders in Finder → Run Shell Script (arguments as `"$@"`)" Quick Action that appends its arguments to a file in the scratchpad. Right-click a file in Finder → Quick Actions.
- [ ] **Step 2: Record** whether it appears without opening Automator, whether `pbs -flush`/a Finder relaunch is needed, and whether macOS asks for a permission. Delete the workflow afterwards. **If it cannot be made to work reliably**, Task 15 ships the script plus a guide section only, and the drop zone stays the supported way; report this to the owner.

---

## Part 1 — Agent: receive from the phone

### Task 2: Switches and file-name sanitizer

**Files:**
- Modify: `agent/PhoneBox.Agent/Config.cs` (`BridgeConfig`), `agent/PhoneBox.Agent/config.default.json`, `config.default.linux.json`, `config.default.macos.json`
- Create: `agent/PhoneBox.Agent/ReceivedFileName.cs`, `agent/PhoneBox.Agent/FileLimits.cs`
- Test: `agent/PhoneBox.Agent.Tests/BridgeConfigTests.cs` (extend), `agent/PhoneBox.Agent.Tests/ReceivedFileNameTests.cs`

**Interfaces:**
- Produces: `BridgeConfig.FilesFromPhone`, `BridgeConfig.FilesToPhone` (`bool`, default false); `static class FileLimits { const long MaxFileBytes, SpaceMarginBytes, OutboxTotalBytes; static readonly TimeSpan OutboxTtl; const int MaxConcurrentUploads }` with the Global Constraints values; `static class ReceivedFileName { const int MaxBytes = 200; static string Sanitize(string? raw); static string Unique(string folder, string name, Func<string, bool> exists); }`.

- [ ] **Step 1: Write the failing tests.**
  - `BridgeConfigTests`: `FileSwitchesAreOffByDefault` (`new BridgeConfig()` has both false); `FileSwitchesRoundTripAsCamelCase` (JSON `{"bridge":{"filesFromPhone":true,"filesToPhone":true}}` loads both true); the three default JSON files each contain `"filesFromPhone": false, "filesToPhone": false` (read the files from the source tree, as the existing default-config tests do).
  - `ReceivedFileNameTests`: `Sanitize("../../.ssh/authorized_keys") == "authorized_keys"`; `Sanitize("..") == "file"`; `Sanitize("a/b\\c.txt") == "c.txt"`; `Sanitize("x\0y\n.jpg") == "xy.jpg"`; `Sanitize(null) == "file"`; `Sanitize("....") == "file"`; `Sanitize(".hidden.png") == "hidden.png"`; `Sanitize(new string('a', 5000) + ".jpg")` is ≤ 200 UTF-8 bytes and ends in `.jpg`; `Sanitize("😀😀😀…")` never cuts a surrogate pair (result round-trips through UTF-8); `Unique(folder, "a.txt", n => n == "a.txt")` → `"a (1).txt"`, and with `a (1).txt` also taken → `"a (2).txt"`.
- [ ] **Step 2: Run** `dotnet test PhoneBox.Agent.Tests --filter "ReceivedFileName|BridgeConfig"`; expect FAIL (types missing).
- [ ] **Step 3: Implement** `Sanitize` (take the part after the last `/` or `\`, drop control chars and Unicode line/paragraph separators, trim leading dots and spaces, cut to 200 UTF-8 bytes on a code-point boundary keeping the extension, empty → `"file"`), `Unique`, `FileLimits`, the two config properties with `<summary>` docs, and the two keys in all three default JSON files next to `"clipboard"`.
- [ ] **Step 4: Run** the same filter; expect PASS.
- [ ] **Step 5: Commit** `git add -A agent && git commit -m "Files: the two switches, limits and the received-name sanitizer"`.

### Task 3: `FileReceiver` (streaming writer)

**Files:**
- Create: `agent/PhoneBox.Agent/FileReceiver.cs`
- Test: `agent/PhoneBox.Agent.Tests/FileReceiverTests.cs`

**Interfaces:**
- Consumes: `ReceivedFileName`, `FileLimits` (Task 2).
- Produces: `enum ReceiveStatus { Stored, TooLarge, NoSpace, Busy, SizeMismatch, Aborted, Failed }`; `sealed record ReceiveResult(ReceiveStatus Status, string? Name, long Bytes)`; `internal sealed class FileReceiver(string folder, Func<string, long> freeSpace, Action<string> quarantine, ILogger log)` with `ReceiveStatus? Precheck(long? declaredSize)` (null = go on; also takes one of the 2 upload slots on success, released by `ReceiveAsync`/`Release()`), `Task<ReceiveResult> ReceiveAsync(string? rawName, long? declaredSize, Stream body, CancellationToken ct)`, and `static FileReceiver ForMac(ILogger log)` (folder `~/Downloads/PhoneBox`, free space from `new DriveInfo(root).AvailableFreeSpace`, quarantine via `xattr -w com.apple.quarantine "0081;<hex unix time>;PhoneBox;" <path>` through `ProcessStartInfo.ArgumentList`, never a shell).

- [ ] **Step 1: Write the failing tests** (temp folder, a `Func` free-space fake, a recording `quarantine`):
  - `StoresAFileAndQuarantinesIt` (body "hello", declared 5 → `Stored`, file `hello.txt` content "hello", quarantine called with that path, no `.part` left).
  - `NeverOverwrites` (second file with the same name → `name (1).ext`, first untouched).
  - `RefusesOverTheLimitBeforeReading` (`Precheck(FileLimits.MaxFileBytes + 1)` → `TooLarge`, no slot taken).
  - `RefusesWhenFreeSpaceWouldDropBelowMargin` (free = size + margin − 1 → `NoSpace`; free = size + margin → null).
  - `UnknownSizeNeedsTheMarginFree` (declared null, free < margin → `NoSpace`).
  - `ThirdConcurrentUploadIsBusy` (two `Precheck` calls hold slots; third → `Busy`; after one `ReceiveAsync` finishes the next passes).
  - `ADroppedBodyLeavesNothing` (body stream that throws `IOException` after 3 bytes → `Aborted`, folder empty incl. hidden files).
  - `ACancelledRequestLeavesNothing` (cancelled token → `Aborted`, folder empty).
  - `ShorterThanDeclaredIsAMismatch` (declared 10, body 4 → `SizeMismatch`, folder empty) and `LongerThanDeclaredIsAMismatch` (declared 4, body 10 → `SizeMismatch`, folder empty).
  - `UnknownSizeStopsAtTheLimit` (declared null, a stream reporting 2 GiB + 1 byte read via a generated non-allocating stream → `TooLarge`, folder empty; run with a `FileReceiver` whose limit is lowered through an internal `MaxBytes` ctor parameter default `FileLimits.MaxFileBytes` so the test does not write 2 GiB).
  - `DiskFillingMidWriteIsAborted` (free-space fake drops below margin after the first buffer → `NoSpace`, folder empty).
  - `HostileNameStaysInsideTheFolder` (`../../x` → file `x` inside the folder).
- [ ] **Step 2: Run** `--filter FileReceiver`; expect FAIL.
- [ ] **Step 3: Implement** `FileReceiver` per the interface: `Precheck` order = declared > limit → `TooLarge`; free space < (declared ?? 0) + margin → `NoSpace`; slot count → `Busy`. `ReceiveAsync` writes a `.<guid>.part` file in the folder (create the folder with mode 0700 → files 0644 via `File.SetUnixFileMode`), copies in 128 KiB chunks counting bytes (re-checking free space about every 64 MiB), enforces the limit and `declaredSize` equality, then `File.Move` to `ReceivedFileName.Unique(...)` and calls `quarantine`. Every non-`Stored` path deletes the part file in a `finally`. Log counts only.
- [ ] **Step 4: Run** `--filter FileReceiver`; expect PASS.
- [ ] **Step 5: Commit** `Files: stream a file from the phone into Downloads/PhoneBox`.

### Task 4: `PUT /api/bridge/files/in`

**Files:**
- Modify: `agent/PhoneBox.Agent/BridgeApi.cs` (`Map`, new `PutFileAsync`), `agent/PhoneBox.Agent/Lan.cs` (`AllowedOverLan`), `agent/PhoneBox.Agent/Program.cs` (register `FileReceiver` in the macOS block next to `ClipboardBridge`)
- Test: `agent/PhoneBox.Agent.Tests/BridgeApiTests.cs` (extend), `agent/PhoneBox.Agent.Tests/BridgeGateTests.cs` (extend)

**Interfaces:**
- Consumes: `FileReceiver` (Task 3), `IMacNotifier` (existing: `Task<bool> PostAsync(string title, string subtitle, string body, byte[]? iconPng, string? replaceId, CancellationToken ct)`).
- Produces: `static Task<IResult> BridgeApi.PutFileAsync(HttpContext ctx, FileReceiver? receiver, IMacNotifier? notifier, ConfigStore cfg, Func<bool> authorized)`; answers: 200 `{ok:true,name,size}`, 400 (no name), 403 `{error:"not allowed here"}` / `{error:"off"}`, 413, 429, 501, 507, 409 (size mismatch), 499-free (aborted → no body). Query `name` (required, URL-decoded) and `size` (optional, digits only).

- [ ] **Step 1: Write the failing tests** in `BridgeApiTests` (use the file's helpers, `SetSwitch`-style config writing with `filesFromPhone`):
  - `PutFileStoresAndAnswersWithTheFinalName` (200, JSON `name` equals stored name, notifier recorded "Received <name>", the notification text contains no folder path).
  - `PutFileRefusesInTheCheapestOrder`: not authorized → 403 and the body stream was never read (a stream that throws on `Read`); `receiver` null → 501, body unread; switch off → 403 `off`, body unread; `size` over the limit → 413, body unread; low free space → 507, body unread; third concurrent → 429.
  - `PutFileWithoutAName` → 400. `PutFileWithBadSize` (`size=-1`, `size=abc`, `size=99999999999999999999`) → 400.
  - `PutFileMismatch` → 409 and nothing stored.
  - `PutFileRaisesTheBodyLimitOnlyForItself`: after `PutFileAsync` starts, `IHttpMaxRequestBodySizeFeature.MaxRequestBodySize == FileLimits.MaxFileBytes` on that context (use a `DefaultHttpContext` with a test feature that has `IsReadOnly == false`).
  - In the allow-list test (`OnlyTheBridgePathsAreOpenOverWifi` theory): `/api/bridge/files/in` allowed; `/api/bridge/files/anything-else` not allowed.
  - `BridgeGateTests`: `/api/bridge/files/in` is refused on the local port (403) like the other bridge paths.
- [ ] **Step 2: Run** `--filter "BridgeApi|BridgeGate"`; expect FAIL.
- [ ] **Step 3: Implement** `PutFileAsync` in the order from Global Constraints; `ctx.Features.Get<IHttpMaxRequestBodySizeFeature>()` is raised to `MaxFileBytes` before the body is touched (per request, never globally); body = `ctx.Request.Body`; post the Mac notification (title `PhoneBox`, subtitle `From your phone`, body `Received <name>`) without awaiting the result into the response (`_ = notifier.PostAsync(...)` wrapped in try/catch). `Map`: `app.MapPut("/api/bridge/files/in", (HttpRequest req) => PutFileAsync(req.HttpContext, ...))` (take the request, as the notification endpoint does, so the lambda is not mistaken for a `RequestDelegate`). Add `path.StartsWithSegments("/api/bridge/files")` to `Lan.AllowedOverLan` right after `/api/bridge/notification`.
- [ ] **Step 4: Run** `--filter "BridgeApi|BridgeGate|FileReceiver"`; expect PASS. Also run the whole agent suite once.
- [ ] **Step 5: Commit** `Files: PUT /api/bridge/files/in, the Wi-Fi allow-list entry and a Mac notification`.

### Task 5: Settings switches and strings (receive)

**Files:**
- Modify: `agent/PhoneBox.Agent/settings.html` (bridge card), `android/app/src/main/assets/www/i18n.js` and the locale packs (`locale-existing.js` plus every `locale-*.js`), `PRIVACY.md`, `docs/GUIDE.md`
- Test: `tools/ui-tests/bridge-settings.spec.js` (extend)

**Interfaces:** Produces i18n keys `s.bridge.filesFromPhone` ("Receive files from the phone into Downloads/PhoneBox") and `s.bridge.filesToPhone` ("Send files to the phone (Finder Quick Action and drop zone)"); checkboxes bound `data-bind="bridge.filesFromPhone"` / `bridge.filesToPhone` using the same `<label class="switch">` markup as the clipboard row.

- [ ] **Step 1: Extend `bridge-settings.spec.js`** (copy the existing test's setup): `the two file switches are off by default and save`: both unchecked on first load; checking one and saving writes `bridge.filesFromPhone === true` into the saved config; `filesToPhone` stays false.
- [ ] **Step 2: Run** the spec the way `pb-personal` does for an own port (copy `tools/ui-tests/playwright.config.js` with `8781`→`8791` to an untracked file; kill anything on 8791 first; rebuild the agent first because `settings.html` is embedded in the dll). Expect FAIL.
- [ ] **Step 3: Implement:** two rows in `#bridgeCard` after the notifications rows; English strings in `i18n.js`; translations for all 22 other languages in the locale packs (follow commit `451fe47` for which files change); one `PRIVACY.md` bullet each ("files go only to/from your own paired phone, are not logged, the outbox is kept one hour"); a short "Sending files" section in `docs/GUIDE.md` (both directions, where files land, limits).
- [ ] **Step 4: Run** the spec (PASS) and `python3 tools/check-release.py` (PASS).
- [ ] **Step 5: Commit** `Files: the two settings switches, strings and privacy notes`.

---

## Part 2 — Android: send to the Mac

### Task 6: Pure rules (JVM-checkable)

**Files:**
- Create: `android/app/src/main/java/net/oxog/phonebox/deskpanel/FileTransferRules.kt`, `tools/kotlin-tests/FileTransferChecks.kt`
- Modify: `android/app/src/main/java/net/oxog/phonebox/deskpanel/BridgeClip.kt` (`SendResult` gets `NO_SPACE`, `BUSY`; `resultFor`), `android/app/src/main/java/net/oxog/phonebox/deskpanel/BridgeSend.kt` (exhaustive `when`), `tools/test-bridge-checks.py` (add `FileTransferRules.kt` to `PRODUCTION`, `FileTransferChecks.kt` to `CHECKS`), `tools/kotlin-tests/BridgeChecks.kt` (new `resultFor` rows)

**Interfaces — Produces** (no Android types, so it compiles on a plain JVM):
```kotlin
object FileTransferRules {
    const val MAX_FILE_BYTES = 2L shl 30
    enum class Target { PICTURES, MOVIES, DOWNLOADS }
    data class IncomingFile(val id: String, val name: String, val size: Long, val mime: String)
    fun acceptableUri(scheme: String?, authority: String?, ownAuthorities: Set<String>): Boolean
    fun displayName(raw: String?): String                       // never empty, no path separators, ≤ 200 UTF-8 bytes
    fun targetFor(mime: String?): Target
    fun uploadPath(name: String, size: Long?): String           // "/api/bridge/files/in?name=<%-encoded UTF-8>[&size=<n>]"
    fun parseFileEvent(event: String, data: String): IncomingFile?  // event=="file"; data={"seq":n,"text":"{\"id\":…,\"name\":…,\"size\":…,\"mime\":…}"}
    fun downloadPath(id: String): String                        // "/api/bridge/files/out/<id>"
    fun donePath(id: String): String                            // "/api/bridge/files/out/<id>/done"
}
```
`SendResult` additions: `NO_SPACE` (HTTP 507), `BUSY` (429); `409` → `UNKNOWN`; `413` → `TOO_LARGE`.

- [ ] **Step 1: Write `FileTransferChecks.kt`** in the style of `BridgeChecks.kt`: `acceptableUri("content","com.android.providers.media.documents",own)` true; `acceptableUri("file",null,own)` false; `acceptableUri("content","net.oxog.phonebox.deskpanel.usbkey",setOf("net.oxog.phonebox.deskpanel.usbkey"))` false; `acceptableUri(null,…)` false; `displayName("../a/b.jpg") == "b.jpg"`, `displayName(null) == "file"`, a 5 000-char name is ≤ 200 bytes; `targetFor("image/jpeg")==PICTURES`, `"video/mp4"==MOVIES`, `"application/pdf"==DOWNLOADS`, `null==DOWNLOADS`; `uploadPath("a b&c.jpg", 12) == "/api/bridge/files/in?name=a%20b%26c.jpg&size=12"`, `uploadPath("x", null)` has no `size=`; `parseFileEvent` good frame → fields, wrong event name → null, id with `/` or longer than 64 chars → null, negative size → null; `resultFor(507)==NO_SPACE`, `429==BUSY`, `413==TOO_LARGE`, `409==UNKNOWN`.
- [ ] **Step 2: Run** `python3 tools/test-bridge-checks.py`; expect FAIL (compile error).
- [ ] **Step 3: Implement** `FileTransferRules`, the two enum values and `resultFor` rows; give `BridgeSend.send`'s `when` the two new branches (reuse `bridge_send_too_large` text for neither: add `R.string.files_send_no_space` and `R.string.files_send_busy` in Task 8).
- [ ] **Step 4: Run** `python3 tools/test-bridge-checks.py`; expect `PASS`.
- [ ] **Step 5: Commit** `Files (phone): the pure rules and two new send results`.

### Task 7: Streaming upload in `AgentLink` and `BridgeClient`

**Files:**
- Modify: `android/app/src/main/java/net/oxog/phonebox/deskpanel/AgentLink.kt` (`exchange`, `usb`, `wifi`, new `upload`), `android/app/src/main/java/net/oxog/phonebox/deskpanel/BridgeClient.kt`, `tools/kotlin-tests/AgentLink.kt` (the JVM stand-in used by checks, add the same `upload` signature)

**Interfaces:**
- Produces on `AgentLink`: 
  `fun upload(pathAndQuery: String, contentType: String, length: Long?, open: () -> java.io.InputStream, onProgress: (Long) -> Unit, cancelled: () -> Boolean): Reply`
  Streams `open()` into a `PUT`; `length` non-null → `setFixedLengthStreamingMode(length)`, null → `setChunkedStreamingMode(64 * 1024)`; read timeout 60 s; USB first, then Wi-Fi, exactly like `request`, but **a PUT whose body was already written is never retried on the other route** (treat like `controlPost`: an `IOException` on the response gives the 504 "result unknown" reply). `cancelled()` is checked between 64 KiB chunks and aborts with `Reply(499, …)`.
  On `BridgeClient`: `fun uploadFile(name: String, size: Long?, open: () -> InputStream, onProgress: (Long) -> Unit, cancelled: () -> Boolean): SendResult` (never throws; uses `FileTransferRules.uploadPath`, content type `application/octet-stream`, maps with `BridgeClip.resultFor`).
- Consumes: `FileTransferRules.uploadPath` (Task 6).

- [ ] **Step 1: Write the failing check** in `tools/kotlin-tests/FileTransferChecks.kt` using the stand-in `AgentLink`: `uploadFile` returns `UNREACHABLE` when the stand-in link throws; returns `OK` on a 200 stand-in reply; calls `onProgress` with a final value equal to the bytes read; stops and returns `UNKNOWN`/cancelled result when `cancelled()` turns true (stand-in reads 3 chunks, then cancel).
- [ ] **Step 2: Run** `python3 tools/test-bridge-checks.py`; expect FAIL.
- [ ] **Step 3: Implement** `exchange` support for `PUT` with a streamed body (today only `POST` writes a body; keep `POST` behaviour byte-identical), `upload` through `usb`/`wifi` via a small private `Body` holder instead of a `ByteArray`, and `BridgeClient.uploadFile`. Do not change `request`'s signature.
- [ ] **Step 4: Run** `python3 tools/test-bridge-checks.py` (PASS) and `cd android && ./gradlew assembleDebug lintDebug` (no errors).
- [ ] **Step 5: Commit** `Files (phone): a streaming upload over the existing link`.

### Task 8: Share target, upload service, switch, strings

**Files:**
- Create: `android/app/src/main/java/net/oxog/phonebox/deskpanel/SendFilesToMacActivity.kt`, `FileUploadService.kt`
- Modify: `AndroidManifest.xml`, `BridgePrefs.kt` (`filesFromPhoneEnabled`, `setFilesFromPhoneEnabled`), `BridgeEntryPoints.kt` (`update` also toggles `SendFilesToMacActivity` with the switch), `BridgeActivity.kt` (a switch row), `res/values/strings.xml` and every `res/values-*/strings.xml`, `PRIVACY.md`

**Interfaces:**
- Consumes: `FileTransferRules`, `BridgeClient.uploadFile`, `BridgePrefs`, `BridgeEntryPoints`.
- Produces: `SendFilesToMacActivity` (exported, `android:enabled="false"` in the manifest, filters `ACTION_SEND` and `ACTION_SEND_MULTIPLE`, `mimeType="*/*"`, label `@string/bridge_send_files_to_mac`, same translucent theme/taskAffinity as `SendToMacActivity`); `FileUploadService.start(context: Context, uris: List<android.net.Uri>)` (a not-exported foreground service, type `dataSync`, permission `FOREGROUND_SERVICE_DATA_SYNC`, progress notification with a Cancel action, final notification "N files sent" or the failure text).
- Strings (all 23 languages, `%1$s`/`%1$d` placeholders identical): `bridge_send_files_to_mac`, `files_confirm_title`, `files_sent`, `files_send_off`, `files_send_no_space`, `files_send_busy`, `files_send_failed`, `files_uploading`, `files_cancel`, `bridge_files_from_phone_switch`.

- [ ] **Step 1: Pure part first, a failing check:** extend `FileTransferChecks.kt` for a helper `FileTransferRules.confirmLines(names: List<String>, sizes: List<Long?>, maxLines: Int): List<String>` (first `maxLines` "name — 1.2 MB" lines plus "+N more"): `confirmLines(["a.jpg"],[1_200_000],3) == ["a.jpg — 1.2 MB"]`, 5 files with `maxLines=3` → 3 lines + `"+2 more"`, size `null` → no size text. Run: FAIL; implement in `FileTransferRules`; run: PASS.
- [ ] **Step 2: Implement the activity.** `onCreate`: read `intent.action`, `EXTRA_STREAM` (`getParcelableExtra` / `getParcelableArrayListExtra`, wrapped like `SendToMacActivity` so an unreadable extra counts as nothing); reject every URI failing `FileTransferRules.acceptableUri(uri.scheme, uri.authority, setOf("$packageName.usbkey"…the app's own provider authorities from the manifest))`; if the switch is off → open `BridgeActivity` (as `BridgeSend.sendable` does); else show the confirmation dialog (names and sizes from `OpenableColumns`, `filterTouchesWhenObscured`, buttons `bridge_send_files_to_mac` / `pair_negative`); on Send call `FileUploadService.start` and `finish()`.
- [ ] **Step 3: Implement the service.** Foreground at once (like `ClipboardService.goForeground`); one worker thread uploads the URIs one after another (sequential, my call), size from `OpenableColumns.SIZE` (null/≤0 → `null`), `open = { contentResolver.openInputStream(uri)!! }`, progress notification updated at most once a second, Cancel via a `PendingIntent` to the service; at the end post the result notification and `stopSelf()`. A refusal with `OFF` stops the batch with `files_send_off`.
- [ ] **Step 4: Wire the rest:** manifest entries (permissions `FOREGROUND_SERVICE_DATA_SYNC`, the disabled exported activity, the service with `foregroundServiceType="dataSync"`), `BridgePrefs` pair, `BridgeEntryPoints.update` (the new activity follows `filesFromPhoneEnabled`; the boot receiver rule becomes `clipboard || notifications || filesToPhone` in Task 14), a switch row in `BridgeActivity` (use `switchRow`), strings in `values` and all 22 `values-*` folders (folder names: `id→in`, `pt-BR→b+pt+BR`, `zh-Hant→zh-rTW`, others by code), one `PRIVACY.md` line.
- [ ] **Step 5: Run** `python3 tools/check-release.py` (PASS), `python3 tools/test-bridge-checks.py` (PASS), `cd android && ./gradlew assembleDebug lintDebug` (no errors).
- [ ] **Step 6: Commit** `Files (phone): Send to Mac in the share sheet, with a confirmation and an upload service`.

### Task 9: Live check, phone → Mac (owner present)

**Files:** none.

- [ ] **Step 1: Ask the owner.** Build the debug APK and run a demo agent from the worktree in its own `HOME` and minimal `PATH` (see Global Constraints). Install the debug APK over the app only after the owner agrees (it replaces the app on the phone, same debug key, no re-pairing).
- [ ] **Step 2: Test and record each:** one photo; a video over 1 GB; three files at once; a file named `../../x`; a file from an app giving no size; cancel mid-way (no file left on the Mac); unplug USB mid-way (no `.part` left); switch off on the Mac (phone shows the "off" message); a received `.command` file carries the quarantine attribute (`xattr -l`); a received photo opens in Preview. Restore volume/playback state. Report what was **not** tested.

---

## Part 3 — Agent: send to the phone

### Task 10: `Outbox`

**Files:**
- Create: `agent/PhoneBox.Agent/Outbox.cs`
- Test: `agent/PhoneBox.Agent.Tests/OutboxTests.cs`

**Interfaces:**
- Consumes: `ReceivedFileName.Sanitize`, `FileLimits`.
- Produces:
```csharp
enum OutboxState { Waiting, Sending, Delivered, Expired, Failed }
sealed record OutboxItem(string Id, string Name, long Size, string Mime, long CreatedMs);
sealed record OutboxEntry(OutboxItem Item, OutboxState State);
enum OutboxAddStatus { Queued, TooLarge, Full, Aborted }
sealed record OutboxAddResult(OutboxAddStatus Status, OutboxItem? Item);
internal sealed class Outbox(string folder, TimeProvider time, Func<string, long>? freeSpace = null)
{
    Task<OutboxAddResult> AddAsync(string? rawName, long? declaredSize, string? mime, Stream body, CancellationToken ct);
    IReadOnlyList<OutboxEntry> List();                 // newest first, includes the last 20 finished entries (metadata only)
    Stream? Open(string id);                           // read-only; marks Sending; null for an unknown/expired id
    bool Done(string id);                              // deletes the file, marks Delivered; false for an unknown id
    void Sweep();                                      // deletes files older than FileLimits.OutboxTtl (state Expired)
    void Clear();                                      // deletes the whole folder's contents
    int WaitingCount { get; }
}
```
Ids are 32 lowercase hex chars from `RandomNumberGenerator.GetBytes(16)`; files are stored as `<folder>/<id>` (no user-controlled name on disk); mime defaults to `application/octet-stream`.

- [ ] **Step 1: Write the failing tests:** `AddStoresUnderARandomIdNotTheName`; `AddRefusesAFileOverTheLimit` (limit lowered through an optional ctor parameter, as in Task 3) → `TooLarge`, nothing on disk; `AddRefusesWhenTheTotalWouldPassFourGiB` → `Full`; `AbortedBodyLeavesNothing`; `OpenReturnsTheBytesAndMarksSending`; `OpenOfAnUnknownIdIsNull` (also ids `../x`, `A`, 33 chars); `DoneDeletesTheFile` and `DoneTwiceIsFalse`; `SweepExpiresAfterOneHour` with a `FakeTimeProvider` (59 min stays, 61 min is `Expired` and the file is gone); `ClearEmptiesTheFolder`; `ListNeverContainsPathsOrContents`; `NewOutboxClearsLeftoversFromAnEarlierRun`.
- [ ] **Step 2: Run** `--filter Outbox`; expect FAIL.
- [ ] **Step 3: Implement** with a lock around the in-memory index; the constructor deletes everything in the folder; `AddAsync` streams to `<id>.part`, then renames; total size counts Waiting+Sending entries.
- [ ] **Step 4: Run** `--filter Outbox`; expect PASS.
- [ ] **Step 5: Commit** `Files: the Mac-to-phone outbox`.

### Task 11: Local key and the local trigger

**Files:**
- Create: `agent/PhoneBox.Agent/LocalTransferKey.cs`, `agent/PhoneBox.Agent/TransferApi.cs`
- Modify: `agent/PhoneBox.Agent/Program.cs` (register `Outbox`, `LocalTransferKey`; call `TransferApi.Map(app, SameOrigins)` near `BridgeApi.Map(app, gate)`)
- Test: `agent/PhoneBox.Agent.Tests/TransferApiTests.cs`

**Interfaces:**
- Consumes: `Outbox` (Task 10), `BridgeStream.Publish(string name, string text, string? origin)` (existing), `ConfigStore`.
- Produces: 
  `internal sealed class LocalTransferKey(string path, int port) { const string Header = "X-PhoneBox-Transfer-Key"; void Ensure(); void Delete(); bool Matches(string? header); }` (`Ensure` writes `{"port":N,"key":"<32 random bytes, base64url>"}` with mode 0600, keeps the same key while the file exists, `Matches` is a fixed-time compare, false when no key);
  `static class TransferApi { void Map(IEndpointRouteBuilder app, string[] sameOrigins); static bool CallerAllowed(HttpRequest req, string[] sameOrigins, LocalTransferKey key); static string FileEventJson(OutboxItem item); static Task<IResult> SendAsync(HttpContext ctx, Outbox outbox, BridgeStream stream, ConfigStore cfg, LocalTransferKey key, string[] sameOrigins); static IResult Status(Outbox outbox, ConfigStore cfg, HttpRequest req, LocalTransferKey key, string[] sameOrigins); }`
  `FileEventJson` = `{"id":"…","name":"…","size":N,"mime":"…"}`; `SendAsync` publishes `stream.Publish("file", FileEventJson(item), null)` after a successful `AddAsync` and answers 202 `{id}`. `Status` answers `{files:[{id,name,size,state}]}`.

- [ ] **Step 1: Write the failing tests:**
  - `CallerAllowed`: Origin equal to a same origin → true; header-less (no Origin, no key) → **false**; foreign Origin → false; wrong key → false; right key, no Origin → true; key of a deleted file → false.
  - `SendAsync`: switch off → 403 `off`, body unread; right caller + on → 202, the item is in the outbox, and the stream's last event is `file` with the JSON; over the limit → 413; outbox full → 507; missing `name` → 400.
  - `KeyFileIsPrivate`: after `Ensure`, `File.GetUnixFileMode(path) == UserRead | UserWrite`; `Delete` removes it; calling `Ensure` twice keeps the key.
  - `TransferPathsAreNotOnTheWifiAllowList`: `Lan.AllowedOverLan("/api/transfer/send")` and `("/api/transfer/status")` are false (add rows to the existing allow-list theory); `UsbLink.Authorize` for the same paths is not `Allowed` (use the existing UsbLink test helper in `BridgeGateTests`).
  - `LocalPortServesTransferButNotBridge`: through `RequestGate` on the local port `/api/transfer/status` passes the gate (not 403), `/api/bridge/files/out` is 403.
- [ ] **Step 2: Run** `--filter "Transfer|LocalTransferKey|BridgeGate"`; expect FAIL.
- [ ] **Step 3: Implement.** `CallerAllowed` requires a **present** `Origin` that is in `sameOrigins`, or a matching key header — a request with neither is refused (the gate itself lets header-less requests through, so this check is the real one). Both endpoints call it first. `Map` takes the request, not the context, in its lambdas (as in Task 4). The body goes straight to `outbox.AddAsync(name, size, mime, ctx.Request.Body, ctx.RequestAborted)` after the per-request body-limit raise (as Task 4).
- [ ] **Step 4: Run** the same filter plus `BridgeApi`; expect PASS.
- [ ] **Step 5: Commit** `Files: the local trigger for sending to the phone, with a local key`.

### Task 12: Phone-facing outbox endpoints, the `file` event and stream gating

**Files:**
- Modify: `agent/PhoneBox.Agent/BridgeApi.cs` (`Map`, `StreamAllowed`, `StreamAsync`, new `ListOutbox`, `GetOutboxFile`, `DoneOutboxFile`), `agent/PhoneBox.Agent/AdbReverse.cs` only if `HelperWanted` does not already require the clipboard switch (read it first)
- Test: `agent/PhoneBox.Agent.Tests/BridgeApiTests.cs`, `agent/PhoneBox.Agent.Tests/BridgeReplayTests.cs`, `agent/PhoneBox.Agent.Tests/ClipHelperLauncherTests.cs` (extend)

**Interfaces:**
- Consumes: `Outbox` (Task 10), `TransferApi.FileEventJson` (Task 11).
- Produces: `static bool BridgeApi.StreamAllowed(ConfigStore)` becomes `Clipboard || FilesToPhone`; `static bool BridgeApi.EventAllowed(BridgeConfig cfg, string eventName)` (`clipboard` ↔ `Clipboard`, `file` ↔ `FilesToPhone`, anything else false); `static IResult ListOutbox(Outbox? o, ConfigStore cfg, Func<bool> authorized)` → `{files:[{id,name,size,mime}]}` of **Waiting/Sending** entries; `static IResult GetOutboxFile(string id, Outbox? o, ConfigStore cfg, Func<bool> authorized)` → `Results.Stream(stream, mime, fileDownloadName: null, enableRangeProcessing: true)`; `static IResult DoneOutboxFile(string id, Outbox? o, ConfigStore cfg, Func<bool> authorized)` → 200/404. All: 403 not allowed / 403 `off` when `FilesToPhone` is off / 501 when `o` is null.

- [ ] **Step 1: Write the failing tests:**
  - Endpoint order and answers for the three handlers (not authorized 403, null 501, switch off 403, unknown id 404, ids like `../x` 404, list excludes Delivered/Expired, GET with `Range: bytes=3-` answers 206 with the right bytes, `done` deletes the file).
  - Allow-list rows: `/api/bridge/files/out`, `/api/bridge/files/out/<32hex>`, `/api/bridge/files/out/<32hex>/done` allowed over Wi-Fi.
  - Stream gating: `StreamAllowed` true with only `FilesToPhone`; false with neither; `EventAllowed` table above.
  - **Review Focus 4:** `FilesOnlyStreamDeliversNoClipboardEvents` (switches: clipboard off, filesToPhone on; publish a `clipboard` and a `file` event; the frames written by `StreamAsync` contain only the `file` one, both in replay and live); `ClipboardOnlyStreamDeliversNoFileEvents`; and in `ClipHelperLauncherTests`: `HelperWanted(true, cfgWithOnlyFilesToPhone, listeners: 1)` is **false**.
  - Replay: a `file` event survives `Resume` within the window like a clipboard one (existing `BridgeReplayTests` style).
- [ ] **Step 2: Run** `--filter "BridgeApi|BridgeReplay|ClipHelper"`; expect FAIL.
- [ ] **Step 3: Implement.** Filter inside both loops of `StreamAsync` (replay and `Since`) with `EventAllowed(cfg.Get().Bridge, e.Name)`; keep `Open()` using the new `StreamAllowed`. Map the three routes with `{id}` constrained to `^[0-9a-f]{32}$` (anything else → 404 before the handler). Add `path.StartsWithSegments("/api/bridge/files")` already covers them (Task 4).
- [ ] **Step 4: Run** the filter plus the whole agent suite; expect PASS.
- [ ] **Step 5: Commit** `Files: the phone pulls files from the outbox; the stream follows either switch`.

### Task 13: Switch watcher (key file, outbox sweep, cleanup)

**Files:**
- Create: `agent/PhoneBox.Agent/TransferSwitchWatcher.cs`
- Modify: `agent/PhoneBox.Agent/Program.cs` (register as a hosted service in the macOS block)
- Test: `agent/PhoneBox.Agent.Tests/TransferSwitchWatcherTests.cs`

**Interfaces:**
- Consumes: `ConfigStore`, `Outbox`, `LocalTransferKey`, and an interface `IQuickActionInstaller { void Install(); void Remove(); }` (implemented in Task 15; a fake here).
- Produces: `internal sealed class TransferSwitchWatcher(ConfigStore cfg, Outbox outbox, LocalTransferKey key, IQuickActionInstaller qa, TimeSpan period, ILogger log) : BackgroundService` with a public `void Tick()` (the test hook) — `FilesToPhone` on: `key.Ensure()`, `qa.Install()` once, `outbox.Sweep()`; off: `key.Delete()`, `qa.Remove()` once, `outbox.Clear()`. Period 5 s.

- [ ] **Step 1: Write the failing tests:** `TurningOnWritesTheKeyAndInstallsOnce` (two ticks → one `Install`); `TurningOffRemovesKeyQuickActionAndClearsTheOutbox`; `StartWithTheSwitchOffClearsLeftovers`; `AnExceptionInInstallDoesNotStopTheWatcher` (`Tick` swallows and logs, the next tick retries).
- [ ] **Step 2: Run** `--filter TransferSwitchWatcher`; expect FAIL. **Step 3: Implement.** **Step 4: Run;** PASS.
- [ ] **Step 5: Commit** `Files: keep the key, the Quick Action and the outbox in step with the switch`.

---

## Part 4 — Android: receive from the Mac

### Task 14: Receive path in the phone service

**Files:**
- Modify: `android/app/src/main/java/net/oxog/phonebox/deskpanel/ClipboardService.kt`, `BridgePrefs.kt` (`filesToPhoneEnabled`, `setFilesToPhoneEnabled`, `streamWanted(context) = clipboardEnabled || filesToPhoneEnabled`), `BridgeEntryPoints.kt` (boot receiver follows `streamWanted || notifications`), `BridgeActivity.kt` (second switch row), `AndroidManifest.xml` (service property text), `res/values*/strings.xml`, `PRIVACY.md`
- Create: `android/app/src/main/java/net/oxog/phonebox/deskpanel/IncomingFiles.kt`
- Modify tests: `tools/kotlin-tests/FileTransferChecks.kt`

**Interfaces:**
- Consumes: `FileTransferRules.parseFileEvent/downloadPath/donePath/targetFor` (Task 6), `AgentLink.stream(path, readTimeoutMs, headers)` (existing, used for the download with a `Range` header), `BridgeClient`.
- Produces: `class IncomingFiles(context: Context)` with `fun onEvent(file: FileTransferRules.IncomingFile)`, `fun onStreamOpen()` (fetches `GET /api/bridge/files/out` and queues every id not seen yet) and a single worker thread; `BridgeClient.listOutbox(): List<FileTransferRules.IncomingFile>?`, `BridgeClient.markDone(id: String): SendResult`; pure helper `FileTransferRules.parseOutboxList(json: String): List<IncomingFile>`; save step `MediaSaver.save(context, file, input: InputStream): android.net.Uri?` (API 29+ `MediaStore`, `IS_PENDING` 1 → 0 at the end, display name from `FileTransferRules.displayName`, collision-safe by the system; below 29 → `getExternalFilesDir(DIRECTORY_DOWNLOADS)`); a completion notification that opens the file with `ACTION_VIEW` and a read grant (several files within 5 s → one summary).
- Service changes: the service runs while `BridgePrefs.streamWanted`; clipboard handling stays guarded by `clipboardEnabled`; the foreground notification title is the clipboard one when the clipboard switch is on, otherwise `bridge_notification_title_files`; the `"open"` stream event also calls `incoming.onStreamOpen()`; the `else` branch routes `parseFileEvent(event, data)` to `incoming.onEvent`.

- [ ] **Step 1: Failing checks:** `parseOutboxList("""{"files":[{"id":"…32 hex…","name":"a.jpg","size":3,"mime":"image/jpeg"}]}""")` gives one `IncomingFile`; a bad id, a missing field, or a non-object gives an empty list; `parseFileEvent` as in Task 6 still passes. An `IncomingQueue` pure class (in `FileTransferRules.kt`: `class IncomingQueue { fun offer(id: String): Boolean; fun finished(id: String) }`) returns `false` for an id already queued or finished (so the event and the list fetch never download twice): check `offer("a")==true`, `offer("a")==false`, after `finished("a")` still `false` (kept for the process lifetime, 200 ids max, oldest dropped).
- [ ] **Step 2: Run** `python3 tools/test-bridge-checks.py`; expect FAIL.
- [ ] **Step 3: Implement** the pure parts, then `IncomingFiles`: for each accepted file, `link.stream(downloadPath(id), readTimeoutMs = 60_000, headers = mapOf("Range" to "bytes=$already-"))` into `MediaSaver` (resume from the byte count already written only within one process run; otherwise restart), check the final length equals `size`, then `BridgeClient.markDone(id)`; a size mismatch or 404 is dropped silently; never more than one download at a time. Replace every `BridgePrefs.clipboardEnabled(...)` that decides whether the **service** runs (`onStartCommand`, `pairingChanged`, `sync`, `repostNotification`) with `BridgePrefs.streamWanted(...)`.
- [ ] **Step 4: Strings and screen:** `bridge_files_to_phone_switch`, `bridge_notification_title_files`, `files_received` (`%1$s`), `files_received_many` (`%1$d`), `files_receive_failed` in all 23 languages; a second switch row on `BridgeActivity`; one `PRIVACY.md` line (already added in Task 5 for the Mac side — extend, do not duplicate).
- [ ] **Step 5: Run** `python3 tools/check-release.py`, `python3 tools/test-bridge-checks.py`, `python3 tools/test-agent-stream.py` (all PASS) and `cd android && ./gradlew assembleDebug lintDebug` (no errors).
- [ ] **Step 6: Commit** `Files (phone): receive files from the Mac into the gallery and Downloads`.

---

## Part 5 — Mac: Quick Action and drop zone

### Task 15: Quick Action installer and script

**Files:**
- Create: `agent/PhoneBox.Agent/Unix/QuickActionInstaller.cs`
- Modify: `agent/PhoneBox.Agent/Program.cs` (register `IQuickActionInstaller` in the macOS block)
- Test: `agent/PhoneBox.Agent.Tests/QuickActionTests.cs`

**Interfaces:**
- Consumes: `IQuickActionInstaller` (Task 13), the key file path from `LocalTransferKey`, and the outcome of Spike S2 (Task 2a) for the exact `.workflow` layout.
- Produces: `internal sealed class QuickActionInstaller(string servicesDir, string keyFilePath) : IQuickActionInstaller`, `static string BuildScript(string keyFilePath)` and `string WorkflowName = "Send to phone.workflow"`.
- The script (bash) must: take files as `"$@"`; read `port` and `key` from the key file with `/usr/bin/plutil` or `python3 -c` (never `eval`); for each file call `curl --fail --silent --show-error --max-time 0 -H "X-PhoneBox-Transfer-Key: $key" -T -- "$file" "http://127.0.0.1:$port/api/transfer/send?name=$(encoded basename)"` (the URL-encoding done with `python3 -c 'import sys,urllib.parse;print(urllib.parse.quote(sys.argv[1],safe=""))' -- "$base"` or `/usr/bin/jq -rn --arg s … '$s|@uri'`, whichever the Mac has: the script picks `python3` and falls back to `jq`, and shows an error notification if neither exists); skip directories; finish with one `osascript -e 'display notification (item 1 of argv) …' -- "<text>"` using `on run argv` so no file name is spliced into AppleScript.

- [ ] **Step 1: Write the failing tests:** `ScriptPassesFilesAsArguments` (the script text contains `"$@"` and never contains an unquoted `$file`/`$1`; run the script with `sh -n` for syntax); **Review Focus 5:** `ScriptHandlesAwkwardNames`: with a fake `curl` earlier in `PATH` that records its arguments, run the script on files named `a b.txt`, `it's.txt`, `-rf.txt`, `line1\nline2.txt` (created in a temp folder) and assert the recorded URL for each is `…/api/transfer/send?name=<percent-encoded exact base name>`, that `--` precedes the file argument, and that the key header value equals the key in a temp key file; `ScriptRefusesWhenTheKeyFileIsMissing` (non-zero exit, nothing sent); `InstallWritesTheWorkflowAndRemoveDeletesIt` (temp `servicesDir`; after `Install` the folder `Send to phone.workflow/Contents/` has `Info.plist` and `document.wflow` that `plutil -lint` accepts; `Remove` deletes it; `Remove` twice does not throw); `InstallDoesNotTouchOtherWorkflows`.
- [ ] **Step 2: Run** `--filter QuickAction`; expect FAIL.
- [ ] **Step 3: Implement** `BuildScript` and the installer following the layout proven in Task 2a. Install writes into a temp sibling and renames, so Finder never sees a half-written bundle.
- [ ] **Step 4: Run** `--filter QuickAction`; expect PASS. Then, with the owner's go-ahead, on the real Mac: run the demo agent, turn `FilesToPhone` on, and confirm "Send to phone" appears under Finder's Quick Actions and moves a text file to the phone (Task 17 covers the phone side).
- [ ] **Step 5: Commit** `Files: the Finder Quick Action "Send to phone"`.

### Task 16: Drop zone on the settings page

**Files:**
- Modify: `agent/PhoneBox.Agent/settings.html` (bridge card + script), the locale files (`s.bridge.dropTitle`, `s.bridge.dropOff`, `s.bridge.dropWaiting`, `s.bridge.dropSending`, `s.bridge.dropDelivered`, `s.bridge.dropExpired`, `s.bridge.dropFailed` in all 23 languages)
- Test: `tools/ui-tests/bridge-settings.spec.js`

**Interfaces:**
- Consumes: `PUT /api/transfer/send?name=<encoded>` and `GET /api/transfer/status` (Task 11), same origin so no key is needed; the page's `api()` helper prefixes `/agent/api` itself, so use plain `fetch('/api/transfer/send…')` like `bridgeTest()` does for `/api/bridge/notification/test`.
- Produces: `#bridgeDrop` (a dashed drop area inside `#bridgeCard`, `hidden` unless `osName === 'macos'`; disabled with `s.bridge.dropOff` while `cfg.bridge.filesToPhone` is false), `async function bridgeDropFiles(fileList)` (sequential `fetch` with `body: file`, per-file progress text), and a status list refreshed every 2 s from `/api/transfer/status` while the card is visible.

- [ ] **Step 1: Failing UI test:** `the drop zone is disabled while the switch is off`; `dropping a file posts its bytes to /api/transfer/send` (route-intercept `/api/transfer/send` with `page.route`, dispatch a `drop` event carrying a `File('x.txt')`, assert one `PUT` with `name=x.txt` and the body text); `the status list shows the state words from /api/transfer/status` (intercept with `{files:[{id,name,size,state:"delivered"}]}`).
- [ ] **Step 2: Run** the spec on its own port (see Task 5); expect FAIL.
- [ ] **Step 3: Implement** the markup, the handlers, and the strings.
- [ ] **Step 4: Run** the spec (PASS) and `python3 tools/check-release.py` (PASS).
- [ ] **Step 5: Commit** `Files: a drop zone for sending to the phone`.

---

## Part 6 — Finish

### Task 17: Live check Mac → phone, docs, version, release dry run (owner present)

**Files:**
- Modify: `agent/PhoneBox.Agent/PhoneBox.Agent.csproj` (`<Version>0.9.29.2</Version>`), `android/app/build.gradle.kts` (`versionName = "0.9.29.2"`, `versionCode` +1), `docs/PERSONAL-BUILD.md` (a line about the file switches), `docs/GUIDE.md` (Quick Action and drop zone, the Downloads permission note from Spike S1)

- [ ] **Step 1: Ask the owner** before touching the phone. Same demo-agent isolation as Task 9.
- [ ] **Step 2: Test and record each:** drop a photo (lands in Gallery › PhoneBox, notification opens it); Quick Action on three files; a 1 GB video over USB and over Wi-Fi; phone offline, then back within the hour (file arrives; the outbox status flips to delivered); phone away more than an hour (expired, file deleted); switch off on the Mac while a file waits (outbox empty, key file gone, Quick Action gone); a file over 2 GiB refused at the trigger; `ps` shows no leftover demo agent and `adb reverse --list` shows only `tcp:8765`; restore volume and playback state.
- [ ] **Step 3: Docs and version** edits above; run the full agent suite, `python3 tools/check-release.py`, `python3 tools/test-bridge-checks.py`, `python3 tools/test-agent-stream.py`, `cd android && ./gradlew assembleDebug lintDebug`, and the Windows build the same way `docs/PERSONAL-BUILD.md` lists (expect 0 errors).
- [ ] **Step 4: Dry run the release script** (`scripts/personal-release.sh` without `--publish`) and show the owner its output. **Do not publish and do not push**; ask first.
- [ ] **Step 5: Commit** `Files: version 0.9.29.2 and the guide`, then report what was and was not tested on hardware, and offer to merge `personal/file-transfer` into `personal/phone-bridge`.

---

## Self-review (done against the spec)

- Spec coverage: switches/limits/sanitizer (2), receive writer with quarantine, size hint and disk checks (3), receive endpoint and Mac notification (4), settings card + privacy (5), share target with URI rules and confirmation, upload service (6–8), outbox/TTL/total cap/cleanup (10), local key and header-less refusal and not-on-Wi-Fi (11), pending list/download/done and stream gating (12), switch watcher (13), phone receive and MediaStore routing and below-29 fallback (14), Quick Action and awkward names (15), drop zone with status (16), live tests, docs, version, release dry run (9, 17), both spikes (1, 2a). One phone assumed: no target picker (Task 12 leaves any paired phone able to fetch; first `done` deletes).
- Names checked across tasks: `FileLimits`, `ReceivedFileName`, `FileReceiver`, `Outbox`/`OutboxItem`/`OutboxEntry`, `LocalTransferKey`, `TransferApi.FileEventJson`, `IQuickActionInstaller`, `FileTransferRules.*`, `SendResult.NO_SPACE/BUSY` are defined once and used with the same signatures.
- Proportion: code blocks hold only interface signatures; bodies are left to the implementer.
