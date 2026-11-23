// The page overview (long-press the page dots) is where the phone's own Phone bridge screen is reached from the panel.
// The button exists only inside the Android app (window.Android with openBridge); a browser has no native screen to open.
const { test, expect } = require('@playwright/test');

test('the overview offers Phone bridge inside the app, and a tap opens the native screen', async ({ page }) => {
  await page.addInitScript(() => {
    window.__opened = [];
    window.Android = { openBridge: (key) => { window.__opened.push(key); } };
  });
  await page.goto('/panel/');
  await page.waitForFunction(() => typeof window.openOverview === 'function');
  await page.evaluate(() => window.openOverview());
  const button = page.locator('#ovBridge');
  await expect(button).toBeVisible();
  await expect(button).toContainText('Phone bridge');
  await button.click();
  expect(await page.evaluate(() => window.__opened.length)).toBe(1);
  await expect(page.locator('#overview')).toBeHidden();
});

test('without the app there is no Phone bridge button', async ({ page }) => {
  await page.goto('/panel/');
  await page.waitForFunction(() => typeof window.openOverview === 'function');
  await page.evaluate(() => window.openOverview());
  await expect(page.locator('#overview')).toBeVisible();
  await expect(page.locator('#ovBridge')).toBeHidden();
});
