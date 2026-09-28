import { expect, test } from '@playwright/test';

test('toolchain shell starts and its dialog is keyboard operable', async ({ page }) => {
  await page.goto('/');
  await expect(page.getByRole('heading', { name: 'Monergy frontend foundation' })).toBeVisible();
  await expect(page.getByText('ACCEPTED · SIMULATOR')).toBeVisible();

  const trigger = page.getByRole('button', { name: 'View toolchain evidence' });
  await trigger.focus();
  await page.keyboard.press('Enter');
  await expect(page.getByRole('dialog', { name: 'D02 frontend evidence' })).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(page.getByRole('dialog', { name: 'D02 frontend evidence' })).toBeHidden();
  await expect(trigger).toBeFocused();
});

test('VS-02 reference flow preserves the authority boundary end to end', async ({ page }) => {
  await page.route('**/local-ci/vs02/reference-runs', async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        runId: 'run-e2e-001',
        processingState: 'COMPLETED',
        evidenceId: 'evidence-e2e-001',
        documentVersionId: 'document-version-e2e-001',
        observations: [
          {
            sourceFactId: 'source-fact-e2e-001',
            label: 'Salary',
            candidateValue: 125000,
            currency: 'INR',
            confidence: 0.98,
            sourceLocation: 'page:1',
          },
        ],
        financialFacts: [
          {
            financialFactId: 'financial-fact-e2e-001',
            label: 'Salary',
            value: 125000,
            currency: 'INR',
            revision: 1,
            financialProvenanceId: 'provenance-e2e-001',
          },
        ],
        auditRecordCount: 5,
        correlationId: 'correlation-e2e-001',
      }),
    });
  });

  await page.goto('/vs02');
  await expect(page.getByRole('heading', { name: 'Evidence to financial truth' })).toBeVisible();
  await expect(page.getByText('REFERENCE · LOCAL / CI ONLY')).toBeVisible();
  await page.getByRole('button', { name: 'Run reference flow' }).focus();
  await page.keyboard.press('Enter');

  await expect(page.getByRole('status')).toContainText('Reference flow completed.');
  await expect(page.getByText('NOT AUTHORITATIVE FINANCIAL TRUTH')).toBeVisible();
  await expect(page.getByText('AUTHORITATIVE', { exact: true })).toBeVisible();
  await expect(page.getByText('provenance-e2e-001')).toBeVisible();
});

test('D07 authorized search exposes derived provenance without frontend filtering', async ({
  page,
}) => {
  await page.route('**/contracts/cid-042/v1', async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        outcome: 'Success',
        error: null,
        data: {
          query: 'salary',
          matchMode: 'Semantic',
          items: [
            {
              searchRecordId: 'document-e2e-1',
              resultType: 'Document',
              title: 'Salary evidence',
              summary: 'Reference evidence metadata',
              authoritativeOwner: 'Evidence Service',
              representationState: 'DERIVED_REBUILDABLE',
              score: 1,
              source: {
                sourceObjectType: 'DocumentVersion',
                sourceObjectId: 'version-e2e-1',
                sourceReferenceId: 'evidence-e2e-1',
                provenanceReferenceId: 'provenance-e2e-1',
                calculationLineageReferenceId: null,
              },
            },
          ],
        },
      }),
    });
  });
  await page.goto('/search');
  await expect(page.getByRole('heading', { name: 'Authorized search & provenance' })).toBeVisible();
  await expect(page.getByText('REFERENCE · LOCAL / CI ONLY')).toBeVisible();
  await page.getByLabel('Search query').fill('salary');
  await page.getByRole('switch', { name: 'Semantic reference matching' }).click();
  await page.getByRole('button', { name: 'Search authorized references' }).click();
  await expect(page.getByRole('status')).toContainText('1 authorized result · Semantic');
  await expect(page.getByText('DERIVED · REBUILDABLE')).toBeVisible();
  await expect(page.getByText('Evidence Service')).toBeVisible();
  await expect(page.getByText('provenance-e2e-1')).toBeVisible();
});
