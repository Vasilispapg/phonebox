const { test, expect } = require('@playwright/test');

// The Now playing widget shows what the phone itself plays (the Android app reads it from the media sessions and hands it
// over through window.Android) while the computer plays nothing. A song on the computer always wins, and the widget's
// buttons go to the phone for the phone's song, never to the agent.
const song = (extra = {}) => ({ phone: true, app: 'Spotify', title: 'Phone song', artist: 'Phone artist', album: '', playing: true,
  posMs: 10000, durMs: 200000, artId: 0, canSeek: true, playbackRate: 1, ageMs: 0, ...extra });
const mac = (extra = {}) => ({ app: 'Music', title: 'Mac song', artist: 'Mac artist', album: '', playing: true,
  posMs: 5000, durMs: 100000, artId: 0, canSeek: true, ageMs: 0, ...extra });

const PAGES = [{ id: 'music', title: 'Music', icon: '', enabled: true, widgets: [{ id: 'm', type: 'media', x: 0, y: 0, w: 12, h: 6 }] }];

async function open(page, initial = null, pages = PAGES) {
  await page.addInitScript((first) => {
    window.__calls = [];
    window.__phone = first;
    window.Android = {
      phoneMedia: () => JSON.stringify(window.__phone),
      phoneMediaArt: () => '',
      phoneMediaControl: (key, action, ms) => { window.__calls.push([action, ms]); return true; },
      phoneAudio: () => JSON.stringify({ volume: 40, muted: true, canControl: true, phone: true }),
      phoneVolume: (key, percent) => { window.__calls.push(['volume', percent]); return true; },
      phoneMute: () => { window.__calls.push(['mute']); return true; },
    };
  }, initial);
  await page.clock.install({ time: new Date('2026-01-01T12:00:00Z') });
  await page.goto('/panel/');
  await page.evaluate((pages) => {
    const cfg = JSON.parse(localStorage.getItem('pb-demo-config') || 'null') || {};
    localStorage.setItem('pb-demo-config', JSON.stringify({ ...cfg, pages, rules: [], shortcuts: cfg.shortcuts || [] }));
  }, pages);
  await page.reload();
  await expect(page.locator('.m-title').first()).toBeVisible();
  await page.evaluate(() => window.onAppPause(true)); // the demo's own once-a-second state would replace the media
  await page.clock.pauseAt(new Date('2026-01-01T12:01:00Z'));
}

/** Feeds the panel one agent state frame carrying [media] and returns the title the widget shows. */
async function frame(page, media) {
  await page.evaluate(async (m) => {
    const main = await import('/panel/main.js');
    main.applyState({ ts: Date.now() + 1e12 + (window.__n = (window.__n || 0) + 1), evSeq: 0, media: m });
  }, media);
}
const shown = (page) => page.locator('.m-title').first().textContent();
const phoneSays = (page, m) => page.evaluate((m) => window.onPhoneMedia(m === null ? 'null' : JSON.stringify(m)), m);

test('the phone\'s song is shown when the computer plays nothing', async ({ page }) => {
  await open(page, song());
  await frame(page, null);
  await expect(page.locator('.m-title').first()).toHaveText('Phone song');
  await expect(page.locator('.m-app-name').first()).toHaveText('Spotify');
  await expect(page.locator('.m-artist').first()).toHaveText('Phone artist');
});

test('a song playing on the computer wins over the phone\'s', async ({ page }) => {
  await open(page, song());
  await frame(page, mac());
  await expect(page.locator('.m-title').first()).toHaveText('Mac song');
});

test('a paused song on the computer gives way to the phone\'s playing one, and when the phone pauses too the phone stays shown', async ({ page }) => {
  await open(page, song());
  await frame(page, mac({ playing: false }));
  await expect(page.locator('.m-title').first()).toHaveText('Phone song');
  await phoneSays(page, song({ playing: false })); // both are paused now: Play must start the one that was on screen
  await expect(page.locator('.m-title').first()).toHaveText('Phone song');
  await page.locator('[data-media="playpause"]').first().click();
  expect(await page.evaluate(() => window.__calls.map((c) => c[0]))).toEqual(['playpause']);
});

