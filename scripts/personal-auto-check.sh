#!/usr/bin/env bash
# Looks once a day (launchd, see docs/PERSONAL-BUILD.md) whether upstream has a new release and, if it has, TRIES
# scripts/personal-update.sh on a throwaway copy of the personal branch, then tells the owner the result with a macOS
# notification. It never touches the owner's working tree or branch, never pushes, never publishes: the owner decides.
#
#   scripts/personal-auto-check.sh [--force] [--no-notify]
#
# --force runs the trial even when upstream's latest release was already looked at; --no-notify prints instead of notifying.
# The log is ~/Library/Logs/PhoneBox-upstream-check.log; the last trial's own output is in
# ~/Library/Application Support/PhoneBox-auto/last-run.log.
set -uo pipefail

UPSTREAM_REPO="ersinkoc/phonebox"
BRANCH="personal/phone-bridge"
TRIAL_BRANCH="personal/auto-trial"
STATE="$HOME/Library/Application Support/PhoneBox-auto"
LOG="$HOME/Library/Logs/PhoneBox-upstream-check.log"
export PATH="/opt/homebrew/bin:/opt/homebrew/sbin:/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin:$PATH"
export LC_ALL=en_US.UTF-8

force=0; notify=1
for arg in "$@"; do
  case "$arg" in
    --force) force=1 ;;
    --no-notify) notify=0 ;;
    -h|--help) sed -n '2,11p' "$0"; exit 0 ;;
    *) echo "unknown option: $arg" >&2; exit 2 ;;
  esac
done

mkdir -p "$STATE" "$(dirname "$LOG")"
exec >>"$LOG" 2>&1
echo "--- $(date '+%Y-%m-%d %H:%M:%S') personal-auto-check"

tell() {  # <title> <text>
  echo "notify: $1 — $2"
  if [ "$notify" = 1 ]; then
    osascript - "$1" "$2" <<'OSA' || true
on run argv
  display notification (item 2 of argv) with title (item 1 of argv)
end run
OSA
  fi
}

cd "$(dirname "$0")/.." || exit 1
repo="$(pwd)"
[ "$(git rev-parse --abbrev-ref HEAD)" = "$BRANCH" ] || { echo "the worktree is not on $BRANCH; skipping"; exit 0; }

latest="$(gh release view --repo "$UPSTREAM_REPO" --json tagName -q .tagName 2>/dev/null || true)"
[ -n "$latest" ] || { echo "could not read upstream's latest release (offline?); skipping"; exit 0; }
if [ ! -f "$STATE/upstream-tag" ]; then
  # first run: what this branch is based on counts as already seen
  base3="$(sed -n 's:.*<Version>\([0-9]*\.[0-9]*\.[0-9]*\).*:\1:p' agent/PhoneBox.Agent/PhoneBox.Agent.csproj | head -n1)"
  echo "v$base3" > "$STATE/upstream-tag"
fi
seen="$(cat "$STATE/upstream-tag")"
echo "upstream latest: $latest; last looked at: $seen"
if [ "$latest" = "$seen" ] && [ "$force" = 0 ]; then echo "nothing new"; exit 0; fi

# the trial: a throwaway worktree on a throwaway branch cut from the personal branch
trial="$STATE/trial"
git worktree remove --force "$trial" >/dev/null 2>&1 || true
git branch -D "$TRIAL_BRANCH" >/dev/null 2>&1 || true
rm -rf "$trial"
tags_before="$(git tag -l 'personal/before-update-*')"
git worktree add -q -b "$TRIAL_BRANCH" "$trial" "$BRANCH" || { echo "could not create the trial worktree"; exit 1; }
[ -f android/local.properties ] && cp android/local.properties "$trial/android/local.properties"

(cd "$trial" && PERSONAL_BRANCH="$TRIAL_BRANCH" bash scripts/personal-update.sh) > "$STATE/last-run.log" 2>&1
rc=$?
version="$(sed -n 's:.*<Version>\([^<]*\)</Version>.*:\1:p' "$trial/agent/PhoneBox.Agent/PhoneBox.Agent.csproj" | head -n1)"
conflicts="$(sed -n '/The merge has conflicts in:/,/personal-update: stopped/p' "$STATE/last-run.log" | grep -v -e 'conflicts in:' -e 'personal-update: stopped' | tr '\n' ' ')"
failure="$(grep -E 'personal-update:|FAIL|error|Failed' "$STATE/last-run.log" | head -n1)"

# clean up: the worktree, the branch and the "before-update" tags the trial made
git worktree remove --force "$trial" >/dev/null 2>&1 || true
git branch -D "$TRIAL_BRANCH" >/dev/null 2>&1 || true
git worktree prune
for t in $(git tag -l 'personal/before-update-*'); do
  echo "$tags_before" | grep -qx "$t" || git tag -d "$t" >/dev/null
done

echo "$latest" > "$STATE/upstream-tag"
if [ "$rc" = 0 ]; then
  tell "PhoneBox: upstream $latest is ready" "It merges cleanly with the phone bridge and all checks pass ($version). To install it: scripts/personal-update.sh --publish."
elif [ -n "$conflicts" ]; then
  tell "PhoneBox: upstream $latest needs a hand-merge" "Conflicts in: $conflicts Nothing was changed."
else
  tell "PhoneBox: upstream $latest failed the checks" "${failure:-see ~/Library/Application Support/PhoneBox-auto/last-run.log} Nothing was changed."
fi
echo "done (exit status of the trial: $rc)"
exit 0
