# Phone ↔ Mac file transfer ("Share to Mac / Send to phone") — design

Date: 2026-11-24. Status: draft for approval (revised the same day, see Base). Builds on the phone-to-Mac bridge
([2026-11-23-phone-mac-bridge-design.md](2026-11-23-phone-mac-bridge-design.md)); read its Constraints first.

## Base

This is **personal-only work for the owner's own Mac and phone**, not a series of upstream pull requests. The maintainer
accepted only the clipboard part (#528/#529) and put the rest on hold, so the owner keeps the whole bridge on the
personal branch `personal/phone-bridge` (worktree `/Volumes/T7 MAC/Code/pb-personal`, pushed to the fork, version
`0.9.29.1`, upstream 0.9.29 already merged, `scripts/personal-release.sh` publishing signed-with-the-debug-key releases to
`Vasilispapg/phonebox`, `docs/PERSONAL-BUILD.md`). File transfer is built **on top of that branch**: it already has the
clipboard bridge, the notification bridge with the Swift notifier (`MacAppNotifier`, the PhoneBox icon), the adb
clipboard helper, the Phone bridge screen and the hardened gate. Nothing here goes to `ersinkoc/phonebox`. The
maintainer's rules (one change per PR, about 800 lines, CHANGELOG left to him, the bridge-path allow-list as a "security
decision" he reviews) therefore **do not bind this work**; the code is still kept tidy and every security point below
still applies, because the data is the owner's own.

## Intent

The owner has an Android phone and a Mac and wants what AirDrop gives between Apple devices: **press Share, pick the
other device, the file arrives.** In both directions, for photos, videos and any other file. PhoneBox already links the
two (pairing, pinned TLS, token, USB or Wi-Fi), and the clipboard bridge already moves text. Files are the missing
half; the bridge design listed them as a non-goal for the clipboard series, so this is a separate series of pull requests.

## Goals

- **Phone → Mac:** a "Send to Mac" entry in Android's Share sheet for any file or image (one or several). The files
  stream to the agent and land in `~/Downloads/PhoneBox/` on the Mac. Progress and the result show on the phone.
- **Mac → phone:** (A) a Finder **Quick Action** "Send to phone" (right-click → Quick Actions) and (C) a **drop zone**
  on the agent's settings page. The file goes to the paired phone, is saved there (photos and videos into the gallery,
  everything else into Downloads) and a notification opens it with one tap.
- Large files work: a 1–2 GB video does not need to fit in memory on either side.
- Works over USB and over Wi-Fi/Tailscale, whichever route the bridge already uses. No cloud, no relay, no new
  always-on listener, no new pairing.
- Everything off by default, one switch per direction.

## Non-goals

- Finding nearby devices without pairing, or any transfer when the phone cannot reach the Mac (no relay, no
  store-and-forward beyond the short outbox below).
- A real macOS Share-menu entry (needs a signed native app extension; the agent is signed ad hoc, see #269). Possible
  later as its own piece; this series does the Quick Action and the drop zone, as the owner chose.
- Sending folders, syncing folders, a transfer history, clipboard images, iPhone, a Play Store build.
- Resuming a half-finished upload from the phone (it restarts). Downloads on the phone may resume (range request) if cheap.
- Windows and Linux: the agent code is plain .NET and kept portable, but only macOS is implemented and tested
  (the owner has no Windows hardware; see the project notes). Quick Action and quarantine are macOS-only.

## Anti-goals

- A file that is opened, run or interpreted by PhoneBox, ever. Received files are only written to disk.
- Any file appearing on the phone from anything but the paired Mac, or on the Mac from anything but the paired phone.
- Filling the disk, or the phone's storage, without a check and a clear error.
- Looking switched on while nothing works (status on both sides says what is wrong: switch off, no route, Mac
  asleep, no space, too large).
- File names or contents in the agent log, or the outbox outliving its purpose.

## Constraints found in the code

- **The bridge paths are refused on the local port** (`RequestGate.IsBridgePath`), on purpose: any local process could
  otherwise read the clipboard. A Quick Action is a local process, so the Mac → phone *trigger* needs its own local
  path outside `/api/bridge`, with its own authentication (below). The gate for phone-facing paths stays unchanged.
- **Phone-facing paths must be on the Wi-Fi allow-list** (`Lan.AllowedOverLan`), each with a one-line reason in the
  guide. The local trigger path must **not** be on that list.
- **One request body limit**: Kestrel's default body limit is about 30 MB; the upload endpoint raises it for itself only
  (as the pairing claim lowers it, via `IHttpMaxRequestBodySizeFeature`), never globally.
- **The bridge stream** (`/api/bridge/stream`, `BridgeStream`, `ClipboardService`) is tied to the clipboard switch, keeps
  20 events in memory, and replays only the last two minutes. Mac → phone needs the stream (or a poll) while only the
  file switch is on, and needs events that outlive two minutes. The Android service and its notification text say
  "sharing the clipboard" and must become honest for files.
- **Android:** minSdk 21, targetSdk 35. From API 29 files go into the shared Downloads/Pictures/Movies through
  `MediaStore` with no permission; below 29 that needs a storage permission the app does not have. Android 15 limits
  `dataSync` foreground services to about six hours a day (fine for a transfer).
- **An exported share target can be started by any app with any intent.** The text flow already asks the owner to
  confirm for this reason (the maintainer's review of #529). A file flow adds a worse trick: an intent whose stream URI
  points at the app's own private data (`file://` paths, or this app's own content provider such as `UsbKeyProvider`).
- **No Mac UI in the agent** (launchd user agent, no tray). `tools/check-release.py` still enforces what the personal
  branch kept: a default for every new setting, all three `config.default*.json`, the settings card and the language
  strings (23 languages), and `PRIVACY.md`; the checks must stay green. Anything that sends data stays off by default.
- **The personal updater** installs the fork's releases over the agent and the app. The macOS privacy grant (Downloads,
  Files and Folders) belongs to the agent's ad hoc signature, which changes with every build, so each update may ask
  again. The spike below also checks this.