test('when neither plays, the computer\'s song stays shown if it was the one on screen', async ({ page }) => {
  await open(page, song({ playing: false }));
  await frame(page, mac());
  await expect(page.locator('.m-title').first()).toHaveText('Mac song');
  await frame(page, mac({ playing: false })); // the computer pauses, the phone is paused too: still the computer's
  await expect(page.locator('.m-title').first()).toHaveText('Mac song');
});

test('a paused phone song is still shown when nothing else is, so its play button can resume it', async ({ page }) => {
  await open(page, song({ playing: false }));
  await frame(page, null);
  await expect(page.locator('.m-title').first()).toHaveText('Phone song');
});

test('a change from the phone reaches the widget at once, and "nothing playing" empties it', async ({ page }) => {
  await open(page, null);
  await frame(page, null);
  await expect(page.locator('.m-title').first()).not.toHaveText('Phone song');
  await phoneSays(page, song({ title: 'Later song' }));
  await expect(page.locator('.m-title').first()).toHaveText('Later song');
  await phoneSays(page, null);
  await expect(page.locator('.m-title').first()).not.toHaveText('Later song');
});

test('the position keeps counting from the phone\'s sample through the agent\'s once-a-second frames', async ({ page }) => {
  await open(page, null);
  await phoneSays(page, song({ posMs: 10000 })); // arrives now: its position is true now (the clock was moved on while the page opened)
  await frame(page, null);
  await page.clock.runFor(3000);
  await frame(page, null);
  const pos = await page.evaluate(async () => { const m = await import('/panel/main.js'); return Math.round(m.mediaPosMs(m.media)); });
  expect(pos).toBeGreaterThanOrEqual(12900);
  expect(pos).toBeLessThanOrEqual(13200);
});

test('the buttons drive the phone for the phone\'s song, and the agent hears nothing', async ({ page }) => {
  await open(page, song());
  const posts = [];
  await page.route('**/api/media/**', (route) => { posts.push(route.request().url()); route.continue(); });
  await frame(page, null);
  await expect(page.locator('.m-title').first()).toHaveText('Phone song');
  await page.locator('[data-media="next"]').first().click();
  await page.locator('[data-media="playpause"]').first().click();
  expect(await page.evaluate(() => window.__calls.map((c) => c[0]))).toEqual(['next', 'playpause']);
  expect(posts).toEqual([]);
});

test('the buttons still go to the agent for the computer\'s song', async ({ page }) => {
  await open(page, song());
  await frame(page, mac());
  await expect(page.locator('.m-title').first()).toHaveText('Mac song');
  await page.locator('[data-media="next"]').first().click();
  expect(await page.evaluate(() => window.__calls.length)).toBe(0);
});

test('when both play, the computer\'s song is shown with a switch, and the switch moves between the two', async ({ page }) => {
  await open(page, song());
  await frame(page, mac());
  const swap = page.locator('.m-swap').first();
  await expect(swap).toBeVisible();
  await expect(page.locator('.m-title').first()).toHaveText('Mac song');
  await swap.click();
  await expect(page.locator('.m-title').first()).toHaveText('Phone song');
  await expect(swap).toHaveClass(/phone/);
  await frame(page, mac()); // the agent's next frame does not take the widget back
  await expect(page.locator('.m-title').first()).toHaveText('Phone song');
  await page.locator('[data-media="playpause"]').first().click();
  expect(await page.evaluate(() => window.__calls.map((c) => c[0]))).toEqual(['playpause']);
  await swap.click();
  await expect(page.locator('.m-title').first()).toHaveText('Mac song');
});

