# Personal build

The branch `personal/phone-bridge` carries the whole phone bridge (clipboard, notifications, calls, app icons, the adb
clipboard helper). It is never sent upstream. Releases of it come from the fork (`Vasilispapg/phonebox`) and update the
installed agent and the phone app in place, the same way upstream's releases do.

## Version numbers

The updater installs a release of the configured repository when its tag parses as a `System.Version` and is greater than
the running version. A personal release uses upstream's version plus a fourth component: `0.9.29.1` is newer than
`0.9.29` and older than `0.9.30`. The agent's `<Version>` (`agent/PhoneBox.Agent/PhoneBox.Agent.csproj`) and the Android
`versionName` (`android/app/build.gradle.kts`) must be the same; `versionCode` goes up by one with every release.
`tools/check-release.py` accepts the four-part version and looks for the upstream release notes under its first three
parts.

## The short way: `scripts/personal-update.sh`

```bash
scripts/personal-update.sh               # fetch upstream, merge it, set the version, run the checks
scripts/personal-update.sh --release     # ... and build the release into dist/ (a dry run)
scripts/personal-update.sh --publish     # ... push the branch to the fork and publish the release there
```

The version lines are the only ones that conflict on every upstream release, so the script puts them back to upstream's
values before the merge and sets them again after it (`<upstream version>.1`, or the fourth part raised when upstream has
not moved). Any other conflict stops it and puts everything back (the tag `personal/before-update-<time>`); then merge by
hand as described below. `--skip-tests` leaves out the slow checks. Without `--publish` nothing leaves this Mac.

## Files between the phone and the Mac

`0.9.29.2` adds sending files both ways (Settings › Phone bridge › the two file switches, and the phone's Phone bridge
screen): the share sheet's **Send file to Mac**, the Finder Quick Action **Send to phone** and the settings page's drop
zone. The design is in `docs/GUIDE.md` (Phone bridge) and `PRIVACY.md`. Received files are saved with the quarantine mark.
An update of the agent can change its signature (always with an ad hoc one), so macOS may ask again for access to the Downloads folder.

## Keeping it up to date

```bash
git fetch origin
git merge origin/main            # one merge, resolve conflicts keeping both sides' intent
# set the version: 0.9.29.1 -> 0.9.29.2 for a change of ours, or <new upstream version>.1 after taking a new release
#   agent/PhoneBox.Agent/PhoneBox.Agent.csproj  <Version>
#   android/app/build.gradle.kts                versionName and versionCode (+1)
python3 tools/check-release.py
python3 tools/test-bridge-checks.py
python3 tools/test-agent-stream.py
(cd agent && DOTNET_ROLL_FORWARD=Major dotnet test PhoneBox.Agent.Tests)
git commit -am "Personal build: version ..."
git push fork personal/phone-bridge
scripts/personal-release.sh            # dry run: builds into dist/, prints what a release would contain
scripts/personal-release.sh --publish  # creates the release v<version> on the fork
```

The script refuses a dirty tree, any other branch, a version that differs between the agent and the Android app, a tag
that already exists (locally or on the fork) and a fork branch that is not at the commit being built. It builds the
macOS arm64 agent with the same `dotnet publish` line as `.github/workflows/release.yml`, signs it ad hoc, zips the
files of `out/` at the zip's root (as upstream's zip), smoke-tests the agent from that zip in an empty HOME on port
18776 (`--no-smoke` skips it), builds the APK signed with the Mac's Android debug key, and fails unless the APK's signer
certificate has the SHA-256 of the key of the app on the phone (the phone only accepts an update signed with it). It
writes `dist/PhoneBox-<v>.apk`, `dist/PhoneBoxAgent-<v>-osx-arm64.zip` and `dist/SHA256SUMS-<v>.txt` (the updater
checks every download against that file). `dist/` and `out/` are ignored by git.

## Settings, once

In the agent's settings (`config.json`):

```json
"updates": { "repo": "Vasilispapg/phonebox", "auto": true }
```

The agent then checks `https://api.github.com/repos/Vasilispapg/phonebox/releases/latest` and installs the zip and the
APK of the newest release. This works for an agent that runs from the install folder
(`~/Library/Application Support/PhoneBox/app`); a PhoneBox.app from a disk image updates from a `.dmg` release asset,
which this script does not make.

## Mac signature and the Accessibility permission

Upstream signs the macOS agent with its own certificate, so the designated requirement stays the same across releases and
macOS keeps the Accessibility permission. An ad hoc signature changes with every build: after the first personal update,
and after each one, the permission (shortcuts that press keys) has to be granted again. To avoid it after the first time,
make a certificate of your own once (`tools/make-macos-signing-cert.sh`, import the `.p12` into the login keychain) and
run the script with `PHONEBOX_MAC_SIGN_IDENTITY` set to its name or SHA-1; keep using the same certificate.

## The daily check

A launchd job (`~/Library/LaunchAgents/net.oxog.phonebox.upstream-check.plist`, every day at 12:30, or when the Mac wakes
up after that) runs `scripts/personal-auto-check.sh`. When upstream has a release it has not looked at, it tries
`scripts/personal-update.sh` on a throwaway copy of the branch and shows a notification: ready (merges cleanly, all checks
pass), needs a hand-merge (the files are named) or failed a check. It never touches the working tree, never pushes and never
publishes. Log: `~/Library/Logs/PhoneBox-upstream-check.log`; the last trial: `~/Library/Application Support/PhoneBox-auto/`.
Run it by hand with `scripts/personal-auto-check.sh --force`; stop it with
`launchctl bootout gui/$(id -u)/net.oxog.phonebox.upstream-check`. The worktree must be on `personal/phone-bridge` and its disk mounted.
