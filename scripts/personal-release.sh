#!/usr/bin/env bash
# Builds the personal PhoneBox release (agent for macOS arm64 + Android APK) into dist/ and, only with --publish, creates
# a GitHub release for it on the fork. Without --publish nothing leaves this Mac: it is a dry run that prints what a
# release would contain. See docs/PERSONAL-BUILD.md.
#
#   scripts/personal-release.sh [--publish] [--no-smoke]
#
# The release is named the way the updater (UpdateService.SelectAssets) expects: PhoneBox-<v>.apk,
# PhoneBoxAgent-<v>-osx-arm64.zip and SHA256SUMS-<v>.txt.
set -euo pipefail

FORK_REPO="Vasilispapg/phonebox"
FORK_REMOTE="fork"
BRANCH="personal/phone-bridge"
RID="osx-arm64"
# The key of the app on the owner's phone: the phone only accepts an update signed with it.
EXPECTED_SIGNER_SHA256="4e03ff8c216691ced1619e15b906c1f7fff2bfdbaf71405419e51676745ea61e"
SMOKE_PORT=18776

publish=0
smoke=1
for arg in "$@"; do
  case "$arg" in
    --publish) publish=1 ;;
    --no-smoke) smoke=0 ;;
    -h|--help) sed -n '2,10p' "$0"; exit 0 ;;
    *) echo "unknown option: $arg" >&2; exit 2 ;;
  esac
done

die() { echo "personal-release: $*" >&2; exit 1; }
step() { printf '\n== %s\n' "$*"; }

cd "$(dirname "$0")/.."
root="$(pwd)"

# --- checks before anything is built ------------------------------------------------------------------------------
step "Checking the working tree"
[ -z "$(git status --porcelain)" ] || { git status --short >&2; die "the working tree is not clean; commit or stash first"; }
[ "$(git rev-parse --abbrev-ref HEAD)" = "$BRANCH" ] || die "not on $BRANCH (on $(git rev-parse --abbrev-ref HEAD))"

version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' agent/PhoneBox.Agent/PhoneBox.Agent.csproj | head -n1)"
android_version="$(sed -n 's/.*versionName = "\([^"]*\)".*/\1/p' android/app/build.gradle.kts | head -n1)"
[ -n "$version" ] || die "no <Version> in agent/PhoneBox.Agent/PhoneBox.Agent.csproj"
[ "$version" = "$android_version" ] || die "agent version $version differs from the Android versionName '$android_version'"
printf '%s' "$version" | grep -Eq '^[0-9]+\.[0-9]+\.[0-9]+(\.[0-9]+)?$' || die "unexpected version '$version'"
tag="v$version"
echo "version $version (tag $tag), branch $BRANCH, commit $(git rev-parse --short HEAD)"

fork_url="$(git remote get-url "$FORK_REMOTE" 2>/dev/null || true)"
case "$fork_url" in
  *"$FORK_REPO"*) ;;
  *) die "remote '$FORK_REMOTE' is '$fork_url', not $FORK_REPO" ;;
esac

if git rev-parse -q --verify "refs/tags/$tag" >/dev/null; then
  [ "$publish" = 0 ] || die "tag $tag already exists locally; bump the version"
  echo "note: tag $tag already exists locally (a release would be refused)"
fi
if git ls-remote --exit-code --tags "$FORK_REMOTE" "refs/tags/$tag" >/dev/null 2>&1; then
  [ "$publish" = 0 ] || die "tag $tag already exists on $FORK_REPO; bump the version"
  echo "note: tag $tag already exists on $FORK_REPO (a release would be refused)"
fi
# the release's tag is made on the fork's branch: it has to be the commit that is built here
remote_head="$(git ls-remote "$FORK_REMOTE" "refs/heads/$BRANCH" | cut -f1)"
if [ "$remote_head" != "$(git rev-parse HEAD)" ]; then
  [ "$publish" = 0 ] || die "$FORK_REMOTE/$BRANCH is at '${remote_head:-nothing}', not at HEAD; git push $FORK_REMOTE $BRANCH first"
  echo "note: $FORK_REMOTE/$BRANCH is not at HEAD (a release would be refused until it is pushed)"
fi

rm -rf out dist
mkdir -p dist

# --- the agent (the same publish line as release.yml's osx-arm64 job) -----------------------------------------------
step "Agent $RID"
dotnet publish agent/PhoneBox.Agent -c Release -f net10.0 -r "$RID" --self-contained true -p:PublishSingleFile=true -o out --nologo
# Ad hoc by default. macOS ties the Accessibility permission to the signature, and an ad hoc one changes with every build
# (the permission has to be granted again after an update). PHONEBOX_MAC_SIGN_IDENTITY (a certificate of your own in the
# login keychain, e.g. made with tools/make-macos-signing-cert.sh) keeps it: one certificate, one requirement.
if [ -n "${PHONEBOX_MAC_SIGN_IDENTITY:-}" ]; then
  codesign --force --sign "$PHONEBOX_MAC_SIGN_IDENTITY" --identifier net.oxog.phonebox.agent --timestamp=none out/PhoneBoxAgent
else
  codesign --force --sign - out/PhoneBoxAgent
fi
codesign --verify --strict out/PhoneBoxAgent

