// A key that cycles through other keys: each tap runs the next one, and the key shows the one that ran last.
const { test, expect } = require('@playwright/test');

const SHORTCUTS = [
  { id: 'low', label: 'Low', icon: '🔈', color: '#6b7280', cmd: 'builtin:keys' },
  { id: 'high', label: 'High', icon: '🔊', color: '#6b7280', cmd: 'builtin:keys' },
  { id: 'vol', label: 'Volume', icon: '🎚️', color: '#6b7280', cmd: 'builtin:cycle', cycle: ['low', 'high'] },
];

async function open(page) {
  await page.addInitScript((config) => localStorage.setItem('pb-demo-config', config), JSON.stringify({
    night: { enabled: false }, rules: [], shortcuts: SHORTCUTS,
    pages: [{ id: 'cycle-test', title: 'Keys', widgets: [{ id: 'b0', type: 'button', x: 0, y: 0, w: 4, h: 6, options: { id: 'vol' } }] }],
  }));
  await page.goto('/panel/');
  return page.locator('[data-id="cycle-test"] .w-button .key');
}

test('a cycle key shows its own name until it is tapped, then the key that ran last', async ({ page }) => {
  const key = await open(page);
  await expect(key.locator('.k-label')).toHaveText('Volume');
  await key.click();
  await expect(key.locator('.k-label')).toHaveText('Low');
  await key.click();
  await expect(key.locator('.k-label')).toHaveText('High');
  await key.click();
  await expect(key.locator('.k-label')).toHaveText('Low');
});

test('the key editor can turn a key into one that cycles, and lists the other keys', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 860 });
  await page.goto('/settings/#shortcuts');
  const keys = page.locator('#deck .key:not(.add)');
  await expect(keys.first()).toBeVisible();
  const what = page.locator('section.on select').first();
  await what.selectOption({ label: 'Cycle through other keys' });
  await page.getByRole('button', { name: 'Add a key' }).click();
  const rows = page.locator('section.on select').nth(1);
  await expect(rows).toBeVisible();
  const own = (await keys.first().locator('.kl').textContent()).trim();
  expect(await rows.locator('option').allTextContents()).not.toContain(own);
});
