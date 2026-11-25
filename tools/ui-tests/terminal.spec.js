const { test, expect } = require('@playwright/test');

// Terminal: a card on the page and a full-screen xterm.js overlay. The demo (tools/pages/demo.js) answers the agent's
// /api/terminal routes with an echo "shell" ("demo$ ", "echo X", "exit") and lets the tests look inside through
// window.__terminalDemo. Everything is checked through the DOM and the demo's records: no screenshots.
const KEY = 'test/term';

async function layout(page, { control = true, enabled = true, missing = false, night = { enabled: false } } = {}) {
  await page.addInitScript((missing) => {
    // the phone's bridge, with a clipboard the tests fill (window.__clip) and the keyboard resize it was asked for (window.__kb)
    window.Android = { battery: () => JSON.stringify({ level: 82, plugged: true, temp: 29.5 }), transport: () => 'usb', version: () => 'demo',
      is24Hour: () => true, haptic() {}, buzz() {}, setBrightness() {}, setVolumeKeys() {}, readClipboardText: () => window.__clip ?? '',
      setKeyboardResize: (key, on) => { (window.__kb ||= []).push(on); } };
    // an agent without /api/terminal (older than the widget): set before the demo's backend exists
    if (missing) Object.defineProperty(window, '__terminalDemo', { configurable: true, get() { return this.__td; }, set(v) { v.missing = true; this.__td = v; } });
  }, missing);
  await page.goto('/panel/');
  await page.evaluate(({ control, enabled, night }) => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({ night, shortcuts: [], terminal: { enabled },
      pages: [{ id: 'test', title: 'Test', enabled: true, widgets: [{ id: 'term', type: 'terminal', x: 0, y: 0, w: 6, h: 3, options: { control } }] },
        { id: 'two', title: 'Two', enabled: true, widgets: [] }] }));
  }, { control, enabled, night });
  await page.reload();
  await expect(page.locator('.w-terminal')).toBeVisible();
}

const demo = (page, fn, arg) => page.evaluate(fn, arg);
const inputs = (page) => demo(page, (k) => window.__terminalDemo.sessions.get(k)?.inputs || [], KEY);
const typed = async (page) => (await inputs(page)).join('');
const requests = (page, route) => demo(page, (r) => window.__terminalDemo.requests.filter((x) => x.route === r).length, route);
// the overlay's screen as text, a line per row (the DOM renderer draws spaces as no-break spaces)
const screen = (page) => page.evaluate(() => [...(document.querySelector('.tm-ov .xterm-rows')?.children || [])]
  .map((row) => row.textContent.replace(/\u00a0/g, ' ').trimEnd()).join('\n').trim());

async function openOverlay(page) {
  await page.locator('.tm-open').click();
  await expect(page.locator('.tm-ov')).toBeVisible();
  await expect.poll(() => screen(page)).toContain('demo$');
  await page.locator('.tm-ov .xterm-helper-textarea').focus();
}

test('card shows locked state when the widget option is off', async ({ page }) => {
  await layout(page, { control: false });
  await expect(page.locator('.tm-hint')).toContainText('Allow a shell on the computer');
  await expect(page.locator('.tm-status')).toContainText('locked');
  await expect(page.locator('.tm-open')).toBeHidden();
  await page.waitForTimeout(500);
  expect(await demo(page, () => window.__terminalDemo.requests.length)).toBe(0); // nothing is asked while it is locked
});

test('card shows Open and status when allowed', async ({ page }) => {
  await layout(page);
  await expect(page.locator('.tm-status')).toContainText('ready');
  await expect(page.locator('.tm-open')).toBeVisible();
  expect(await demo(page, () => window.__terminalDemo.requests.every((r) => r.route === 'state'))).toBe(true);
  // the switch on the computer off
  await layout(page, { enabled: false });
  await expect(page.locator('.tm-hint')).toContainText('Allow terminals on this computer');
  await expect(page.locator('.tm-open')).toBeHidden();
});

test('overlay opens, typed text reaches the shell and its answer appears', async ({ page }) => {
  const errors = []; page.on('pageerror', (e) => errors.push(String(e)));
  await layout(page);
  await openOverlay(page);
  expect(await page.evaluate(() => document.body.classList.contains('term-open'))).toBe(true);
  const ta = page.locator('.tm-ov .xterm-helper-textarea');
  for (const [k, v] of Object.entries({ autocapitalize: 'off', autocorrect: 'off', spellcheck: 'false', autocomplete: 'off' })) await expect(ta).toHaveAttribute(k, v);
  await page.keyboard.type('echo hello');
  await page.keyboard.press('Enter');
  await expect.poll(() => typed(page)).toBe('echo hello\r');
  await expect.poll(() => screen(page)).toMatch(/demo\$ echo hello\s+hello\s+demo\$/);
  // keys meant for the terminal do not drive the panel behind it
  await page.keyboard.press('Escape');
  await page.keyboard.press('ArrowRight');
  await expect(page.locator('#overview')).toBeHidden();
  expect(await page.evaluate(() => document.querySelector('#pager').scrollLeft)).toBe(0);
  const sizes = await demo(page, (k) => window.__terminalDemo.sessions.get(k).sizes, KEY);
  expect(sizes[0][0]).toBeGreaterThanOrEqual(20);
  expect(sizes[0][1]).toBeGreaterThanOrEqual(5);
  expect(errors).toEqual([]);
});

