import { API, PANEL_HEADERS, PREVIEW, buildLayout, bridge, curPage, h, haptic, icon, idle, noteTouch, pages, t, toast } from '../main.js';
import { W } from '../registry.js';

// ---------------------------------------------------------------- terminal
// A shell on the computer (TerminalService). The card shows a status and the last lines seen; the shell itself is in a
// full-screen xterm.js overlay opened by a tap. Output comes from a long poll (GET out: a binary frame
// [flags:1][cursor:8, big-endian][data…], read with arrayBuffer so no byte is lost); typed text goes out in numbered
// batches, one request at a time, and a batch whose answer was lost is resent with the same number, so the agent applies
// it once. Nothing typed or shown is logged or stored: it lives in this page's memory and in the `in` request's `d`.
// The card asks only GET state (which never keeps a shell alive); polling the shell happens only while the overlay is open.

const WAIT_MS = 2500, STATE_EVERY_MS = 8000, COMPACT_BELOW = 340;
// the agent's cap per batch, and a cap on the escaped text so the request line stays under the agent's 8 KB limit
const MAX_BYTES = 4096, MAX_ESCAPED = 6000;
// "pageId/widgetId" → { lines, ended, seq, shell, pending }: survives closing the overlay and a rebuild of the page,
// in memory only. An entry goes with its widget (prune), and the lines once the agent says there is no shell.
const remembered = new Map();
const live = new Set(); // the keys of the terminal widgets on the panel now
let pruning = 0;
/** After a rebuild (which destroys every widget, then creates the new ones): forget the widgets that are gone. */
function prune() {
  if (pruning) return;
  pruning = setTimeout(() => { pruning = 0; for (const k of remembered.keys()) if (!live.has(k)) remembered.delete(k); }, 0);
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const body = async (r) => { try { return await r.json(); } catch (_) { return {}; } };
const keep = (e) => e.preventDefault(); // a key-row tap must not take the focus (and the keyboard) away from the terminal

async function call(path, { method = 'GET', ms = 8000, signal } = {}) {
  const ctl = new AbortController(), stop = () => ctl.abort();
  signal?.addEventListener('abort', stop);
  const timer = setTimeout(stop, ms);
  try { return await fetch(API + path, { method, cache: 'no-store', headers: PANEL_HEADERS, signal: ctl.signal }); }
  finally { clearTimeout(timer); signal?.removeEventListener('abort', stop); }
}

let vendor = null;
/** Loads xterm.js and its fit addon on the first open, once; resolves false when they are missing. */
function loadVendor() {
  if (window.Terminal && window.FitAddon) return Promise.resolve(true);
  if (!vendor) {
    const add = (el) => new Promise((ok, fail) => { el.onload = ok; el.onerror = fail; document.head.append(el); });
    const script = (src) => { const s = document.createElement('script'); s.src = src; return add(s); };
    const css = document.createElement('link');
    css.rel = 'stylesheet';
    css.href = 'vendor/xterm/xterm.css';
    document.head.append(css);
    vendor = script('vendor/xterm/xterm.js').then(() => script('vendor/xterm/addon-fit.js'))
      .then(() => !!(window.Terminal && window.FitAddon), () => false)
      .then((ok) => { if (!ok) { vendor = null; css.remove(); } return ok; });
  }
  return vendor;
}

const wellFormed = (s) => (s.toWellFormed ? s.toWellFormed() : s.replace(/[\uD800-\uDBFF](?![\uDC00-\uDFFF])|(?<![\uD800-\uDBFF])[\uDC00-\uDFFF]/g, '�'));
/** The longest head of `text` that fits one request, cut between characters: [head, rest]. */
function cut(text) {
  let bytes = 0, escaped = 0, i = 0;
  for (const ch of text) {
    const cp = ch.codePointAt(0);
    const b = cp < 0x80 ? 1 : cp < 0x800 ? 2 : cp < 0x10000 ? 3 : 4;
    const e = cp < 0x80 && /[\w.!~*'()-]/.test(ch) ? 1 : 3 * b; // encodeURIComponent's output
    if (i && (bytes + b > MAX_BYTES || escaped + e > MAX_ESCAPED)) break;
    bytes += b; escaped += e; i += ch.length;
  }
  return [wellFormed(text.slice(0, i)), text.slice(i)];
}

/** Ctrl with the next key: a letter (or @ [ \ ] ^ _) becomes its control character, ? becomes DEL, space NUL. */
function control(s) {
  if ([...s].length !== 1) return s;
  const code = s.toUpperCase().charCodeAt(0);
  if (code >= 64 && code <= 95) return String.fromCharCode(code & 31);
  return s === '?' ? '\x7f' : s === ' ' ? '\x00' : s;
}

/** The full-screen terminal for one widget. `mem` is the widget's memory (lines, ended, and the input batch whose answer
 *  has not been seen); `done({ lines, ended })` runs once it has closed (a refused shell closes it too). */
function overlay({ wq, title, scale, mem, done }) {
  const css = getComputedStyle(document.body);
  const term = new window.Terminal({ scrollback: 2000, cursorBlink: false, fontSize: Math.round(14 * scale),
    fontFamily: 'ui-monospace, Menlo, "Roboto Mono", monospace',
    // a link a program prints (OSC 8) does nothing when tapped: xterm.js would confirm() and then window.open() it, which
    // can take the panel's WebView away to that page until the app restarts (and no web-links addon is loaded)
    linkHandler: { activate() {} },
    theme: { background: css.getPropertyValue('--bg').trim() || '#06080b', foreground: css.getPropertyValue('--text').trim() || '#eef2f6' } });
  const fit = new window.FitAddon.FitAddon();
  term.loadAddon(fit);

  let gen = 0; // bumped by close and restart: loops of an older generation stop at their next step
  let closed = false, ready = false, ended = false, polling = false, pollCtl = null;
  let cursor = 0, queue = '', flushTimer = 0, sending = false, ctrl = false, lastSize = '', sizeTimer = 0, watch = 0, touched = 0;
  // stream: bumped when the shell changed under the overlay, so an answer read from the old one is not shown.
  // Old output (a reattach's backlog, see onData): `backlog` says the next frame is old; writes are numbered, and while
  // the last old one (oldUntil) is not parsed yet (parsed counts the writes xterm.js has finished) nothing is sent.
  let stream = 0, backlog = false, written = 0, parsed = 0, oldUntil = 0;
  const replaying = () => parsed < oldUntil;

  const button = (cls, label, fn) => {
    const b = h('button', cls, label);
    b.type = 'button';
    b.addEventListener('pointerdown', keep);
    b.addEventListener('mousedown', keep);
    b.onclick = fn;
    return b;
  };
  const net = h('span', 'tm-net');
  const say = (text) => { net.textContent = text || ''; };
  // Try again (open failed: the shell, if any, is left alone) and Restart (the shell ended: close it, open a fresh one)
  const retryB = button('tm-retry', t('w.terminal.retry'), () => { haptic(); start(); });
  const restartB = button('tm-restart', t('w.terminal.restart'), () => { haptic(); restart(); });
  const endB = button('tm-end', t('w.terminal.end'), () => { haptic(); close({ end: true }); });
  const closeB = button('tm-close', t('w.terminal.close'), () => { haptic(); close(); });
  retryB.hidden = restartB.hidden = true;
  const arrow = (x) => () => send((term.modes.applicationCursorKeysMode ? '\x1bO' : '\x1b[') + x);
  const ctrlB = button('tm-ctrl', 'Ctrl', () => { ctrl = !ctrl; ctrlB.classList.toggle('on', ctrl); });
  const keys = [button('', 'Esc', () => send('\x1b')), button('', 'Tab', () => send('\t')), ctrlB,
    button('', '←', arrow('D')), button('', '↑', arrow('A')), button('', '↓', arrow('B')), button('', '→', arrow('C')),
    ...['|', '~', '/', '-', '_', '`'].map((c) => button('', c, () => send(c))),
    button('tm-paste', t('w.terminal.paste'), paste)];
  const screen = h('div', 'tm-screen');
  const root = h('div', 'tm-ov', h('div', 'tm-bar', h('span', 'tm-ov-title', title), net, retryB, restartB, endB, closeB), screen, h('div', 'tm-keys', keys));
  const compact = () => root.classList.toggle('tm-compact', (window.visualViewport?.height ?? window.innerHeight) < COMPACT_BELOW);
  document.body.append(root);
  document.body.classList.add('term-open'); // main.js: no page changes, idle return, dimming or rebuilds meanwhile
  try {
    compact();
    term.open(screen);
    try { fit.fit(); } catch (_) {}
    for (const [k, v] of [['autocapitalize', 'off'], ['autocorrect', 'off'], ['spellcheck', 'false'], ['autocomplete', 'off']]) term.textarea?.setAttribute(k, v);
    // xterm.js answers the queries in what it parses (who are you, where is the cursor, which colours) through onData,
    // as if typed. Old output replayed on a reattach still holds the queries a program made long ago (vim, tmux, fzf):
    // answering them again would type "0;276;0c" or ";1R" into whatever is in front now. So while that backlog is parsed
    // nothing goes out; typing in that short moment (about a tenth of a second) is dropped too.
    term.onData((s) => { if (!replaying()) send(s); });
    term.focus();
    keyboard(true);
  } catch (e) {
    root.remove();
    document.body.classList.remove('term-open');
    try { term.dispose(); } catch (_) {}
    throw e;
  }

  /** Android 11+: the page shrinks above the on-screen keyboard only while this overlay is open and showing. */
  function keyboard(on) { try { bridge?.setKeyboardResize?.(on); } catch (_) {} }

  // ------------------------------------------------ input: batched, numbered, one request at a time
  function send(s) {
    if (!s || ended || closed) return;
    // typing is a touch (once a second at most): the panel knows someone is there (rules, the night screen's wake)
    const now = Date.now();
    if (now - touched >= 1000) { touched = now; noteTouch(); }
    if (ctrl) { s = control(s); ctrl = false; ctrlB.classList.remove('on'); }
    queue += s;
    if (!flushTimer) flushTimer = setTimeout(flush, 10);
  }
  // the clipboard is read only here, on a tap of Paste
  function paste() {
    haptic();
    let text = '';
    try { text = String(bridge?.readClipboardText?.() ?? ''); } catch (_) {}
    if (!text) { toast(t('w.terminal.pasteEmpty')); return; }
    text = text.replace(/\r?\n/g, '\r'); // as xterm.js pastes: a line break is Enter
    ctrl = false; ctrlB.classList.remove('on');
    // inside the markers the clipboard may not end the paste early (and run what follows): no ESC at all
    if (term.modes.bracketedPasteMode) text = `\x1b[200~${text.replace(/\x1b\[201~/g, '').replace(/\x1b/g, '')}\x1b[201~`;
    send(text);
  }
  // mem.pending is the batch on its way: { seq, shell, text }, numbered for the shell `open` named. It stays until an answer
  // says it landed, so a batch whose overlay closed before the answer came is settled by the next open (see attach()).
  // Every batch names its shell (s=), and the agent refuses one meant for another shell. The agent applies each number once:
  // {applied:false} always means this number is already in, whichever try (or a transport's duplicate) put it there.
  // Two panels sharing one shell may pick the same number; the second then counts as landed and its text is dropped.
  async function flush() {
    flushTimer = 0;
    if (sending || !ready || ended || closed) return;
    if (!mem.pending) {
      if (!queue) return;
      const [text, rest] = cut(queue);
      queue = rest;
      mem.pending = { seq: mem.seq, shell: mem.shell, text };
    }
    const g = gen, p = mem.pending;
    sending = true;
    let delay = 500, conflicts = 0;
    try {
      while (g === gen && !ended && mem.pending === p) {
        let r = null;
        try { r = await call(`/terminal/in?${wq}&s=${encodeURIComponent(p.shell)}&seq=${p.seq}&d=${encodeURIComponent(p.text)}`, { method: 'POST' }); } catch (_) {}
        if (g !== gen) break; // closed or restarted: the batch stays pending for the next open to settle
        if (r?.ok) { mem.pending = null; mem.seq = p.seq + 1; say(''); break; }
        if (r?.status === 409) {
          const b = await body(r);
          if (g !== gen) break;
          if (b.error === 'other-session') {
            // the shell changed under us: this batch, and the rest of what was typed for the old one, are not sent
            if (mem.pending === p) mem.pending = null;
            queue = '';
            toast(t('w.terminal.notSent'));
            ready = false;
            attach(g);
            break;
          }
          // the shell counts differently: its number, after the usual wait (a misbehaving answer never loops at network speed)
          if (Number.isSafeInteger(b.expected) && b.expected > 0 && b.expected !== p.seq) p.seq = b.expected;
          if (++conflicts >= 3) { ready = false; attach(g); break; } // the numbers do not settle: ask open where the shell is
        } else if (r?.status === 410) { finished(); break; }
        else if (r && [403, 404, 501].includes(r.status)) { close(); break; }
        else if (r && (r.status === 400 || r.status === 413)) { mem.pending = null; break; } // cannot be sent as it is (cut() keeps under the limits)
        // no answer, a timeout, 504 "result unknown" or 503 "busy" (an earlier batch is still being written, nothing was
        // typed): the same number again, after a growing wait. Never counted as landed.
        say(t('w.terminal.reconnecting'));
        await sleep(delay);
        delay = Math.min(3000, delay * 2);
      }
    } finally { sending = false; }
    if (queue || mem.pending) flush(); // typed meanwhile, or a restart's new shell is waiting: never left behind
  }

  // ------------------------------------------------ output: one long poll at a time, only while someone can see it
  const paused = () => idle() || document.body.classList.contains('night');
  async function poll(g) {
    if (polling || g !== gen || ended || !ready || paused()) return;
    polling = true;
    let delay = 500;
    try {
      while (g === gen && !ended && !paused()) {
        pollCtl = new AbortController();
        const st = stream;
        let r = null, buf = null;
        try {
          r = await call(`/terminal/out?${wq}&since=${cursor}&wait=${WAIT_MS}`, { ms: WAIT_MS + 8000, signal: pollCtl.signal });
          if (r.ok) buf = await r.arrayBuffer();
        } catch (_) {}
        if (g !== gen) break;
        if (st !== stream) continue; // the shell changed meanwhile: read the new one from its start
        if (buf && buf.byteLength >= 9) { delay = 500; say(''); frame(buf); continue; }
        if (r?.status === 410) { finished(); break; }
        if (r && [403, 404, 501].includes(r.status)) { close(); break; }
        say(t('w.terminal.reconnecting'));
        await sleep(delay);
        delay = Math.min(3000, delay * 2);
      }
    } finally { polling = false; pollCtl = null; }
  }
  function frame(buf) {
    const v = new DataView(buf), flags = v.getUint8(0);
    if (flags & 2) term.reset(); // the agent no longer has what came after our cursor: start over from what it kept
    const data = new Uint8Array(buf, 9);
    // Old output is only the backlog read right after a (re)attach: its queries are not answered again, see onData.
    // A reset frame in a live session (more than the agent keeps arrived since the last poll: `yes`, `cat` of a big
    // file) is new output: what it asks is answered, and keys typed meanwhile (Ctrl-C!) go out. xterm.js parses in
    // order, so output after the backlog is live too.
    const old = backlog;
    backlog = false;
    if (data.length) {
      const n = ++written;
      if (old) oldUntil = n;
      term.write(new Uint8Array(data), () => { parsed = Math.max(parsed, n); });
    }
    cursor = Number(v.getBigUint64(1));
    if (flags & 1) finished();
  }
  function finished() {
    if (ended) return;
    ended = true;
    queue = '';
    mem.pending = null; // nothing more reaches an ended shell
    term.write(`\r\n${t('w.terminal.processEnded')}\r\n`);
    restartB.hidden = false;
    say('');
  }

  // ------------------------------------------------ open, size, close
  /** Opens (or reattaches to) the shell, retrying while answers are lost, then settles a pending batch from what the agent
   *  says: numbered for another shell (it ended, was reaped or replaced, even by an open whose answer was lost) it is
   *  dropped; below nextSeq it landed; at nextSeq (or above) it goes again first, numbered nextSeq. */
  async function attach(g) {
    let delay = 500;
    while (g === gen) {
      let r = null, b = null;
      try {
        r = await call(`/terminal/open?${wq}&cols=${term.cols}&rows=${term.rows}`, { method: 'POST', ms: 15000 });
        if (r.ok) b = await r.json();
      } catch (_) {}
      if (g !== gen) return;
      const next = b?.nextSeq, shell = b?.shell;
      if (b && Number.isSafeInteger(next) && next > 0 && typeof shell === 'string' && shell) {
        // A batch at nextSeq goes again with its number however long the overlay was closed: a write stuck in the pty
        // (the program in front is not reading) still reads as nextSeq although it will complete, so dropping it would
        // lose text typed later (it would reuse the number and count as landed when the stuck write ends) and set the
        // counter off. The cost: a batch can arrive late, when the program reads at last; it is what was typed.
        if (mem.pending && (mem.pending.shell !== shell || mem.pending.seq < next)) mem.pending = null;
        else if (mem.pending) mem.pending.seq = next;
        if (shell !== mem.shell && cursor) {
          // another shell than the one on screen (it was replaced while the overlay was open): start over from its start
          stream++;
          pollCtl?.abort();
          cursor = 0;
          term.reset();
          oldUntil = 0;
        }
        // reattached at the start: what comes first is the shell's old output (see onData)
        backlog = b.attached === true && cursor === 0;
        mem.seq = next;
        mem.shell = shell;
        lastSize = `${term.cols}x${term.rows}`;
        ready = true;
        say('');
        poll(g);
        flush();
        return;
      }
      if (r && [403, 404, 501].includes(r.status)) { close(); return; }
      if (r && r.status !== 504 && r.status < 502) { // refused (429, 500, …): left to the user, the shell untouched
        say(t(r.status === 429 ? 'w.terminal.busy' : 'w.terminal.startFailed'));
        retryB.hidden = false;
        return;
      }
      // no answer, a timeout or 504: the open may have happened; asking again only reattaches
      say(t('w.terminal.reconnecting'));
      await sleep(delay);
      delay = Math.min(3000, delay * 2);
    }
  }
  function start() {
    const g = ++gen;
    ready = ended = backlog = false; cursor = 0; queue = ''; oldUntil = 0;
    retryB.hidden = restartB.hidden = true;
    term.reset();
    say('');
    attach(g);
  }
  async function restart() {
    const g = ++gen;
    pollCtl?.abort();
    mem.pending = null;
    try { await call(`/terminal/close?${wq}`, { method: 'POST' }); } catch (_) {}
    if (g === gen && !closed) start();
  }
  function resize() {
    if (closed) return;
    compact();
    try { fit.fit(); } catch (_) {}
    const size = `${term.cols}x${term.rows}`;
    if (!ready || ended || size === lastSize) return;
    lastSize = size;
    call(`/terminal/size?${wq}&cols=${term.cols}&rows=${term.rows}`, { method: 'POST' }).then((r) => { if (!r.ok) lastSize = ''; }, () => { lastSize = ''; });
  }
  const onResize = () => { clearTimeout(sizeTimer); sizeTimer = setTimeout(resize, 150); };
  const onBack = () => close();
  const onVisibility = () => keyboard(!document.hidden); // another app in front, or the screen off: the page is full height again
  window.addEventListener('resize', onResize);
  window.visualViewport?.addEventListener('resize', onResize);
  window.addEventListener('phonebox-terminal-close', onBack);
  document.addEventListener('visibilitychange', onVisibility);
  // polling waits while the screen is off, another app is in front or night mode is on, and picks up after
  watch = setInterval(() => poll(gen), 1000);

  /** The last three lines of the screen, as plain text, for the card. */
  function lastLines() {
    const b = term.buffer.active, out = [];
    for (let y = b.baseY + b.cursorY; y >= 0 && out.length < 3; y--) {
      const line = b.getLine(y)?.translateToString(true) ?? '';
      if (line.trim() || out.length) out.unshift(line);
    }
    return out.join('\n');
  }
  function close({ end = false } = {}) {
    if (closed) return;
    closed = true;
    gen++;
    const wasEnded = ended;
    let lines = mem.lines;
    try {
      pollCtl?.abort();
      clearTimeout(flushTimer); clearTimeout(sizeTimer); clearInterval(watch);
      window.removeEventListener('resize', onResize);
      window.visualViewport?.removeEventListener('resize', onResize);
      window.removeEventListener('phonebox-terminal-close', onBack);
      document.removeEventListener('visibilitychange', onVisibility);
      lines = lastLines();
      term.dispose();
    } catch (_) {}
    finally {
      keyboard(false);
      root.remove();
      document.body.classList.remove('term-open');
      noteTouch(); // closing is a touch: the idle return, dimming and the night screen start their wait now
    }
    if (end) { mem.pending = null; lines = ''; } // the shell is gone: the card shows no lines of it
    // End session waits for the agent, so the card's next look at the state already sees no shell
    const ending = end ? call(`/terminal/close?${wq}`, { method: 'POST' }).catch(() => {}) : Promise.resolve();
    ending.then(() => done({ lines, ended: end || wasEnded }));
  }

  start();
  return { close };
}

W.terminal = {
  needs: ['tick'],
  create(box, { o, page, w, appearance }) {
    box.classList.add('tm');
    // which widget is asking: the agent checks this one's "Allow a shell" option ("pageId/stackId:cardId" in a Smart Stack)
    const key = `${page.id}/${w.id}`, wq = 'widget=' + encodeURIComponent(key);
    const mem = remembered.get(key) || { lines: '', ended: false, seq: 1, shell: '', pending: null };
    remembered.set(key, mem);
    live.add(key);
    prune();
    const label = h('span');
    const status = h('span', 'tm-status', h('i', 'tm-dot'), label);
    const lines = h('pre', 'tm-lines');
    const hint = h('div', 'tm-hint');
    const open = h('button', 'tm-open', t('w.terminal.open'));
    open.type = 'button';
    open.hidden = true;
    box.append(h('div', 'tm-head', h('span', 'tm-title', o.title || t('w.terminal.title')), status), lines, h('div', 'tm-foot', hint, open));

    let alive = true, lastPoll = 0, polling = false, opening = false, ov = null;
    const HINTS = { locked: 'w.terminal.hintLocked', off: 'w.terminal.masterOff', unsupported: 'w.terminal.unsupported', missing: 'w.terminal.update' };
    const LABELS = { unsupported: 'w.terminal.unavailable', missing: 'w.terminal.unavailable', offline: 'w.offline' };
    const show = (state) => {
      box.dataset.state = state;
      status.querySelector('.ico')?.remove();
      if (state === 'locked') status.prepend(icon('lock'));
      label.textContent = t(LABELS[state] || `w.terminal.${state}`);
      hint.textContent = HINTS[state] ? t(HINTS[state]) : '';
      open.hidden = !!HINTS[state];
      lines.textContent = mem.lines;
      lines.hidden = !mem.lines || !!HINTS[state];
    };
    const fromState = (s) => !s.supported ? 'unsupported' : !s.enabled ? 'off' : !s.control ? 'locked' : s.open ? 'live' : mem.ended ? 'ended' : 'ready';
    const refresh = async () => {
      if (polling) return;
      polling = true;
      lastPoll = Date.now();
      try {
        const r = await call(`/terminal/state?${wq}`, { ms: 5000 });
        if (!alive || ov) return;
        // 404: an agent older than this widget, or this page isn't one of this phone's pages in Settings › Phones
        const s = r.ok ? await r.json() : null;
        if (!alive || ov) return;
        if (r.status === 404 || (s && s.open !== true)) mem.lines = ''; // no shell (ended, closed when idle, or never): no lines of one
        show(r.status === 404 ? 'missing' : s ? fromState(s) : 'offline');
      } catch (_) { if (alive && !ov) show('offline'); }
      finally { polling = false; }
    };
    // idle(): nobody can see the panel (screen off, another app in front, a hidden tab)
    const visible = () => !idle() && !document.body.classList.contains('night') && pages[curPage] === page && !box.closest('.stack-item:not(.active)');

    open.onclick = async () => {
      if (ov || opening) return;
      haptic();
      if (PREVIEW) { toast(t('w.terminal.notInPreview')); return; } // the layout editor's preview has no xterm.js
      opening = true;
      const ok = await loadVendor();
      opening = false;
      if (!alive || ov) return;
      if (!ok) { toast(t('w.terminal.notHere'), 'err'); return; }
      mem.ended = false;
      show('live');
      const done = (r) => {
        mem.lines = r.lines;
        mem.ended = r.ended;
        if (alive) {
          ov = null;
          show(r.ended ? 'ended' : 'live');
          lastPoll = 0;
          if (o.control === true && !PREVIEW) refresh();
        }
        buildLayout(); // a rebuild held back while the overlay was open
      };
      try { ov = overlay({ wq, title: o.title || t('w.terminal.title'), scale: (appearance?.fontScale || 100) / 100, mem, done }); }
      catch (_) { ov = null; show('ready'); toast(t('w.terminal.notHere'), 'err'); }
    };

    if (o.control !== true) show('locked');
    else if (PREVIEW) show('ready');
    else { label.textContent = t('w.loading'); lines.textContent = mem.lines; lines.hidden = !mem.lines; }

    return {
      update() {
        // the card only asks for the state, at a slow pace, while it is showing and the overlay is closed
        if (!alive || ov || PREVIEW || o.control !== true || !visible()) return;
        if (Date.now() - lastPoll > STATE_EVERY_MS) refresh();
      },
      destroy() { alive = false; live.delete(key); prune(); ov?.close(); ov = null; },
    };
  },
};