test('the switch is there while the phone has a song, also when the computer shows nothing, and the choice lasts until the phone has none', async ({ page }) => {
  await open(page, song());
  await frame(page, null);
  const swap = page.locator('.m-swap').first();
  await expect(swap).toBeVisible(); // only the phone has a song: one can still look at the computer's side
  await swap.click();
  await expect(page.locator('.m-title').first()).not.toHaveText('Phone song');
  await expect(swap).toBeVisible(); // and come back from it
  await swap.click();
  await expect(page.locator('.m-title').first()).toHaveText('Phone song');
  await frame(page, mac());
  await expect(page.locator('.m-title').first()).toHaveText('Phone song'); // the choice stays while the computer plays
  await swap.click();
  await expect(page.locator('.m-title').first()).toHaveText('Mac song');
  await phoneSays(page, null); // the phone has nothing left: no switch, the computer's song
  await expect(swap).toBeHidden();
  await expect(page.locator('.m-title').first()).toHaveText('Mac song');
});

test('a paused computer song and a paused phone song can both be picked', async ({ page }) => {
  await open(page, song({ playing: false }));
  await frame(page, mac({ playing: false }));
  await expect(page.locator('.m-title').first()).toHaveText('Mac song');
  await page.locator('.m-swap').first().click();
  await expect(page.locator('.m-title').first()).toHaveText('Phone song');
});

test('the volume row shows and drives the phone\'s volume while the phone\'s song is shown, and the computer\'s otherwise', async ({ page }) => {
  await open(page, song());
  await frame(page, null);
  await expect(page.locator('.volr').first()).toHaveValue('40');
  await expect(page.locator('.w-media').first()).toHaveClass(/muted-on/);
  await page.locator('.mute').first().click();
  await page.locator('.volr').first().fill('70');
  await page.clock.runFor(300);
  expect(await page.evaluate(() => window.__calls.filter((c) => c[0] === 'mute' || c[0] === 'volume'))).toEqual([['mute'], ['volume', 70]]);
  await frame(page, mac()); // the computer plays: its own volume, not the phone's 40
  await expect(page.locator('.volr').first()).not.toHaveValue('40');
  await expect(page.locator('.w-media').first()).not.toHaveClass(/muted-on/);
});

test('a volume level moved on the phone\'s song is not sent to the computer when the source changes before it is sent', async ({ page }) => {
  await open(page, song());
  await frame(page, null);
  await page.evaluate(() => { // the demo answers the agent's API inside window.fetch, so what the panel POSTs is counted there
    const inner = window.fetch;
    window.__posts = [];
    window.fetch = (input, init = {}) => {
      if ((init.method || 'GET').toUpperCase() === 'POST') window.__posts.push(new URL(typeof input === 'string' ? input : input.url, location.href).pathname);
      return inner(input, init);
    };
  });
  await page.locator('.volr').first().fill('70');
  await frame(page, mac()); // the computer starts to play within the 120 ms wait: it is shown now
  await expect(page.locator('.m-title').first()).toHaveText('Mac song');
  await page.clock.runFor(300);
  expect(await page.evaluate(() => window.__calls.filter((c) => c[0] === 'volume'))).toEqual([]);
  expect(await page.evaluate(() => window.__posts.filter((p) => p.endsWith('/volume')))).toEqual([]); // nor to the computer
});

test('a keyboard seek begun on the phone\'s song is dropped when the source changes, even for the same title', async ({ page }) => {
  await open(page, song());
  await frame(page, null);
  await page.evaluate(() => { // the demo answers the agent's API inside window.fetch, so what the panel POSTs is counted there
    const inner = window.fetch;
    window.__posts = [];
    window.fetch = (input, init = {}) => {
      if ((init.method || 'GET').toUpperCase() === 'POST') window.__posts.push(new URL(typeof input === 'string' ? input : input.url, location.href).pathname);
      return inner(input, init);
    };
  });
  await page.locator('.bar').first().focus();
  await page.keyboard.press('ArrowRight'); // starts a seek that goes out after 350 ms
  await frame(page, mac({ app: 'Spotify', title: 'Phone song', durMs: 200000 })); // the computer plays the same song: shown now
  await page.clock.runFor(600);
  expect(await page.evaluate(() => window.__calls.filter((c) => c[0] === 'seek'))).toEqual([]);
  expect(await page.evaluate(() => window.__posts.filter((p) => p.includes('/media/seek')))).toEqual([]); // nor to the computer
});

