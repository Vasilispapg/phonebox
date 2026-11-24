// The Phone bridge card in Settings: macOS only, off by default, the code switch needs the notification switch, and the
// test button asks the agent for a test notification (only for settings that are saved, because the agent reads the file).
const { test, expect } = require('@playwright/test');

const config = (bridge) => ({
  display: { language: 'en', theme: 'default' },
  pages: [],
  bridge: { clipboard: false, notifications: false, copyCodes: false, filesFromPhone: false, filesToPhone: false, ...bridge },
});

async function open(page, cfg, os) {
  await page.goto('/panel/style.css');
  await page.evaluate((c) => localStorage.setItem('pb-demo-config', JSON.stringify(c)), cfg);
  await page.goto('/settings/#general');
  await page.evaluate(async (osName) => {
    const real = window.fetch;
    window.__bridgeCalls = [];
    window.fetch = async (input, init) => {
      const url = String(input);
      if (url.endsWith('/api/bridge/notification/test')) {
        window.__bridgeCalls.push(init?.method || 'GET');
        return new Response(JSON.stringify({ ok: true }), { headers: { 'Content-Type': 'application/json' } });
      }
      const r = await real(input, init);
      if (url.endsWith('/api/info')) {
        const d = await r.json();
        d.os = osName;
        return new Response(JSON.stringify(d), { headers: { 'Content-Type': 'application/json' } });
      }
      return r;
    };
    await loadInfo();
  }, os);
}

test('the card is shown on macOS and hidden elsewhere', async ({ page }) => {
  await open(page, config({}), 'windows');
  await expect(page.locator('#bridgeCard')).toBeHidden();
  await open(page, config({}), 'macos');
  await expect(page.locator('#bridgeCard')).toBeVisible();
  await expect(page.locator('[data-bind="bridge.clipboard"]')).not.toBeChecked();
  await expect(page.locator('[data-bind="bridge.notifications"]')).not.toBeChecked();
  await expect(page.locator('[data-bind="bridge.copyCodes"]')).not.toBeChecked();
});

test('the code switch and the test button wait for the notification switch', async ({ page }) => {
  await open(page, config({}), 'macos');
  await expect(page.locator('#bridgeCopyCodes')).toBeDisabled();
  await expect(page.locator('#bridgeTest')).toBeDisabled();
  await page.locator('label.switch', { has: page.locator('[data-bind="bridge.notifications"]') }).click();
  await expect(page.locator('[data-bind="bridge.notifications"]')).toBeChecked();
  await expect(page.locator('#bridgeCopyCodes')).toBeEnabled();
  await expect(page.locator('#bridgeTest')).toBeEnabled();
  await page.locator('label.switch', { has: page.locator('[data-bind="bridge.notifications"]') }).click();
  await expect(page.locator('#bridgeCopyCodes')).toBeDisabled();
});

test('saved notification settings come back on and enable the code switch', async ({ page }) => {
  await open(page, config({ notifications: true, copyCodes: true }), 'macos');
  await expect(page.locator('[data-bind="bridge.notifications"]')).toBeChecked();
  await expect(page.locator('[data-bind="bridge.copyCodes"]')).toBeChecked();
  await expect(page.locator('#bridgeCopyCodes')).toBeEnabled();
});

test('the test button posts to the agent once the settings are saved, and asks to save first otherwise', async ({ page }) => {
  await open(page, config({ notifications: true }), 'macos');
  await page.locator('#bridgeTest').click();
  await expect.poll(() => page.evaluate(() => window.__bridgeCalls)).toEqual(['POST']);
  // an unsaved change: the agent would test the old settings, so the page says to save first and sends nothing
  await page.locator('label.switch', { has: page.locator('[data-bind="bridge.clipboard"]') }).click();
  await page.locator('#bridgeTest').click();
  await expect(page.locator('#toast')).toContainText(/save/i);
  expect(await page.evaluate(() => window.__bridgeCalls)).toEqual(['POST']);
});

test('the two file switches are off by default and save one at a time', async ({ page }) => {
  await open(page, config({}), 'macos');
  await expect(page.locator('[data-bind="bridge.filesFromPhone"]')).not.toBeChecked();
  await expect(page.locator('[data-bind="bridge.filesToPhone"]')).not.toBeChecked();
  await page.locator('label.switch', { has: page.locator('[data-bind="bridge.filesFromPhone"]') }).click();
  await expect(page.locator('[data-bind="bridge.filesFromPhone"]')).toBeChecked();
  await expect(page.locator('[data-bind="bridge.filesToPhone"]')).not.toBeChecked();
  const saved = await page.evaluate(() => cfg.bridge);
  expect(saved.filesFromPhone).toBe(true);
  expect(saved.filesToPhone).toBe(false);
});