## Approaches considered

Only the Mac-side trigger was a real choice (the phone side is the Android share sheet, with no alternative that
feels like AirDrop). The owner picked **A + C**:

| | Trade-off |
|---|---|
| **A. Finder Quick Action** + **C. Drop zone** (chosen) | No signing or Xcode; testable on the owner's Mac. Right-click, not the Share menu. Drop zone is the fallback and shows status. |
| B. Real Share extension (Swift appex) | The exact AirDrop feel, but a new native project, signing (the agent is ad hoc signed), and a risk of not loading on every Mac. Deferred. |
| Shortcuts app "Show in Share Sheet" | Appears in the Share menu with no code, but a shortcut cannot be installed from a script reliably (signing, prompts). Can be mentioned in the guide as a do-it-yourself path; not shipped. |

For how phone → Mac uploads: one streaming `PUT` per file (below) beat multipart (buffers or parses, awkward for 2 GB)
and chunked custom protocols (no benefit on a reliable link).

## Design

### Switches (Settings, on the Mac; agent config `Bridge`)

Two new booleans next to `Clipboard`, both default off: **`FilesFromPhone`** ("Receive files from the phone") and
**`FilesToPhone`** ("Send files to the phone"). Each has its own line in the bridge card, its own status line, and
an entry in `PRIVACY.md`. Also on the phone's bridge screen (the Android PR for each direction).

### Phone → Mac

- **Endpoint** `PUT /api/bridge/files/in?name=<urlencoded>&size=<bytes>` on the phone-facing ports only; token and
  gate as the clipboard endpoint, and checked in the same order: who, platform (501), switch (403), *then* the body.
  The body is the raw file, streamed to disk; never buffered.
- **Where:** `~/Downloads/PhoneBox/` (created on first use). Written as a hidden `.part` file in the same folder, then
  renamed atomically when the body ends and the byte count matches `size` (when `size` was given); a short or dropped
  body deletes the part file.
- **`size` is a hint, not required (my call):** a content URI from another app often reports no size or a wrong one.
  When given, it is checked before the body is read (413, 507) and must match at the end. When missing, the phone
  streams with no length (chunked), the agent counts bytes while writing, aborts and deletes the part file past
  2 GiB or if the disk drops under the 1 GiB margin, and still needs at least that margin free before it starts.
