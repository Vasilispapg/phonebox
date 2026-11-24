#!/usr/bin/env bash
# Takes upstream's latest into the personal branch and keeps the phone bridge on top of it.
#
#   scripts/personal-update.sh [--skip-tests] [--release] [--publish]
#
# 1. fetches upstream (origin = ersinkoc/phonebox) and, if it has something new, merges it into this branch;
# 2. the version numbers are the only lines that would conflict on every upstream release (upstream bumps them, so does
#    the personal build), so they are put back to upstream's values before the merge and set again afterwards:
#    upstream's version + a fourth part (0.9.30.1), or the same version with the fourth part raised (0.9.29.2) when
#    upstream has not moved but this branch has changed;
# 3. any OTHER conflict stops the script and puts everything back as it was (the tag personal/before-update-<time> marks
#    the starting point), with the files listed: that is the case that needs a person;
# 4. runs the checks (release metadata, bridge checks, stream test, the agent's tests, the Windows target build, the
#    Android lint and debug build); --skip-tests leaves out the slow ones (agent tests, Windows build, Android build);
# 5. --release then builds the release into dist/ (a dry run, scripts/personal-release.sh); --publish also pushes the
#    branch to the fork and publishes it there. Nothing leaves this Mac without --publish.
#
# PERSONAL_UPSTREAM (default origin/main) and PERSONAL_BRANCH (default personal/phone-bridge) exist for testing the
# script on a throwaway branch.
set -euo pipefail

BRANCH="${PERSONAL_BRANCH:-personal/phone-bridge}"
UPSTREAM="${PERSONAL_UPSTREAM:-origin/main}"
FORK_REMOTE="fork"
CSPROJ="agent/PhoneBox.Agent/PhoneBox.Agent.csproj"
GRADLE="android/app/build.gradle.kts"

tests=1; release=0; publish=0
for arg in "$@"; do
  case "$arg" in
    --skip-tests) tests=0 ;;
    --release) release=1 ;;
    --publish) release=1; publish=1 ;;
    -h|--help) sed -n '2,21p' "$0"; exit 0 ;;
    *) echo "unknown option: $arg" >&2; exit 2 ;;
  esac
done

die() { echo "personal-update: $*" >&2; exit 1; }
step() { printf '\n== %s\n' "$*"; }

cd "$(dirname "$0")/.."
[ -z "$(git status --porcelain)" ] || { git status --short >&2; die "the working tree is not clean; commit or stash first"; }
[ "$(git rev-parse --abbrev-ref HEAD)" = "$BRANCH" ] || die "not on $BRANCH (on $(git rev-parse --abbrev-ref HEAD))"

# the version files, read and written by one small helper (python3 is there for tools/check-release.py anyway)
versions() {  # prints: <agent version> <android versionName> <versionCode>
  python3 - "$CSPROJ" "$GRADLE" <<'PY'
import re, sys
cs = open(sys.argv[1], encoding='utf-8').read()
gr = open(sys.argv[2], encoding='utf-8').read()
print(re.search(r'<Version>([^<]+)</Version>', cs)[1], re.search(r'versionName = "([^"]+)"', gr)[1], re.search(r'versionCode = (\d+)', gr)[1])
PY
}
set_versions() {  # <version> <versionCode>
  python3 - "$CSPROJ" "$GRADLE" "$1" "$2" <<'PY'
import re, sys
cs_path, gr_path, version, code = sys.argv[1:5]
cs = open(cs_path, encoding='utf-8').read()
gr = open(gr_path, encoding='utf-8').read()
cs, n1 = re.subn(r'<Version>[^<]+</Version>', f'<Version>{version}</Version>', cs, count=1)
gr, n2 = re.subn(r'versionName = "[^"]+"', f'versionName = "{version}"', gr, count=1)
gr, n3 = re.subn(r'versionCode = \d+', f'versionCode = {code}', gr, count=1)
assert n1 == n2 == n3 == 1, 'version lines not found'
open(cs_path, 'w', encoding='utf-8', newline='').write(cs)
open(gr_path, 'w', encoding='utf-8', newline='').write(gr)
PY
}
upstream_versions() {  # <ref>: the same three values as upstream has them
  local d; d="$(mktemp -d)"
  git show "$1:$CSPROJ" > "$d/cs"; git show "$1:$GRADLE" > "$d/gr"
  python3 - "$d/cs" "$d/gr" <<'PY'
import re, sys
cs = open(sys.argv[1], encoding='utf-8').read(); gr = open(sys.argv[2], encoding='utf-8').read()
print(re.search(r'<Version>([^<]+)</Version>', cs)[1], re.search(r'versionCode = (\d+)', gr)[1])
PY
  rm -rf "$d"
}