test('saved file switches come back on', async ({ page }) => {
  await open(page, config({ filesFromPhone: true, filesToPhone: true }), 'macos');
  await expect(page.locator('[data-bind="bridge.filesFromPhone"]')).toBeChecked();
  await expect(page.locator('[data-bind="bridge.filesToPhone"]')).toBeChecked();
});

// ---- the drop zone for sending files to the phone (Settings › Phone bridge) ----

// The demo answers every /api/ call itself, so the agent's two transfer calls are faked in the page, as the notification test is.
async function mockTransfer(page, { files = [], sendStatus = 202, sendError = null } = {}) {
  await page.evaluate(({ files, sendStatus, sendError }) => {
    const real = window.fetch;
    window.__sends = [];
    window.__transferCalls = 0;
    const json = (body, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
    window.fetch = async (input, init) => {
      const url = String(input);
      if (url.includes('/api/transfer/status')) { window.__transferCalls++; return json({ files }); }
      if (url.includes('/api/transfer/send')) {
        window.__transferCalls++;
        const body = init && init.body && init.body.text ? await init.body.text() : '';
        window.__sends.push({ method: init && init.method, url, body });
        return sendStatus >= 400 ? json({ error: sendError }, sendStatus) : json({ id: '0123456789abcdef0123456789abcdef' }, sendStatus);
      }
      return real(input, init);
    };
    return refreshBridgeDrop();
  }, { files, sendStatus, sendError });
}

async function dropFile(page, name, text) {
  await page.evaluate(({ name, text }) => {
    const data = new DataTransfer();
    data.items.add(new File([text], name, { type: 'text/plain' }));
    $('bridgeDrop').dispatchEvent(new DragEvent('drop', { dataTransfer: data, bubbles: true, cancelable: true }));
  }, { name, text });
}

test('the drop zone says why it is off while the switch is off', async ({ page }) => {
  await open(page, config({}), 'macos');
  await expect(page.locator('#bridgeDrop')).toHaveAttribute('aria-disabled', 'true');
  await expect(page.locator('#bridgeDropText')).toContainText(/turn on/i);
  await page.locator('label.switch', { has: page.locator('[data-bind="bridge.filesToPhone"]') }).click();
  await expect(page.locator('#bridgeDrop')).toHaveAttribute('aria-disabled', 'false');
  await expect(page.locator('#bridgeDropText')).toContainText(/drop files/i);
});

test('a dropped file is sent to the agent as raw bytes under its own name', async ({ page }) => {
  await open(page, config({ filesToPhone: true }), 'macos');
  await expect(page.locator('#bridgeDrop')).toHaveAttribute('aria-disabled', 'false');
  await mockTransfer(page);
  await dropFile(page, 'a b&c.txt', 'hello phone');
  await expect.poll(() => page.evaluate(() => window.__sends.length)).toBe(1);
  const sent = (await page.evaluate(() => window.__sends))[0];
  expect(sent.method).toBe('PUT');
  expect(new URL(sent.url, 'http://x').searchParams.get('name')).toBe('a b&c.txt');
  expect(sent.body).toBe('hello phone');
});

test('a dropped file while the switch is off sends nothing', async ({ page }) => {
  await open(page, config({}), 'macos');
  await mockTransfer(page);
  await dropFile(page, 'a.txt', 'x');
  await expect(page.locator('#toast')).toContainText(/turn on/i);
  expect(await page.evaluate(() => window.__transferCalls)).toBe(0);
});

test('the status list shows each file with its state', async ({ page }) => {
  await open(page, config({ filesToPhone: true }), 'macos');
  await expect(page.locator('#bridgeDrop')).toHaveAttribute('aria-disabled', 'false');
  await mockTransfer(page, { files: [
    { id: 'a'.repeat(32), name: 'photo.jpg', size: 3, state: 'delivered' },
    { id: 'b'.repeat(32), name: 'movie.mp4', size: 9, state: 'waiting' },
  ] });
  await expect(page.locator('#bridgeDropList')).toContainText('photo.jpg');
  await expect(page.locator('#bridgeDropList')).toContainText('Delivered');
  await expect(page.locator('#bridgeDropList')).toContainText('movie.mp4');
  await expect(page.locator('#bridgeDropList')).toContainText('Waiting for the phone');
});

test('a refused send shows the agent\'s reason', async ({ page }) => {
  await open(page, config({ filesToPhone: true }), 'macos');
  await expect(page.locator('#bridgeDrop')).toHaveAttribute('aria-disabled', 'false');
  await mockTransfer(page, { sendStatus: 413, sendError: 'too large' });
  await dropFile(page, 'huge.bin', 'x');
  await expect(page.locator('#bridgeDropList')).toContainText('huge.bin');
  await expect(page.locator('#bridgeDropList')).toContainText('too large');
});