const HTML_PAGES = [{ id: 'music', title: 'Music', icon: '', enabled: true, widgets: [
  { id: 'm', type: 'media', x: 0, y: 0, w: 6, h: 6 },
  { id: 'h', type: 'html', x: 6, y: 0, w: 6, h: 6, options: { html: '<script>addEventListener("message",function(e){var d=e.data;if(d&&d.phonebox==="data"&&d.kind==="state")parent.postMessage({probe:JSON.stringify(d.value&&d.value.media)},"*")})</script>' } },
] }];

test('a Custom HTML widget never gets the phone\'s song: its frame sees the agent\'s media only', async ({ page }) => {
  await page.addInitScript(() => { window.__probes = []; addEventListener('message', (e) => { if (e.data && 'probe' in e.data) window.__probes.push(e.data.probe); }); });
  await open(page, song(), HTML_PAGES);
  await frame(page, null);
  await expect(page.locator('.m-title').first()).toHaveText('Phone song'); // the Now playing widget shows it
  await phoneSays(page, song({ title: 'Another phone song' }));
  await frame(page, mac({ playing: false }));
  await expect.poll(() => page.evaluate(() => window.__probes.length)).toBeGreaterThan(1);
  await page.clock.runFor(500);
  const probes = await page.evaluate(() => window.__probes);
  expect(probes.some((p) => p.includes('song') && p.includes('Mac song')), 'the agent\'s own media does reach the frame').toBe(true);
  expect(probes.filter((p) => p.includes('Phone song') || p.includes('Another phone song') || p.includes('Phone artist'))).toEqual([]);
});

test('with the switch off the page is left alone: a null from the phone changes nothing and an old agent sample is not taken for a new one', async ({ page }) => {
  await open(page, null);
  await frame(page, mac());
  const before = await page.evaluate(async () => { const m = await import('/panel/main.js'); window.__m = m.media; return Math.round(m.mediaPosMs(m.media)); });
  await page.clock.runFor(300_000); // five minutes in the background
  await page.evaluate(() => window.onPhoneMedia('null'));
  await page.evaluate(() => window.onPhoneMedia('null'));
  const after = await page.evaluate(async () => { const m = await import('/panel/main.js'); return { same: m.media === window.__m, pos: Math.round(m.mediaPosMs(m.media)) }; });
  expect(after.same, 'the media object is the one it was').toBe(true);
  expect(after.pos).toBeGreaterThan(before + 90_000); // it kept counting; it did not go back to the old sample's position
});

test('going back to the computer\'s song counts on from its last sample, not from where that sample was', async ({ page }) => {
  await open(page, song());
  await frame(page, mac({ posMs: 10_000 }));
  await page.locator('.m-swap').first().click(); // the phone
  await expect(page.locator('.m-title').first()).toHaveText('Phone song');
  await page.clock.runFor(60_000);
  await page.locator('.m-swap').first().click(); // and back
  await expect(page.locator('.m-title').first()).toHaveText('Mac song');
  const pos = await page.evaluate(async () => { const m = await import('/panel/main.js'); return Math.round(m.mediaPosMs(m.media)); });
  expect(pos).toBeGreaterThan(69_000);
  expect(pos).toBeLessThan(72_000);
});

test('before the agent has answered (no state yet) the phone\'s song is still shown and its buttons work', async ({ page }) => {
  await open(page, null);
  await frame(page, null); // the computer says it plays nothing...
  await page.evaluate(async () => { (await import('/panel/main.js')).D.state = null; }); // ...and there is no state at all (the PC is off)
  await phoneSays(page, song());
  await expect(page.locator('.m-title').first()).toHaveText('Phone song');
  await page.locator('[data-media="next"]').first().click();
  expect(await page.evaluate(() => window.__calls.map((c) => c[0]))).toEqual(['next']);
});

test('lyrics are not looked up for the phone\'s songs (the agent only knows the computer\'s)', async ({ page }) => {
  await open(page, song());
  await frame(page, null);
  const key = await page.evaluate(async () => (await import('/panel/main.js')).media?.phone);
  expect(key).toBe(true);
});

