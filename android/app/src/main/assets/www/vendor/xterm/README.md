# Vendored: xterm.js

Terminal emulator for the Terminal widget, shipped inside the Android app (no CDN, no network at runtime).
Do not edit these files; replace by re-vendoring (npm pack the pinned versions, verify the integrity, copy the same files).

| File | Package | Version | Taken from the tarball |
|------|---------|---------|------------------------|
| xterm.js | @xterm/xterm | 6.0.0 | package/lib/xterm.js (UMD, defines window.Terminal) |
| xterm.css | @xterm/xterm | 6.0.0 | package/css/xterm.css |
| addon-fit.js | @xterm/addon-fit | 0.11.0 | package/lib/addon-fit.js (UMD, defines window.FitAddon.FitAddon) |
| LICENSE-xterm | @xterm/xterm | 6.0.0 | package/LICENSE (MIT) |
| LICENSE-addon-fit | @xterm/addon-fit | 0.11.0 | package/LICENSE (MIT) |

Byte-identical copies; source maps (*.map), ESM (*.mjs) builds and other addons are left out on purpose.

Source: https://github.com/xtermjs/xterm.js  (https://xtermjs.org/)
Tarballs: https://registry.npmjs.org/@xterm/xterm/-/xterm-6.0.0.tgz and
https://registry.npmjs.org/@xterm/addon-fit/-/addon-fit-0.11.0.tgz

npm tarball integrity (sha512, equal to what the registry reports):

- @xterm/xterm@6.0.0: sha512-TQwDdQGtwwDt+2cgKDLn0IRaSxYu1tSUjgKarSDkUM0ZNiSRXFpjxEsvc/Zgc5kq5omJ+V0a8/kIM2WD3sMOYg==
- @xterm/addon-fit@0.11.0: sha512-jYcgT6xtVYhnhgxh3QgYDnnNMYTcf8ElbxxFzX0IZo+vabQqSPAjC3c1wJrKB5E19VwQei89QCiZZP86DCPF7g==