# What release.yml does (Compress-Archive -Path out/*): the files at the zip's root, no folder around them. The updater
# extracts the zip into a staging folder, requires PhoneBoxAgent at its root and moves every file next to the running one.
zip_name="PhoneBoxAgent-$version-$RID.zip"
(cd out && zip -q -X -r "$root/dist/$zip_name" . -x '.DS_Store' -x '*/.DS_Store')
entries="$(unzip -Z1 "dist/$zip_name")"
printf '%s\n' "$entries" | grep -qx 'PhoneBoxAgent' || die "$zip_name has no PhoneBoxAgent at its root"
if printf '%s\n' "$entries" | grep -Eq '^(\./|/)|/$'; then die "$zip_name has folder or ./ entries; the files must sit at its root"; fi

# the zip is what gets installed: check the copy that comes out of it
check="$(mktemp -d "${TMPDIR:-/tmp}/phonebox-zip-check.XXXXXX")"
trap 'rm -rf "$check"' EXIT
unzip -q "dist/$zip_name" -d "$check/agent"
chmod +x "$check/agent/PhoneBoxAgent" # the updater sets the mode itself after extracting
codesign --verify --strict "$check/agent/PhoneBoxAgent" || die "the agent in $zip_name does not verify"
[ "$(file -b "$check/agent/PhoneBoxAgent" | grep -c 'arm64')" -ge 1 ] || die "the agent in $zip_name is not an arm64 binary"
if [ "$smoke" = 1 ]; then
  step "Smoke test of the agent from the zip (own HOME, no adb on PATH, port $SMOKE_PORT)"
  mkdir -p "$check/home"
  env -i HOME="$check/home" PATH=/usr/bin:/bin python3 tools/smoke-agent.py "$check/agent/PhoneBoxAgent" --version "$version" --port "$SMOKE_PORT"
fi

# --- the phone app ------------------------------------------------------------------------------------------------
step "Android APK"
export JAVA_HOME="${JAVA_HOME:-/opt/homebrew/opt/openjdk@17}"
export ANDROID_HOME="${ANDROID_HOME:-$HOME/Library/Android/sdk}"
# the Mac's Android debug key (public defaults); only these four variables are read by android/app/build.gradle.kts
export PHONEBOX_STORE_FILE="$HOME/.android/debug.keystore"
export PHONEBOX_STORE_PASSWORD="android"
export PHONEBOX_KEY_ALIAS="androiddebugkey"
export PHONEBOX_KEY_PASSWORD="android"
[ -f "$PHONEBOX_STORE_FILE" ] || die "$PHONEBOX_STORE_FILE not found"
(cd android && ./gradlew assembleRelease --console=plain -q)
apk_name="PhoneBox-$version.apk"
cp android/app/build/outputs/apk/release/app-release.apk "dist/$apk_name"

apksigner="$ANDROID_HOME/build-tools/35.0.0/apksigner"
[ -x "$apksigner" ] || die "apksigner not found at $apksigner"
certs="$(PATH="$JAVA_HOME/bin:$PATH" "$apksigner" verify --print-certs "dist/$apk_name")" || die "apksigner does not verify $apk_name"
signers="$(printf '%s\n' "$certs" | grep -c 'certificate SHA-256 digest:' || true)"
signer="$(printf '%s\n' "$certs" | sed -n 's/^Signer #1 certificate SHA-256 digest: *//p' | head -n1 | tr 'A-F' 'a-f')"
[ "$signers" = 1 ] || die "$apk_name has $signers signer certificates, expected 1"
[ "$signer" = "$EXPECTED_SIGNER_SHA256" ] || die "$apk_name is signed with $signer, not with the phone's key $EXPECTED_SIGNER_SHA256; the phone would refuse the update"
echo "apksigner: signature verifies; signer certificate SHA-256 $signer (matches the phone's key)"

# --- checksums (the updater checks every download against this file) ------------------------------------------------
step "Checksums"
sums_name="SHA256SUMS-$version.txt"
(cd dist && shasum -a 256 "$apk_name" "$zip_name" > "$sums_name")
cat "dist/$sums_name"

step "What a release would contain"
for f in dist/*; do
  printf '%-48s %12s bytes  sha256 %s\n' "$(basename "$f")" "$(stat -f %z "$f")" "$(shasum -a 256 "$f" | cut -d' ' -f1)"
done

base="$(printf '%s' "$version" | cut -d. -f1-3)" # upstream's version: the first three components
notes="Personal build of PhoneBox $version: the phone bridge (clipboard, notifications, calls, app icons), based on upstream PhoneBox $base.

Files:
- $apk_name: the phone app
- $zip_name: the agent for macOS (Apple silicon), used by the in-place update
- $sums_name: SHA-256 of the two, checked by the updater"

if [ "$publish" = 0 ]; then
  step "Dry run: nothing was published"
  echo "A release would be made with:"
  echo "  gh release create $tag dist/* --repo $FORK_REPO --target $BRANCH --title \"PhoneBox $version (personal build)\" --notes <below>"
  printf '%s\n' "$notes" | sed 's/^/    /'
  echo "Run again with --publish to create it."
  exit 0
fi

step "Publishing $tag to $FORK_REPO"
gh release create "$tag" dist/* --repo "$FORK_REPO" --target "$BRANCH" --title "PhoneBox $version (personal build)" --notes "$notes"
echo "Released: https://github.com/$FORK_REPO/releases/tag/$tag"