test('key row sends Esc, Tab, arrows and sticky Ctrl+C as 0x1b, 0x09, escape sequences and 0x03', async ({ page }) => {
  await layout(page);
  await openOverlay(page);
  const key = (name) => page.locator('.tm-keys button', { hasText: new RegExp(`^${name.replace(/[|]/g, '\\$&')}$`) }).click();
  for (const k of ['Esc', 'Tab', '←', '↑', '↓', '→', '|', '~', '/', '-', '_', '`']) await key(k);
  await key('Ctrl');
  await expect(page.locator('.tm-keys .tm-ctrl')).toHaveClass(/on/);
  await page.keyboard.type('c');
  await expect(page.locator('.tm-keys .tm-ctrl')).not.toHaveClass(/on/);
  await page.keyboard.type('c'); // Ctrl cleared itself: a plain c
  await expect.poll(() => typed(page)).toBe('\x1b\t\x1b[D\x1b[A\x1b[B\x1b[C|~/-_`\x03c');
  // a full-screen program asked for application cursor keys
  await demo(page, (k) => window.__terminalDemo.write(k, '\x1b[?1h'), KEY);
  await page.waitForTimeout(300);
  await key('↑');
  await expect.poll(() => typed(page)).toMatch(/\x1bOA$/);
});

test('a resent batch is not applied twice', async ({ page }) => {
  await layout(page);
  await openOverlay(page);
  await demo(page, () => { window.__terminalDemo.loseNext = 1; });
  await page.keyboard.type('ls');
  await expect.poll(() => demo(page, () => window.__terminalDemo.requests.filter((r) => r.route === 'in' && r.seq === '1').length)).toBe(2);
  await demo(page, () => { window.__terminalDemo.lose504 = 1; });
  await page.keyboard.type('x');
  await expect.poll(() => demo(page, () => window.__terminalDemo.requests.filter((r) => r.route === 'in' && r.seq === '2').length)).toBe(2);
  await page.keyboard.type('y');
  await expect.poll(() => inputs(page)).toEqual(['ls', 'x', 'y']);
  await expect.poll(() => screen(page)).toContain('demo$ lsxy');
});

test('reopening attaches to the same shell and keeps its numbering', async ({ page }) => {
  await layout(page);
  await openOverlay(page);
  await page.keyboard.type('echo one');
  await page.keyboard.press('Enter');
  await expect.poll(() => screen(page)).toMatch(/one\s+demo\$/);
  await page.locator('.tm-ov .tm-close').click();
  await expect(page.locator('.tm-ov')).toHaveCount(0);
  await expect(page.locator('.tm-status')).toContainText('live');
  await openOverlay(page);
  await expect.poll(() => screen(page)).toMatch(/demo\$ echo one\s+one\s+demo\$/); // the shell's output so far is replayed
  await page.keyboard.type('echo two');
  await page.keyboard.press('Enter');
  await expect.poll(() => typed(page)).toMatch(/echo two\r$/); // not taken for a repeat of an earlier batch
  expect(await demo(page, () => window.__terminalDemo.opens)).toBe(1);
});

test('4 KB paste is split into batches in order', async ({ page }) => {
  await layout(page);
  await openOverlay(page);
  // a run long enough to hit the 4096-byte cap, then multi-byte runs placed so that a cut falls between two emoji
  const text = 'a'.repeat(5000) + 'é'.repeat(1501) + '😀'.repeat(1201) + '%&+# '.repeat(300) + 'end';
  await page.evaluate((t) => { window.__clip = t; }, text);
  await page.locator('.tm-keys button', { hasText: 'Paste' }).click();
  await expect.poll(() => typed(page), { timeout: 15_000 }).toBe(text);
  const sent = await inputs(page);
  expect(sent.length).toBeGreaterThan(3);
  expect(new TextEncoder().encode(sent[0]).length).toBe(4096); // the cap is used, and never passed (the demo answers 413)
  for (const s of sent) {
    expect(new TextEncoder().encode(s).length).toBeLessThanOrEqual(4096);
    expect(s.isWellFormed()).toBe(true); // no emoji cut in half
  }
  expect(sent.some((s, i) => i > 0 && s.startsWith('😀') && sent[i - 1].endsWith('😀'))).toBe(true); // a cut among the emoji
  // a program that asked for bracketed paste gets the markers, and a line break becomes Enter
  await demo(page, (k) => window.__terminalDemo.write(k, '\x1b[?2004h'), KEY);
  await page.waitForTimeout(300);
  await page.evaluate(() => { window.__clip = 'one\ntwo'; });
  await page.locator('.tm-keys button', { hasText: 'Paste' }).click();
  await expect.poll(() => typed(page)).toBe(text + '\x1b[200~one\rtwo\x1b[201~');
  // an empty clipboard says so and sends nothing
  await page.evaluate(() => { window.__clip = ''; });
  await page.locator('.tm-keys button', { hasText: 'Paste' }).click();
  await expect(page.locator('#toast')).toContainText('clipboard is empty');
});

