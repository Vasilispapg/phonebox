#!/usr/bin/env bash
# Lets the PhoneBox app on the phone read the text of "sensitive" notifications (login codes, one-time passwords).
#
#   scripts/personal-allow-sensitive.sh [adb serial]
#
# Since Android 15 the system hides such notifications' text from notification listeners ("Sensitive notification
# content hidden"), so the Mac shows that sentence instead of the code. The app op RECEIVE_SENSITIVE_NOTIFICATIONS lifts it
# for one app, and only adb can set it (the app cannot ask). It survives app updates but not an uninstall: run this again
# after an uninstall. Personal build only: upstream does not ship or document this.
set -euo pipefail
PKG="net.oxog.phonebox.deskpanel"
ADB="${ADB:-adb}"
SERIAL_ARGS=()
[ $# -ge 1 ] && SERIAL_ARGS=(-s "$1")

"$ADB" ${SERIAL_ARGS[@]+"${SERIAL_ARGS[@]}"} shell pm path "$PKG" >/dev/null 2>&1 || { echo "PhoneBox is not installed on the phone (or no phone is connected)" >&2; exit 1; }
"$ADB" ${SERIAL_ARGS[@]+"${SERIAL_ARGS[@]}"} shell cmd appops set "$PKG" RECEIVE_SENSITIVE_NOTIFICATIONS allow
echo "now: $("$ADB" ${SERIAL_ARGS[@]+"${SERIAL_ARGS[@]}"} shell cmd appops get "$PKG" RECEIVE_SENSITIVE_NOTIFICATIONS)"
