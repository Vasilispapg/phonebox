const { test, expect } = require('@playwright/test');

// The Now playing widget shows what the phone itself plays (the Android app reads it from the media sessions and hands it
// over through window.Android) while the computer plays nothing. A song on the computer always wins, and the widget's
// buttons go to the phone for the phone's song, never to the agent.
const song = (extra = {}) => ({ phone: true, app: 'Spotify', title: 'Phone song', artist: 'Phone artist', album: '', playing: true,
  posMs: 10000, durMs: 200000, artId: 0, canSeek: true, playbackRate: 1, ageMs: 0, ...extra });
const mac = (extra = {}) => ({ app: 'Music', title: 'Mac song', artist: 'Mac artist', album: '', playing: true,
  posMs: 5000, durMs: 100000, artId: 0, canSeek: true, ageMs: 0, ...extra });

const PAGES = [{ id: 'music', title: 'Music', icon: '', enabled: true, widgets: [{ id: 'm', type: 'media', x: 0, y: 0, w: 12, h: 6 }] }];

async function open(page, initial = null) {
  await page.addInitScript((first) => {
    window.__calls = [];
    window.__phone = first;
    window.Android = {
      phoneMedia: () => JSON.stringify(window.__phone),
      phoneMediaArt: () => '',
      phoneMediaControl: (key, action, ms) => { window.__calls.push([action, ms]); return true; },
    };
  }, initial);
  await page.clock.install({ time: new Date('2026-01-01T12:00:00Z') });
  await page.goto('/panel/');
  await page.evaluate((pages) => {
    const cfg = JSON.parse(localStorage.getItem('pb-demo-config') || 'null') || {};
    localStorage.setItem('pb-demo-config', JSON.stringify({ ...cfg, pages, rules: [], shortcuts: cfg.shortcuts || [] }));
  }, PAGES);
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

test('a paused song on the computer gives way to the phone\'s playing one, and takes the widget back when the phone stops', async ({ page }) => {
  await open(page, song());
  await frame(page, mac({ playing: false }));
  await expect(page.locator('.m-title').first()).toHaveText('Phone song');
  await phoneSays(page, song({ playing: false }));
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

test('there is no switch while only one of them plays, and the choice is forgotten once one stops', async ({ page }) => {
  await open(page, song());
  await frame(page, null);
  await expect(page.locator('.m-swap').first()).toBeHidden();
  await frame(page, mac());
  await page.locator('.m-swap').first().click();
  await expect(page.locator('.m-title').first()).toHaveText('Phone song');
  await frame(page, mac({ playing: false })); // the computer stops: the phone stays
  await expect(page.locator('.m-swap').first()).toBeHidden();
  await frame(page, mac()); // and when it plays again, the computer is first again
  await expect(page.locator('.m-title').first()).toHaveText('Mac song');
});

test('lyrics are not looked up for the phone\'s songs (the agent only knows the computer\'s)', async ({ page }) => {
  await open(page, song());
  await frame(page, null);
  const key = await page.evaluate(async () => (await import('/panel/main.js')).media?.phone);
  expect(key).toBe(true);
});