test('with the switch turned off while the panel was paused, the next resume takes the song away', async ({ page }) => {
  await open(page, song());
  await frame(page, null);
  await expect(page.locator('.m-title').first()).toHaveText('Phone song');
  await expect(page.locator('.m-swap').first()).toBeVisible();
  // the Phone bridge screen was on top: the switch went off there (its "null" reached nobody), and the panel resumes
  await page.evaluate(() => { window.__phone = null; });
  await phoneSays(page, null); // what onResume pushes whatever the switch says
  await expect(page.locator('.m-title').first()).toHaveText('Nothing playing');
  await expect(page.locator('.m-swap').first()).toBeHidden();
  await page.locator('[data-media="next"]').first().click();
  expect(await page.evaluate(() => window.__calls.length)).toBe(0); // nothing goes to the phone any more
});

test('a null after a null (the switch was off all along) changes nothing on the page', async ({ page }) => {
  await open(page, null);
  await frame(page, mac({ posMs: 10_000 }));
  await page.clock.runFor(60_000);
  const before = await page.evaluate(async () => { const m = await import('/panel/main.js'); return Math.round(m.mediaPosMs(m.media)); });
  await phoneSays(page, null);
  await phoneSays(page, null);
  const after = await page.evaluate(async () => { const m = await import('/panel/main.js'); return Math.round(m.mediaPosMs(m.media)); });
  expect(after).toBe(before); // the old agent sample was not taken for a new one
  await expect(page.locator('.m-title').first()).toHaveText('Mac song');
});

/** The computer is off: the demo answers everything except the state, which fails as it does when the agent is unreachable. */
async function computerOff(page) {
  await page.addInitScript(() => {
    let inner = window.fetch.bind(window);
    const url = (i) => (typeof i === 'string' ? i : i?.url || '');
    const wrapped = (input, init) => (/\/api\/(state|media)/.test(url(input)) ? Promise.reject(new TypeError('Failed to fetch')) : inner(input, init));
    Object.defineProperty(window, 'fetch', { configurable: true, get: () => wrapped, set: (f) => { inner = f.bind(window); } });
  });
}

test('with the computer off the page reads the phone once at start, shows its song, and no offline message covers it', async ({ page }) => {
  await computerOff(page);
  await open(page, song());
  await page.evaluate(async () => { (await import('/panel/main.js')).setOnline(false); });
  await expect(page.locator('body')).toHaveClass(/offline/);
  expect(await page.evaluate(async () => (await import('/panel/main.js')).D.state)).toBeNull(); // no state frame ever came
  await expect(page.locator('.m-title').first()).toHaveText('Phone song'); // read at start, not through a frame or a push
  await expect(page.locator('.offline-msg').first()).toBeHidden();
  await expect(page.locator('.m-swap').first()).toBeVisible();
  await page.locator('[data-media="next"]').first().click(); // reaches the button, not an overlay
  expect(await page.evaluate(() => window.__calls.map((c) => c[0]))).toEqual(['next']);
});

test('offline with nothing on the phone the media card still says the computer is not connected', async ({ page }) => {
  await computerOff(page);
  await open(page, null);
  await page.evaluate(async () => { (await import('/panel/main.js')).setOnline(false); });
  await expect(page.locator('.offline-msg').first()).toBeVisible();
});

test('the phone\'s volume is read from the bridge at most every few seconds, and again after the panel changes it', async ({ page }) => {
  await open(page, song());
  await frame(page, null);
  await page.evaluate(() => { window.__audioReads = 0; const a = window.Android.phoneAudio; window.Android.phoneAudio = (...x) => { window.__audioReads++; return a(...x); }; });
  await page.evaluate(async () => { const m = await import('/panel/main.js'); for (let i = 0; i < 5; i++) m.phoneAudio(); });
  expect(await page.evaluate(() => window.__audioReads)).toBeLessThanOrEqual(1);
  await page.evaluate(async () => { const m = await import('/panel/main.js'); m.phoneVolume(10); m.phoneAudio(); });
  expect(await page.evaluate(() => window.__audioReads)).toBeLessThanOrEqual(2);
});
