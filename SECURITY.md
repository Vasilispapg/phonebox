# Security policy

Please report vulnerabilities through [GitHub private vulnerability reporting](https://github.com/ersinkoc/phonebox/security/advisories/new).
Include the affected version, operating system, connection mode (USB or paired Wi-Fi), reproduction steps and impact.
Do not include access tokens, pairing keys, calendar URLs or personal session transcripts.

Security fixes target the latest release. Install the latest agent and Android app together when a fix changes both sides.

PhoneBox can run shortcuts and control local applications. Keep the agent on loopback, its key-checked USB tunnel or its
authenticated, pinned-TLS Wi-Fi connection. Custom HTML widgets are sandboxed; they must use the documented widget messaging interface.

The Terminal widget (macOS only; not available on Linux or Windows yet) is a remote shell that runs as your user. It
is off by default and needs two switches: **Allow terminals on this computer** in the agent's settings (changeable
only from the local settings page, never from the phone) and the widget's own **Allow a shell on the computer**
option. It is protected by the paired key, the pinned TLS connection and the local-only settings. Two trust points
remain. Any phone paired with the agent can use it, and separate phones are not an authorization boundary: a phone's
device id is stated by the phone, so someone who holds the Wi-Fi key can name another phone's id and attach to that
phone's shell. And the settings port on `127.0.0.1` has no key, so any program running as you on the computer can open
a shell through it once both switches are on, the same trust as the rest of the local API. Running shells end when a
switch is turned off, the widget is removed or its page disabled, the idle time runs out, or a new Wi-Fi key is saved
(**New key**, then save; this also ends a USB phone's shell). The shell runs under PhoneBox's macOS privacy permissions
(what macOS has allowed PhoneBox, such as access to Documents or Desktop, the shell has too; a first access raises a
prompt on the Mac that the phone cannot answer). Reports about getting around the switches, the key, the pinned TLS or
the local-only settings are in scope.