- **Folder access (my call, check first):** `~/Downloads` is protected by macOS privacy controls (TCC), and the agent is
  a background launchd job signed ad hoc (#269), so a prompt nobody sees or a silent refusal is possible, and a grant
  may be lost when the updater replaces the binary. A spike writes to `~/Downloads/PhoneBox/` from a launchd job on
  the owner's Mac **before step 1**. If it fails, the save location changes and that **comes back to the owner**
  (candidates: a folder under Application Support, or one the owner picks); nothing else is decided in advance.
- **Names:** only the last path component is used; control characters, `/`, `\`, leading dots and reserved names are
  removed; at most 200 bytes (the extension kept); an empty result becomes `file`. An existing name is never
  overwritten (`name (1).ext`). The name is data, never part of a command.
- **Limits (my call):** 2 GiB per file; refuse with 413 before reading when `size` is over it, 507 when the free space
  would drop below the size plus 1 GiB; two uploads at once at most (429 above). No type filtering: nothing is opened.
- **Quarantine (my call):** each received file gets the `com.apple.quarantine` attribute, as AirDrop and browsers do, so
  Gatekeeper treats a received `.command` or app like a downloaded one. The builder verifies on the real Mac that
  images and PDFs still open normally.
- **Feedback:** 200 with `{ name, size }` (the final name); the phone shows it. The Mac shows a notification through the
  existing notifier ("Received photo.jpg from your phone"), like AirDrop; clicking is not required. The agent logs
  counts only, never names.

### Mac → phone

- **Local trigger (not under `/api/bridge`, not on the Wi-Fi list):** `PUT /api/transfer/send?name=...` and
  `GET /api/transfer/status` on the local port. A request is accepted if it is either a same-origin browser request
  from the settings page (the drop zone: it must **carry** a same-origin `Origin`, not merely lack a foreign one, since
  `RequestGate` also lets header-less requests through), or carries a **local key** in a header: a random value the
  agent writes with mode 0600 to `~/Library/Application Support/PhoneBox/transfer.json` (with the port) when the switch
  is turned on and deletes when it is turned off. The Quick Action reads that file. **Honest bar:** this keeps out other
  users and sandboxed apps that can reach `127.0.0.1` but not read the owner's files; it does **not** stop another
  process running as the owner, which can read the file (and which can already change the agent's config through the
  same local port today). That is the intended bar, the same as the rest of the local API. The body is the file's bytes
  (the agent never reads a path the caller names, so the trigger cannot be used to copy arbitrary files off the Mac).
- **Which phone (my call):** one phone is assumed (the owner has one). With several paired phones that have the file
  switch on, any of them may fetch a file, and the first `done` deletes it; no target picker, and the pull request says so.
- **Outbox:** the file is streamed into a private folder (`~/Library/Caches/PhoneBox/outbox/<id>`, `id` = 128 random
  bits), at most 2 GiB per file and 4 GiB in total, **kept 1 hour** for a phone that is not connected yet, then
  deleted; deleted after the phone confirms; the whole folder is emptied at agent start and when the switch is off.
  The trigger answers 202 with the id and queues; `/api/transfer/status` lists id, state (waiting, sending, delivered,
  expired, failed) and size, never the contents.
- **Telling the phone:** a `file` event on the bridge stream with `{ id, name, size, mime }` (metadata only). Because the
  outbox outlives the two-minute replay window, the phone also fetches `GET /api/bridge/files/out` (the pending list)
  whenever its stream (re)connects. `GET /api/bridge/files/out/{id}` streams the bytes (with `Range`), and
  `POST /api/bridge/files/out/{id}/done` tells the agent to delete it.
- **Stream gating:** the bridge stream and the phone service run when the clipboard **or** either file switch is on;
  each event type is delivered only while its own switch is on.
- **Phone side:** the service downloads to `MediaStore` (API 29+): images → Pictures/PhoneBox, video → Movies/PhoneBox,
  everything else → Downloads/PhoneBox, with `IS_PENDING` until complete. Below API 29 (my call, no new permission)
  the file goes to the app's own external Downloads folder and the notification says where. A completed file
  raises a notification that opens it (several files: one summary). The service notification text is generalised from
  "sharing the clipboard" to what is actually on.

### Android: share target

- A new exported activity **`SendFilesToMacActivity`** with `ACTION_SEND` and `ACTION_SEND_MULTIPLE` for `*/*`
  (separate from `SendToMacActivity`, which stays text-only), labelled "Send to Mac".
- **Rejects** any stream URI whose scheme is not `content`, or whose authority is this app's own, and a bad `Parcelable`
  extra counts as nothing to send (as the text activity treats unreadable extras).
- **Confirms first** (my call, for the same reason the text flow does): a dialog with the first few names and sizes
  and a "Send to Mac" button, `filterTouchesWhenObscured`. If it proves annoying it is a one-line change to drop it.
- **Uploads in a foreground service** (type `dataSync`) so a closed activity does not kill a big transfer, with a progress
  notification and a Cancel button; the result is a final notification ("3 files sent" / the reason it failed). The
  request body is streamed from `ContentResolver.openInputStream` with a fixed length when the size is known
  (`setFixedLengthStreamingMode`), so nothing is held in memory. `AgentLink.request` takes a `ByteArray` today; the
  builder adds a streaming variant next to it rather than changing it.
- With the file switch off, or no bridge pairing, the entry says so instead of failing silently
  (as `BridgeSend.sendable` does for text); like the text entry it is disabled until the bridge is on
  (`BridgeEntryPoints`), so it is not in every user's share sheet.

### Mac: Quick Action and drop zone

- **Quick Action:** the agent writes `~/Library/Services/Send to phone.workflow` when `FilesToPhone` is turned on and
  removes it when turned off. It runs a short shell script on the selected files: for each file `curl -T` to the local
  trigger with the key read from `transfer.json`, and a one-line result via `osascript display notification`.
  Arguments always passed as arguments, never spliced into a script. **Risk, to be checked first with a throwaway
  spike on the owner's Mac:** a hand-written `.workflow` that Finder lists under Quick Actions without Automator, and
  whether macOS asks for extra permission (Files and Folders). If it cannot be made reliable, the fallback is to ship
  the script and a guide section, and the drop zone stays the supported way.
- **Drop zone:** in the settings page's bridge card, a dashed area "Drop files to send to your phone"; disabled with
  the reason when the switch is off; shows progress and each file's state from `/api/transfer/status`.

## Security and privacy

- Opt-in per direction. Files and names are never logged (counts only), never stored beyond the Mac folder, the
  1-hour outbox and the phone's own storage; `PRIVACY.md` says so.
- Phone-facing paths need the pairing token over pinned TLS and are on the Wi-Fi allow-list, each justified in the PR.
  The local trigger needs the local key or the same-origin settings page, and is never reachable from Wi-Fi.
- Received files are inert: sanitized name, atomic write, no overwrite, quarantine attribute, never opened by the agent.
- Share target: content URIs only, never this app's own; confirmation before sending.
- Ids are 128 random bits and only the paired phone's key can fetch them.
- Sideloaded app, owner's own devices: the threat model is another app on the phone or another process on the Mac
  pushing a file to the other device, and a hostile name or oversized body, not a network attacker.

## Testing

- Agent: xUnit for the name sanitizer (traversal, `..`, absolute paths, control characters, long names, collisions),
  size and disk-space refusals, the order of checks (no body read when off or not allowed), `.part` cleanup on a
  short or aborted body, the outbox (TTL, total cap, cleanup on start and on switch-off), the local trigger's two ways
  in and its refusal without them, that `/api/transfer/*` is not on the Wi-Fi allow-list, and that bridge paths are
  still refused locally.
- Android: the project's Kotlin checks (`tools/kotlin-tests/BridgeChecks.kt` pattern) for URI rejection, name and size
  handling, MediaStore routing by type; build + lint.
- Live, with the owner at the phone and Mac in one session (no screenshots, no adb swipes; the demo agent runs in its
  own HOME and minimal PATH so it does not take the phone from the installed agent): phone → Mac with a photo, a video
  over 1 GB, several files, a hostile name; Mac → phone with the Quick Action and the drop zone, over USB and Wi-Fi;
  phone offline then back within the hour; the switch off in each direction; a file over the limit; unplugging mid-way.
  Anything not done there is stated as not tested on hardware in the pull request.

## Order of work

Worked in its own worktree cut from `personal/phone-bridge` (branch `personal/file-transfer`, worktree
`/Volumes/T7 MAC/Code/pb-files`), never in the shared folder and never in `pb-personal` while another session works
there. Merged back into `personal/phone-bridge` only when each step's tests are green. Nothing is pushed, released or
installed on the Mac or phone without the owner's say-so (installing replaces the running agent and the app). Each step
is still a reviewable commit group; there is no line limit. The old stacked branches `pr/bridge-*` stay untouched.

1. Agent: receive endpoint, folder writer, `FilesFromPhone` switch and card, allow-list, tests.
2. Android: share target, confirmation, upload service, bridge-screen switch. Live check phone → Mac.
3. Agent: outbox, local trigger and key, file event, pending list, download and done endpoints, `FilesToPhone`, tests.
4. Android: stream/service changes, download to MediaStore, notifications. Live check with `curl` against the trigger.
5. Mac: Quick Action installer and drop zone.

Two spikes on the owner's Mac go first, throwaway and labelled as such: writing to `~/Downloads/PhoneBox/` from a launchd
job (before step 1), and a hand-written `.workflow` that Finder lists under Quick Actions (before step 3), so a dead end
is found early. Step 5 ends with a release to the fork through `scripts/personal-release.sh` (version `0.9.29.2` or the
next one), shown to the owner before it is published, and the first install by hand.

## Left to the builder

Exact event field names, the settings-card layout, the notification wording and its translations, the shape of the
streaming request in `AgentLink`, whether the outbox folder is the cache or the config directory, how the Quick Action
shows an error, and the guide text. Anything that would change what the owner sees (a different save location, the
confirmation dialog, the limits) comes back to the owner first.