step "Fetching upstream"
case "$UPSTREAM" in */*) git fetch "${UPSTREAM%%/*}" --quiet ;; esac
news="$(git rev-list --count "HEAD..$UPSTREAM")"
read -r cur_version _ cur_code <<<"$(versions)"
echo "this branch: $cur_version (versionCode $cur_code); upstream has $news new commit(s)"

if [ "$news" != 0 ]; then
  start_tag="personal/before-update-$(date -u +%Y%m%d-%H%M%S)"
  git tag "$start_tag"
  echo "starting point: $start_tag"

  # 1. our version lines back to what upstream had when we last merged, so that upstream's bump merges cleanly
  base="$(git merge-base HEAD "$UPSTREAM")"
  read -r base_version base_code <<<"$(upstream_versions "$base")"
  set_versions "$base_version" "$base_code"
  git commit -q -am "Personal build: version lines back to upstream's ($base_version) for the merge"

  # 2. the merge itself
  step "Merging $UPSTREAM"
  if ! git merge --no-edit "$UPSTREAM" >/tmp/personal-update-merge.log 2>&1; then
    echo "The merge has conflicts in:" >&2
    git diff --name-only --diff-filter=U >&2
    git merge --abort || true
    git reset --hard -q "$start_tag"
    die "stopped; everything is back as it was ($start_tag). Merge by hand: git merge $UPSTREAM, keep both sides' intent."
  fi
  tail -3 /tmp/personal-update-merge.log
  rm -f /tmp/personal-update-merge.log
fi

# 3. the version: upstream's plus our fourth part. A release must be newer than the one installed, so the fourth part
#    only goes up, and versionCode goes up by one over every earlier personal release (and over upstream's).
read -r now_version _ now_code <<<"$(versions)"
three="$(echo "$now_version" | cut -d. -f1-3)"
if [ "$news" != 0 ] || [ "$now_version" = "$three" ]; then
  if [ "$three" = "$(echo "$cur_version" | cut -d. -f1-3)" ]; then
    part="$(( $(echo "$cur_version" | awk -F. '{print ($4==""?0:$4)}') + 1 ))"
  else
    part=1
  fi
  new_code="$(( (now_code > cur_code ? now_code : cur_code) + 1 ))"
  set_versions "$three.$part" "$new_code"
  git commit -q -am "Personal build: version $three.$part"
fi
read -r final_version _ final_code <<<"$(versions)"
echo "version now: $final_version (versionCode $final_code)"

# 4. the checks
step "Release metadata and bridge checks"
python3 tools/check-release.py
python3 tools/test-bridge-checks.py | tail -1
python3 tools/test-agent-stream.py | tail -1
if [ "$tests" = 1 ]; then
  step "Agent tests"
  (cd agent && DOTNET_ROLL_FORWARD=Major dotnet test PhoneBox.Agent.Tests --nologo -v q | tail -3)
  step "Windows target build"
  (cd agent
   DOTNET_ROLL_FORWARD=Major dotnet restore PhoneBox.Agent -p:EnableWindowsTargeting=true -p:TargetFramework=net10.0-windows10.0.19041.0 >/dev/null
   DOTNET_ROLL_FORWARD=Major dotnet build PhoneBox.Agent --no-restore -f net10.0-windows10.0.19041.0 -p:EnableWindowsTargeting=true --nologo -v q | grep -E 'error|Error\(s\)' | sort -u || true
   DOTNET_ROLL_FORWARD=Major dotnet restore PhoneBox.Agent >/dev/null)
  step "Android lint and debug build"
  (cd android && JAVA_HOME=/opt/homebrew/opt/openjdk@17 ANDROID_HOME="$HOME/Library/Android/sdk" ./gradlew -q lintDebug assembleDebug)
  echo "android: ok"
fi
[ -z "$(git status --porcelain)" ] || { git status --short >&2; die "the checks left changes in the working tree"; }

# 5. the release
if [ "$release" = 1 ]; then
  if [ "$publish" = 1 ]; then
    step "Pushing $BRANCH to the fork"
    git push "$FORK_REMOTE" "$BRANCH"
    scripts/personal-release.sh --publish
  else
    scripts/personal-release.sh
  fi
else
  echo
  echo "Done. Nothing was pushed or published. Next: scripts/personal-update.sh --publish (or scripts/personal-release.sh)."
fi
