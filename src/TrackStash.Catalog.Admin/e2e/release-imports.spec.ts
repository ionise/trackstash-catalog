import { expect, test } from '@playwright/test'

const key = 'test-only-browser-key-not-a-production-secret'

test('review every source identity, confirm a full cached release import, then record a no-op', async ({ page }) => {
  test.setTimeout(120_000)
  await page.goto('/admin/#/purchase-releases')
  await page.getByLabel('API key').fill(key)
  await page.getByRole('button', { name: 'Connect', exact: true }).click()
  await page.getByRole('button', { name: 'Inspect Inspector complete release (900)' }).click()
  const review = page.getByRole('region', { name: 'Immutable catalog import review' })
  await review.getByRole('button', { name: 'Create immutable import preview' }).click()
  await expect(review.getByText('Apply blocked:')).toBeVisible()
  const apply = review.getByRole('button', { name: 'Apply reviewed catalog import', exact: true })
  await expect(apply).toBeDisabled()
  for (let index = 0; index < 30; index++) {
    await expect(review.getByRole('heading', { name: 'Frozen identity review' })).toBeVisible()
    await expect(review.getByRole('heading', { name: 'Frozen release placements and source credits' })).toBeVisible()
    await expect(review.getByRole('progressbar', { name: 'Loading frozen identity page' })).toHaveCount(0)
    let undecided = review.getByRole('region').filter({ hasText: 'Current decision: undecided' })
    while (await undecided.count() === 0) {
      await review.getByRole('button', { name: 'Next identities', exact: true }).click()
      await expect(review.getByRole('progressbar', { name: 'Loading frozen identity page' })).toHaveCount(0)
      await expect(review.getByRole('region').first()).toBeVisible()
      undecided = review.getByRole('region').filter({ hasText: 'Current decision: undecided' })
    }
    const row = undecided.first()
    await row.getByRole('textbox', { name: 'Identity review evidence' }).fill('Reviewed source identity and recording version')
    const response = page.waitForResponse(response => response.url().endsWith('/api/v1/release-import-previews') && response.request().method() === 'POST')
    await row.getByRole('button', { name: 'Save decision in a new immutable preview' }).click()
    expect((await response).status()).toBe(201)
    await expect(review.getByRole('progressbar', { name: 'Saving import review' })).toHaveCount(0)
  }
  await expect(review.getByText('Apply blocked:')).toHaveCount(0)
  await review.getByRole('textbox', { name: 'Import review reason' }).fill('Complete release reviewed, no media assertion')
  await review.getByRole('checkbox').check()
  await apply.click()
  await expect(review.getByText(/Saved import outcome for Release/)).toBeVisible()
  await expect(review).toContainText('4 Artists, 26 Recordings, 26 placements created')
  await review.getByRole('button', { name: 'Start a fresh preview (discard decisions)' }).click()
  await expect(review).toContainText('No-op.')
  await review.getByRole('textbox', { name: 'Import review reason' }).fill('Reimport unchanged source')
  await review.getByRole('checkbox').check()
  await review.getByRole('button', { name: 'Apply reviewed no-op' }).click()
  await expect(review.getByText(/Saved no-op outcome for Release/)).toBeVisible()
  await expect(review).toContainText('0 Artists, 0 Recordings, 0 placements created')
})