test('bracketed paste strips ESC from the clipboard', async ({ page }) => {
  await layout(page);
  await openOverlay(page);
  await demo(page, (k) => window.__terminalDemo.write(k, '\x1b[?2004h'), KEY);
  await page.waitForTimeout(300);
  // a clipboard that tries to end the paste early and run a command
  await page.evaluate(() => { window.__clip = 'safe\x1b[201~rm -rf x\n\x1b[A'; });
  await page.locator('.tm-keys button', { hasText: 'Paste' }).click();
  await expect.poll(() => typed(page)).toBe('\x1b[200~saferm -rf x\r[A\x1b[201~');
});

test('awkward text round-trips', async ({ page }) => {
  await layout(page);
  await openOverlay(page);
  await page.keyboard.type('echo %+&#');
  await page.keyboard.press('Enter');
  await expect.poll(() => screen(page)).toMatch(/%\+&#\s+demo\$/);
  const text = 'echo 100% + a&b=c #1 é😀 ?x=1;y\necho next\n';
  await page.evaluate((t) => { window.__clip = t; }, text);
  await page.locator('.tm-keys button', { hasText: 'Paste' }).click();
  await expect.poll(() => typed(page)).toBe('echo %+&#\r' + text.replace(/\n/g, '\r'));
  await expect.poll(() => screen(page)).toMatch(/100% \+ a&b=c #1 é😀 \?x=1;y\s+demo\$ echo next\s+next/);
});

test('ended shell shows Restart and Restart opens a fresh one', async ({ page }) => {
  await layout(page);
  await openOverlay(page);
  await page.keyboard.type('exit');
  await page.keyboard.press('Enter');
  await expect.poll(() => screen(page)).toContain('[process ended]');
  await expect(page.locator('.tm-ov .tm-restart')).toBeVisible();
  await page.locator('.tm-ov .tm-restart').click();
  await expect.poll(() => demo(page, () => window.__terminalDemo.opens)).toBe(2);
  await expect.poll(async () => (await screen(page)).trim()).toBe('demo$');
  await expect(page.locator('.tm-ov .tm-restart')).toBeHidden();
  await page.locator('.tm-ov .xterm-helper-textarea').focus();
  await page.keyboard.type('echo again');
  await page.keyboard.press('Enter');
  await expect.poll(() => inputs(page)).toEqual(['echo again\r']); // the fresh shell's numbering starts over
  await expect.poll(() => screen(page)).toMatch(/again\s+demo\$/);
  // End session closes the shell for good
  await page.locator('.tm-ov .tm-end').click();
  await expect(page.locator('.tm-ov')).toHaveCount(0);
  await expect(page.locator('.tm-status')).toContainText('ended');
  expect(await demo(page, (k) => window.__terminalDemo.sessions.has(k), KEY)).toBe(false);
});

test('closing the overlay stops polling', async ({ page }) => {
  await layout(page);
  await openOverlay(page);
  await page.waitForTimeout(600);
  await page.locator('.tm-ov .tm-close').click();
  await expect(page.locator('.tm-ov')).toHaveCount(0);
  expect(await page.evaluate(() => document.body.classList.contains('term-open'))).toBe(false);
  const closedAt = await page.evaluate(() => Date.now());
  await page.waitForTimeout(3000);
  const after = await demo(page, (t) => window.__terminalDemo.requests.filter((r) => r.at > t).map((r) => r.route), closedAt);
  expect(after.filter((r) => r !== 'state')).toEqual([]);
  expect(await demo(page, (k) => window.__terminalDemo.sessions.has(k), KEY)).toBe(true); // the shell lives on
});

test('card copies the last lines on close', async ({ page }) => {
  await layout(page);
  await openOverlay(page);
  for (const w of ['one', 'two', 'three']) { await page.keyboard.type(`echo ${w}`); await page.keyboard.press('Enter'); }
  await expect.poll(() => screen(page)).toMatch(/three\s+demo\$/);
  await page.locator('.tm-ov .tm-close').click();
  await expect(page.locator('.tm-lines')).toHaveText(/demo\$ echo three\nthree\ndemo\$/);
});

test('older agent (404) shows the update hint', async ({ page }) => {
  await layout(page, { missing: true });
  await expect(page.locator('.tm-hint')).toContainText('Update PhoneBox on the computer');
  await expect(page.locator('.tm-open')).toBeHidden();
});

test('card works without the vendor files', async ({ page }) => {
  const errors = []; page.on('pageerror', (e) => errors.push(String(e)));
  await page.route('**/vendor/xterm/**', (r) => r.abort());
  await layout(page);
  await expect(page.locator('.tm-status')).toContainText('ready');
  await page.locator('.tm-open').click();
  await expect(page.locator('#toast')).toContainText('couldn’t load');
  await expect(page.locator('.tm-ov')).toHaveCount(0);
  expect(await page.evaluate(() => document.body.classList.contains('term-open'))).toBe(false);
  expect(errors).toEqual([]);
});

test('binary output survives', async ({ page }) => {
  const errors = []; page.on('pageerror', (e) => errors.push(String(e)));
  await layout(page);
  await openOverlay(page);
  const write = async (bytes) => { await demo(page, ([k, b]) => window.__terminalDemo.write(k, b), [KEY, bytes]); await page.waitForTimeout(300); };
  await write([0x41, 0x00, 0x42, 0x43, 0x20, 0x1b, 0x5b, 0x33]); // "A\0BC \e[3": a NUL, and an escape sequence cut short
  await write([...new TextEncoder().encode('1mRED\x1b[0m '), 0xc3]); // ...its end, then "é" cut in half
  await write([0xa9, 0x20, 0x64, 0x6f, 0x6e, 0x65, 0x0d, 0x0a]); // ...the rest of "é", " done"
  await expect.poll(() => screen(page)).toMatch(/ABC RED é done/);
  expect(await screen(page)).not.toContain('[31m');
  expect(await screen(page)).not.toContain('\ufffd'); // no character was split by a decode per frame
  await expect(page.locator('.tm-ov .xterm-rows .xterm-fg-1')).toHaveText('RED');
  // every byte is counted: the next poll asks from the shell's own byte count
  const total = await demo(page, (k) => window.__terminalDemo.sessions.get(k).total, KEY);
  await expect.poll(() => demo(page, () => window.__terminalDemo.requests.filter((r) => r.route === 'out').at(-1).since)).toBe(String(total));
  // an invalid byte (0xFF) passes through without breaking what follows
  await write([0x58, 0xff, 0x59, 0x0d, 0x0a]);
  await expect.poll(() => screen(page)).toMatch(/X.?Y/);
  expect(errors).toEqual([]);
});

// ---------------------------------------------------------------- exactly-once input across lost answers and reopenings

test('open says where the shell is: no probe, numbering continues', async ({ page }) => {
  await layout(page);
  await openOverlay(page);
  await page.keyboard.type('a');
  await expect.poll(() => typed(page)).toBe('a');
  await page.locator('.tm-ov .tm-close').click();
  await openOverlay(page);
  await page.keyboard.type('b');
  await expect.poll(() => inputs(page)).toEqual(['a', 'b']);
  const seqs = await demo(page, () => window.__terminalDemo.requests.filter((r) => r.route === 'in').map((r) => r.seq));
  expect(seqs).toEqual(['1', '2']); // nothing but the two batches, numbered on from what open said
});

test('a lost open answer is retried without closing the shell', async ({ page }) => {
  await layout(page);
  await demo(page, () => { window.__terminalDemo.loseOpen = 1; });
  await page.locator('.tm-open').click();
  await expect.poll(() => screen(page)).toContain('demo$');
  await page.locator('.tm-ov .xterm-helper-textarea').focus();
  await page.keyboard.type('x');
  await expect.poll(() => inputs(page)).toEqual(['x']);
  const routes = await demo(page, () => window.__terminalDemo.requests.map((r) => r.route));
  expect(routes.filter((r) => r === 'open').length).toBe(2);
  expect(routes).not.toContain('close');
  expect(await demo(page, () => window.__terminalDemo.opens)).toBe(1);
  await expect(page.locator('.tm-ov .tm-restart')).toBeHidden();
});

test('Try again after a failed open does not close the shell', async ({ page }) => {
  await layout(page);
  await demo(page, () => { window.__terminalDemo.failOpen = 1; });
  await page.locator('.tm-open').click();
  await expect(page.locator('.tm-ov .tm-net')).toContainText('could not be started');
  await expect(page.locator('.tm-ov .tm-restart')).toBeHidden();
  await page.locator('.tm-ov .tm-retry').click();
  await expect.poll(() => screen(page)).toContain('demo$');
  expect(await demo(page, () => window.__terminalDemo.requests.map((r) => r.route))).not.toContain('close');
  await expect(page.locator('.tm-ov .tm-retry')).toBeHidden();
});

test('a first-try {applied:false} counts as landed and is never retyped', async ({ page }) => {
  await layout(page);
  await openOverlay(page);
  await demo(page, () => { window.__terminalDemo.dupNext = 1; });
  await page.keyboard.type('ls');
  await expect.poll(() => typed(page)).toBe('ls');
  await page.keyboard.type('x');
  await expect.poll(() => inputs(page)).toEqual(['ls', 'x']);
  await page.waitForTimeout(300);
  expect(await inputs(page)).toEqual(['ls', 'x']);
});

test('a batch left unanswered by a closed overlay is resolved on reopen', async ({ page }) => {
  await layout(page);
  await openOverlay(page);
  // it never reached the shell: sent again first, with its own number, before anything new
  await demo(page, () => { window.__terminalDemo.inFault = 'drop'; });
  await page.keyboard.type('k'); // one key, one batch
  await expect.poll(() => demo(page, () => window.__terminalDemo.requests.filter((r) => r.route === 'in').length)).toBeGreaterThan(0);
  await page.locator('.tm-ov .tm-close').click();
  await demo(page, () => { window.__terminalDemo.inFault = null; });
  await openOverlay(page);
  await page.keyboard.type('c');
  await expect.poll(() => inputs(page)).toEqual(['k', 'c']);
  // it reached the shell but the answer never came: not typed again
  await demo(page, () => { window.__terminalDemo.inFault = 'lose'; });
  await page.keyboard.type('d');
  await expect.poll(() => typed(page)).toBe('kcd');
  await page.locator('.tm-ov .tm-close').click();
  await demo(page, () => { window.__terminalDemo.inFault = null; });
  await openOverlay(page);
  await page.keyboard.type('e');
  await expect.poll(() => inputs(page)).toEqual(['k', 'c', 'd', 'e']);
  // the shell went away meanwhile (a fresh one opens): the old batch is dropped, not typed into the new shell
  await demo(page, () => { window.__terminalDemo.inFault = 'drop'; });
  const before = await demo(page, () => window.__terminalDemo.requests.filter((r) => r.route === 'in').length);
  await page.keyboard.type('z');
  await expect.poll(() => demo(page, () => window.__terminalDemo.requests.filter((r) => r.route === 'in').length)).toBeGreaterThan(before);
  await page.locator('.tm-ov .tm-close').click();
  await demo(page, (k) => { window.__terminalDemo.inFault = null; window.__terminalDemo.sessions.delete(k); }, KEY);
  await openOverlay(page);
  await page.keyboard.type('f');
  await expect.poll(() => inputs(page)).toEqual(['f']);
});

test('a batch for a shell replaced while an open answer was lost never reaches the new shell', async ({ page }) => {
  await layout(page);
  await openOverlay(page);
  // the batch reaches shell 1, its answer never comes back, and the overlay closes
  await demo(page, () => { window.__terminalDemo.inFault = 'lose'; });
  await page.keyboard.type('k');
  await expect.poll(() => typed(page)).toBe('k');
  await page.locator('.tm-ov .tm-close').click();
  // shell 1 is reaped; the next open starts shell 2 but its answer is lost, so the retry hears "attached, nextSeq 1"
  await demo(page, (k) => { const d = window.__terminalDemo; d.inFault = null; d.sessions.delete(k); d.loseOpen = 1; }, KEY);
  await openOverlay(page);
  await page.keyboard.type('m');
  await expect.poll(() => inputs(page)).toEqual(['m']);
  await page.waitForTimeout(300);
  expect(await inputs(page)).toEqual(['m']); // "k" ran in shell 1 already: never again in shell 2
  expect(await demo(page, () => window.__terminalDemo.requests.filter((r) => r.route === 'open').length)).toBe(3);
});

test('input still in transit when Restart replaces the shell is refused by the new shell', async ({ page }) => {
  await layout(page);
  await openOverlay(page);
  await demo(page, () => { window.__terminalDemo.holdIn = true; });
  await page.keyboard.type('k');
  await expect.poll(() => demo(page, () => window.__terminalDemo.requests.filter((r) => r.route === 'in').length)).toBe(1);
  await demo(page, (k) => window.__terminalDemo.exit(k), KEY);
  await page.locator('.tm-ov .tm-restart').click();
  await expect.poll(() => demo(page, () => window.__terminalDemo.opens)).toBe(2);
  await expect.poll(async () => (await screen(page)).trim()).toBe('demo$');
  await demo(page, () => window.__terminalDemo.release()); // the old request arrives only now, numbered 1 like the new shell's first
  await page.waitForTimeout(300);
  expect(await inputs(page)).toEqual([]);
  await page.locator('.tm-ov .xterm-helper-textarea').focus();
  await page.keyboard.type('m');
  await expect.poll(() => inputs(page)).toEqual(['m']);
});

test('input refused as meant for another shell is dropped with a note, then typing goes on', async ({ page }) => {
  await layout(page);
  await openOverlay(page);
  await demo(page, (k) => { window.__terminalDemo.sessions.get(k).id = '0000000000000000'; }, KEY); // the shell changed under the phone
  await page.keyboard.type('k');
  await expect(page.locator('#toast')).toContainText('not sent');
  await expect.poll(() => demo(page, () => window.__terminalDemo.requests.filter((r) => r.route === 'open').length)).toBe(2);
  await page.keyboard.type('m');
  await expect.poll(() => inputs(page)).toEqual(['m']);
});

test('a 409 without an expected number opens again and carries on', async ({ page }) => {
  await layout(page);
  await openOverlay(page);
  await demo(page, () => { window.__terminalDemo.noExpected = 3; });
  await page.keyboard.type('q');
  await expect.poll(() => inputs(page), { timeout: 15_000 }).toEqual(['q']);
  expect(await demo(page, () => window.__terminalDemo.requests.filter((r) => r.route === 'open').length)).toBe(2);
});

test('a short screen gets the compact bar', async ({ page }) => {
  await page.setViewportSize({ width: 780, height: 300 });
  await layout(page);
  await openOverlay(page);
  await expect(page.locator('.tm-ov')).toHaveClass(/tm-compact/);
  await expect(page.locator('.tm-ov .tm-end')).toBeVisible(); // a shell stuck behind the keyboard can still be ended
  await page.setViewportSize({ width: 780, height: 360 });
  await expect(page.locator('.tm-ov')).not.toHaveClass(/tm-compact/);
  await expect.poll(() => demo(page, (k) => window.__terminalDemo.sessions.get(k).sizes.length, KEY)).toBeGreaterThan(1); // the new size is sent
});

// ---------------------------------------------------------------- final review fixes

test('reopening replays old output without answering its queries again; live queries are answered', async ({ page }) => {
  await layout(page);
  await openOverlay(page);
  // a program asks the terminal who it is (DA1), as vim, tmux or fzf do: answered once, while it waits
  await demo(page, (k) => window.__terminalDemo.write(k, '\x1b[c'), KEY);
  await expect.poll(() => typed(page)).toMatch(/^\x1b\[\?[\d;]*c$/);
  const before = await inputs(page), sent = await requests(page, 'in');
  await page.locator('.tm-ov .tm-close').click();
  await openOverlay(page); // a fresh xterm.js reads the shell's output from the start, that query included
  await page.waitForTimeout(800);
  expect(await requests(page, 'in')).toBe(sent); // its answer is not typed at the prompt again
  expect(await inputs(page)).toEqual(before);
  // the same query asked now, after the old output: answered
  await demo(page, (k) => window.__terminalDemo.write(k, '\x1b[c'), KEY);
  await expect.poll(() => inputs(page)).toHaveLength(before.length + 1);
  expect((await inputs(page)).at(-1)).toMatch(/^\x1b\[\?[\d;]*c$/);
  // and typing goes on as usual
  await page.keyboard.type('x');
  await expect.poll(async () => (await inputs(page)).at(-1)).toBe('x');
});

test('the night screen never covers an open terminal; typing is a touch', async ({ page }) => {
  await page.clock.install({ time: new Date(2026, 0, 1, 2, 0, 0) }); // 02:00 here, in the night
  await layout(page, { night: { enabled: true, start: '00:30', end: '07:30' } });
  await expect(page.locator('body')).toHaveClass(/\bnight\b/);
  await page.mouse.click(5, 5); // a tap wakes the panel for 30 s
  await expect(page.locator('body')).not.toHaveClass(/\bnight\b/);
  await openOverlay(page);
  await page.keyboard.type('a');
  await expect.poll(() => typed(page)).toBe('a');
  await page.clock.fastForward(90_000); // far past that wake and the last key
  await page.waitForTimeout(1500); // the panel's one-second tick runs
  await expect(page.locator('body')).not.toHaveClass(/\bnight\b/);
  await expect(page.locator('.tm-ov')).toBeVisible();
  const polls = await requests(page, 'out');
  await expect.poll(() => requests(page, 'out')).toBeGreaterThan(polls); // the output is still read
  await page.keyboard.type('b');
  await expect.poll(() => typed(page)).toBe('ab');
  // once the overlay is closed, the night screen comes back as usual (after the wake a touch gives)
  await page.locator('.tm-ov .tm-close').click();
  await expect(page.locator('.tm-ov')).toHaveCount(0);
  await page.clock.fastForward(60_000);
  await expect(page.locator('body')).toHaveClass(/\bnight\b/);
});

test('the page shrinks above the keyboard only while the overlay is open and showing', async ({ page }) => {
  await layout(page);
  const kb = () => page.evaluate(() => window.__kb || []);
  const hidden = (on) => page.evaluate((on) => {
    Object.defineProperty(document, 'hidden', { configurable: true, get: () => on });
    document.dispatchEvent(new Event('visibilitychange'));
  }, on);
  expect(await kb()).toEqual([]);
  await openOverlay(page);
  expect(await kb()).toEqual([true]);
  await hidden(true); // another app in front, or the screen off
  expect(await kb()).toEqual([true, false]);
  await hidden(false);
  expect(await kb()).toEqual([true, false, true]);
  await page.locator('.tm-ov .tm-close').click();
  expect(await kb()).toEqual([true, false, true, false]);
  await hidden(true); // closed: nothing more is asked
  await hidden(false);
  expect(await kb()).toEqual([true, false, true, false]);
});

test('a busy answer (an earlier batch still being written) goes again with the same number and lands once', async ({ page }) => {
  await layout(page);
  await openOverlay(page);
  await demo(page, () => { window.__terminalDemo.busyIn = 2; });
  await page.keyboard.type('x');
  await expect.poll(() => inputs(page), { timeout: 10_000 }).toEqual(['x']);
  expect(await demo(page, () => window.__terminalDemo.requests.filter((r) => r.route === 'in').map((r) => r.seq))).toEqual(['1', '1', '1']);
  await page.keyboard.type('y');
  await expect.poll(() => inputs(page)).toEqual(['x', 'y']);
});

test('keys typed while a flood of output is parsed reach the shell (Ctrl-C above all)', async ({ page }) => {
  await layout(page);
  await openOverlay(page);
  await page.keyboard.type('echo one');
  await page.keyboard.press('Enter');
  await expect.poll(() => screen(page)).toMatch(/one\s+demo\$/);
  // like `yes`: far more than the 256 KB the agent keeps arrives between two polls, so every answer is a reset frame
  const before = await demo(page, (k) => window.__terminalDemo.sessions.get(k).total, KEY);
  expect(before).toBeGreaterThan(0); // a live session: the cursor is not 0
  await page.evaluate((k) => {
    const chunk = 'y\r\n'.repeat(100_000);
    window.__flood = setInterval(() => window.__terminalDemo.write(k, chunk), 40);
  }, KEY);
  await expect.poll(() => demo(page, () => window.__terminalDemo.requests.filter((r) => r.route === 'out').length)).toBeGreaterThan(4);
  // an older phone: xterm.js parses each 256 KB frame slower than the next one arrives
  const cdp = await page.context().newCDPSession(page);
  await cdp.send('Emulation.setCPUThrottlingRate', { rate: 20 });
  await page.waitForTimeout(300);
  await page.keyboard.type('x');
  await page.keyboard.press('Control+c');
  await page.locator('.tm-keys .tm-ctrl').click();
  await page.keyboard.type('c'); // the key row's sticky Ctrl, then c
  await page.waitForTimeout(500);
  await page.evaluate(() => clearInterval(window.__flood));
  await cdp.send('Emulation.setCPUThrottlingRate', { rate: 1 });
  await expect.poll(() => typed(page), { timeout: 15_000 }).toBe('echo one\rx\x03\x03');
});

test('a link a program prints does not open anything or leave the panel', async ({ page }) => {
  await layout(page);
  await openOverlay(page);
  await page.evaluate(() => {
    window.__asked = [];
    window.open = (...a) => { window.__asked.push(['open', ...a]); return null; };
    window.confirm = (...a) => { window.__asked.push(['confirm', ...a]); return true; };
  });
  const url = page.url();
  await demo(page, (k) => window.__terminalDemo.write(k, '\r\n\x1b]8;;https://example.com/\x07CLICKME\x1b]8;;\x07\r\n'), KEY);
  await expect.poll(() => screen(page)).toContain('CLICKME');
  const row = page.locator('.tm-ov .xterm-rows > div', { hasText: 'CLICKME' });
  const box = await row.boundingBox();
  await page.mouse.move(box.x + 12, box.y + box.height / 2);
  await page.waitForTimeout(300);
  await page.mouse.click(box.x + 12, box.y + box.height / 2);
  await page.waitForTimeout(500);
  expect(await page.evaluate(() => window.__asked)).toEqual([]);
  expect(page.url()).toBe(url);
  await expect(page.locator('.tm-ov')).toBeVisible();
});

test('the card drops the last lines once the shell is gone', async ({ page }) => {
  await page.clock.install();
  await layout(page);
  await openOverlay(page);
  await page.keyboard.type('echo one');
  await page.keyboard.press('Enter');
  await expect.poll(() => screen(page)).toMatch(/one\s+demo\$/);
  await page.locator('.tm-ov .tm-close').click();
  await expect(page.locator('.tm-lines')).toContainText('one');
  // the computer closed the shell (idle): at the card's next look at the state the lines go
  await demo(page, (k) => window.__terminalDemo.sessions.delete(k), KEY);
  await page.clock.fastForward(9_000);
  await expect(page.locator('.tm-lines')).toBeHidden();
  // End session: none either
  await openOverlay(page);
  await page.keyboard.type('echo two');
  await page.keyboard.press('Enter');
  await expect.poll(() => screen(page)).toMatch(/two\s+demo\$/);
  await page.locator('.tm-ov .tm-end').click();
  await expect(page.locator('.tm-status')).toContainText('ended');
  await expect(page.locator('.tm-lines')).toBeHidden();
});

test('a terminal widget taken off the page forgets its lines', async ({ page }) => {
  await layout(page);
  await openOverlay(page);
  await page.keyboard.type('echo one');
  await page.keyboard.press('Enter');
  await expect.poll(() => screen(page)).toMatch(/one\s+demo\$/);
  await page.locator('.tm-ov .tm-close').click();
  await expect(page.locator('.tm-lines')).toContainText('one');
  const widgets = await page.evaluate(() => JSON.parse(localStorage.getItem('pb-demo-config')).pages[0].widgets);
  const set = (list) => page.evaluate(async (list) => {
    const c = JSON.parse(localStorage.getItem('pb-demo-config'));
    c.pages[0].widgets = list;
    localStorage.setItem('pb-demo-config', JSON.stringify(c));
    const main = await import('/panel/main.js');
    await main.loadConfig();
  }, list);
  await set([]);
  await expect(page.locator('.w-terminal')).toHaveCount(0);
  await page.waitForTimeout(100);
  await set(widgets); // the same widget again: the shell is still there, but nothing of the old card is remembered
  await expect(page.locator('.tm-status')).toContainText('live');
  await expect(page.locator('.tm-lines')).toBeHidden();
});

test('a batch left unanswered while the overlay was closed for more than 10 s is still sent on reopen, with its number', async ({ page }) => {
  await page.clock.install();
  await layout(page);
  await openOverlay(page);
  await demo(page, () => { window.__terminalDemo.inFault = 'drop'; });
  await page.keyboard.type('k');
  await expect.poll(() => requests(page, 'in')).toBeGreaterThan(0);
  await page.locator('.tm-ov .tm-close').click();
  await demo(page, () => { window.__terminalDemo.inFault = null; });
  await page.clock.fastForward(11_000);
  await openOverlay(page);
  await expect.poll(() => inputs(page)).toEqual(['k']); // what was typed, typed late
  await page.keyboard.type('c');
  await expect.poll(() => inputs(page)).toEqual(['k', 'c']);
  await page.waitForTimeout(300);
  expect(await inputs(page)).toEqual(['k', 'c']);
  const seqs = await demo(page, () => window.__terminalDemo.requests.filter((r) => r.route === 'in').map((r) => r.seq));
  expect(new Set(seqs)).toEqual(new Set(['1', '2'])); // "k" kept its number, "c" took the next
  expect(seqs.at(-1)).toBe('2');
  await expect(page.locator('#toast')).not.toContainText('not sent');
});

test('a batch stuck in the pty while the overlay was closed lands once, and what is typed after it is not lost', async ({ page }) => {
  await page.clock.install();
  await layout(page);
  await openOverlay(page);
  // the program in front is not reading: the batch is in flight, every `in` answers busy, open says nextSeq is its number
  await demo(page, () => { window.__terminalDemo.stuckIn = true; });
  await page.keyboard.type('k');
  await expect.poll(() => requests(page, 'in')).toBeGreaterThan(0);
  await page.locator('.tm-ov .tm-close').click();
  await page.clock.fastForward(11_000);
  await openOverlay(page);
  await page.keyboard.type('c');
  await expect.poll(() => requests(page, 'in')).toBeGreaterThan(1);
  await demo(page, (k) => window.__terminalDemo.unstick(k), KEY); // the program reads: the stuck write finishes
  await expect.poll(() => inputs(page), { timeout: 10_000 }).toEqual(['k', 'c']);
  await page.waitForTimeout(300);
  expect(await inputs(page)).toEqual(['k', 'c']); // nothing lost, nothing twice
  const seqs = await demo(page, () => window.__terminalDemo.requests.filter((r) => r.route === 'in').map((r) => r.seq));
  expect(seqs.filter((q) => q !== '1')).toEqual(['2']); // "k" only ever as 1, "c" once as 2
  await expect(page.locator('#toast')).not.toContainText('not sent');
});

test('a shell replaced while the overlay is open is shown from its start', async ({ page }) => {
  await layout(page);
  await openOverlay(page);
  await page.keyboard.type('echo one');
  await page.keyboard.press('Enter');
  await expect.poll(() => screen(page)).toMatch(/one\s+demo\$/);
  // another shell takes the widget's place (the old one ended, another panel opened a fresh one), with more output already
  await demo(page, (k) => window.__terminalDemo.replace(k, 'fresh$ ' + 'N'.repeat(40) + '\r\nfresh$ '), KEY);
  await page.keyboard.type('k'); // refused as meant for the old shell: a note, and the overlay attaches to the new one
  await expect(page.locator('#toast')).toContainText('not sent');
  await expect.poll(() => screen(page)).toMatch(/^fresh\$ N{40}\s+fresh\$$/);
  expect(await screen(page)).not.toContain('echo one');
  await page.keyboard.type('m');
  await expect.poll(() => inputs(page)).toEqual(['m']);
});
