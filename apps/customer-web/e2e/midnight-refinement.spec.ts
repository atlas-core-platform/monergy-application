import { expect, test } from '@playwright/test';
import type { Page, Route } from '@playwright/test';

const member = {
  actorId: 'A100',
  normalizedEmail: 'analyst@example.test',
  status: 'Active',
  tenantAdmin: false,
  businessRoleId: 'R1',
};
const role = { id: 'R1', code: 'analyst', label: 'Analyst', permissionIds: ['P1'] };
const permission = {
  id: 'P1',
  code: 'read',
  label: 'Read records',
  grants: [{ capabilityId: 'records.read', scope: 'Resource' }],
};
const group = { id: 'G1', code: 'team', label: 'Advisory team', memberCount: 1 };
const grant = {
  id: 'GRT1',
  actorId: 'A100',
  resourceId: 'record-1',
  capabilityId: 'records.read',
  resourceType: 'customer',
};
async function fixtures(page: Page, override?: (route: Route) => Promise<boolean>) {
  await page.route('**/*-api/**', async (route) => {
    if (await override?.(route)) return;
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith('/resource-directory/customer')) {
      await route.fulfill({
        json: {
          resourceType: 'customer',
          items: [
            { resourceId: 'record-1', displayName: 'First Customer' },
            { resourceId: 'record-2', displayName: 'Second Customer' },
          ],
        },
      });
      return;
    }
    await route.fulfill({
      json: path.endsWith('/tenant-sessions')
        ? {
            tenantId: 'T001',
            actorId: 'A900',
            authenticationContextId: 'refinement-fixture',
            subjectVersion: 1,
            expiresAt: new Date(Date.now() + 3600000).toISOString(),
          }
        : path.endsWith('/context')
          ? { tenantId: 'T001', actorId: 'A900', authority: 'TenantAdmin', policyVersion: 7 }
          : path.endsWith('/capabilities')
            ? {
                capabilities: [
                  {
                    capabilityId: 'records.read',
                    displayName: 'Read records',
                    service: 'records',
                    resourceType: 'customer',
                    lifecycle: 'Active',
                  },
                ],
              }
            : {
                policyVersion: 7,
                nextCursor: null,
                items: path.endsWith('/members')
                  ? [member]
                  : path.endsWith('/roles')
                    ? [role, { id: 'R2', code: 'reviewer', label: 'Reviewer', permissionIds: [] }]
                    : path.endsWith('/permissions')
                      ? [permission]
                      : path.endsWith('/groups')
                        ? [group]
                        : path.endsWith('/resource-grants')
                          ? [grant]
                          : [],
              },
    });
  });
  await page.goto('/access/users');
  await page.getByLabel('Local access key').fill('fixture-only');
  await page.getByRole('button', { name: 'Connect workspace', exact: true }).click();
  await expect(page.getByText(member.normalizedEmail, { exact: true })).toBeVisible();
}

test('user review preserves drafts, explains consequences, traps focus and restores the opener', async ({
  page,
}) => {
  const writes: unknown[] = [];
  await fixtures(page, async (route) => {
    if (route.request().method() !== 'PUT') return false;
    writes.push(route.request().postDataJSON());
    await route.fulfill({ json: { policyVersion: 8 } });
    return true;
  });
  const opener = page.getByRole('button', { name: `Open ${member.normalizedEmail}` });
  await opener.click();
  const drawer = page.getByRole('dialog');
  await expect(drawer.getByRole('heading', { name: 'Identity', exact: true })).toBeVisible();
  await drawer.getByRole('switch', { name: 'Tenant administrator' }).click();
  await drawer.getByRole('switch', { name: 'Active membership' }).click();
  await drawer.getByRole('button', { name: 'Review change' }).click();
  await expect(drawer.getByRole('heading', { name: 'Confirm access change' })).toBeFocused();
  await expect(drawer.locator('.is-changed')).toHaveCount(2);
  await expect(drawer.locator('.is-unchanged')).toHaveCount(1);
  await expect(
    drawer.getByText('Saving changes invalidates this user’s existing sessions.'),
  ).toBeVisible();
  await drawer.getByRole('button', { name: 'Back', exact: true }).click();
  await expect(drawer.getByRole('button', { name: 'Review change' })).toBeFocused();
  await expect(drawer.getByRole('switch', { name: 'Tenant administrator' })).toBeChecked();
  await expect(drawer.getByRole('switch', { name: 'Active membership' })).not.toBeChecked();
  await drawer.getByRole('combobox', { name: 'Business role' }).click();
  await page.getByTitle('Reviewer', { exact: true }).click();
  await drawer.getByRole('button', { name: 'Review change' }).click();
  await expect(
    drawer.getByText(/Changing the business role removes all individual resource grants/),
  ).toBeVisible();
  for (let i = 0; i < 8; i++) {
    await page.keyboard.press('Tab');
    expect(
      await page.evaluate(() => Boolean(document.activeElement?.closest('[role="dialog"]'))),
    ).toBe(true);
  }
  expect(writes).toEqual([]);
  await page.keyboard.press('Escape');
  await expect(drawer).toHaveCount(0);

  await expect(opener).toBeFocused();
  // Reopen and submit through the same governed payload.
  await opener.click();
  await drawer.getByRole('switch', { name: 'Tenant administrator' }).click();
  await drawer.getByRole('button', { name: 'Review change' }).click();
  await drawer.getByRole('button', { name: 'Save changes' }).click();
  await expect(drawer).toHaveCount(0);
  await expect(opener).toBeVisible();
  expect(writes).toEqual([
    { expectedPolicyVersion: 7, businessRoleId: 'R1', active: true, tenantAdmin: true },
  ]);
});

for (const status of [409, 500]) {
  test(`a ${status} result blocks resubmission until an authoritative, revision-consistent reload succeeds`, async ({
    page,
  }) => {
    let writes = 0;
    let recovering = false;
    let failRead = false;
    let pageTwo = false;
    await fixtures(page, async (route) => {
      const url = new URL(route.request().url());
      if (route.request().method() === 'PUT') {
        writes++;
        recovering = true;
        failRead = true;
        await route.fulfill({
          status,
          json: { error: status === 409 ? 'POLICY_VERSION_CONFLICT' : 'REQUEST_FAILED' },
        });
        return true;
      }
      if (recovering && url.pathname.endsWith('/members')) {
        if (failRead) {
          await route.fulfill({ status: 500, json: { error: 'REQUEST_FAILED' } });
          return true;
        }
        pageTwo = url.searchParams.has('after') || pageTwo;
        await route.fulfill({
          json: {
            policyVersion: 8,
            items: url.searchParams.has('after')
              ? [{ ...member, tenantAdmin: status === 500 }]
              : [],
            nextCursor: url.searchParams.has('after') ? null : 'next',
          },
        });
        return true;
      }
      if (recovering && url.pathname.endsWith('/roles')) {
        await route.fulfill({ json: { policyVersion: 8, items: [role], nextCursor: null } });
        return true;
      }
      return false;
    });
    await page.getByRole('button', { name: `Open ${member.normalizedEmail}` }).click();
    const drawer = page.getByRole('dialog');
    await drawer.getByRole('switch', { name: 'Tenant administrator' }).click();
    await drawer.getByRole('button', { name: 'Review change' }).click();
    await drawer.getByRole('button', { name: 'Save changes' }).click();
    await expect(drawer.getByRole('button', { name: 'Review change' })).toBeDisabled();
    await expect(drawer.getByRole('switch', { name: 'Tenant administrator' })).toBeChecked();
    await drawer.getByRole('button', { name: 'Reload current records' }).click();
    await expect(drawer.getByRole('button', { name: 'Review change' })).toBeDisabled();
    await expect(drawer.getByRole('button', { name: 'Reload current records' })).toBeEnabled();
    expect(writes).toBe(1);
    failRead = false;
    await drawer.getByRole('button', { name: 'Reload current records' }).click();
    await expect(page.getByText(/Current records reloaded from the service/)).toBeVisible();
    await expect(drawer).toHaveCount(0);
    expect(pageTwo).toBe(true);
    expect(writes).toBe(1);
  });
}

test('permission, group and resource editing keep existing contracts and review before commit', async ({
  page,
}) => {
  const writes: { path: string; method: string; body: unknown }[] = [];
  await fixtures(page, async (route) => {
    if (route.request().method() === 'GET' || route.request().url().includes('/identity-api/'))
      return false;
    writes.push({
      path: new URL(route.request().url()).pathname,
      method: route.request().method(),
      body: route.request().postDataJSON(),
    });
    await route.fulfill({ json: { policyVersion: 7 } });
    return true;
  });
  const nav = page.getByRole('navigation');
  const drawer = page.getByRole('dialog');
  await nav.getByRole('link', { name: 'Permissions', exact: true }).click();
  await page.getByRole('button', { name: 'Open Read records' }).click();
  await drawer.getByLabel('Name', { exact: true }).fill('Read assigned records');
  await drawer.getByLabel('Scope', { exact: true }).click();
  await page.getByTitle('Tenant-wide', { exact: true }).click();
  await drawer.getByRole('button', { name: 'Review change' }).click();
  await expect(drawer.locator('.is-changed')).toHaveCount(2);
  expect(writes).toHaveLength(0);
  await drawer.getByRole('button', { name: 'Save changes' }).click();
  await expect(drawer).toHaveCount(0);
  await nav.getByRole('link', { name: 'Groups', exact: true }).click();
  await page.getByRole('button', { name: 'Open Advisory team' }).click();
  await drawer.getByRole('tab', { name: 'People in this group' }).click();
  await drawer.locator('.ant-select-selection-item-remove').click();
  await drawer.getByRole('button', { name: 'Review change' }).click();
  await expect(drawer.locator('.access-review-row')).toHaveText(
    /ChangedCurrentanalyst@example.testProposedNone/,
  );
  await drawer.getByRole('button', { name: 'Save changes' }).click();
  await expect(drawer).toHaveCount(0);
  await nav.getByRole('link', { name: 'Resource Access', exact: true }).click();
  await page.getByRole('button', { name: 'Create resource access', exact: true }).click();
  await drawer.getByLabel('Person', { exact: true }).click();
  await page.getByTitle(member.normalizedEmail, { exact: true }).click();
  await drawer.getByLabel('Capability', { exact: true }).click();
  await page.getByTitle('Read records', { exact: true }).click();
  await drawer.getByLabel('Resource', { exact: true }).click();
  await page.getByTitle('Second Customer', { exact: true }).click();
  await expect(drawer.getByLabel('Resource identifier')).toHaveCount(0);
  await drawer.getByRole('button', { name: 'Review change' }).click();
  await expect(
    drawer
      .getByRole('region', { name: 'Review changes' })
      .getByText('Second Customer', { exact: true }),
  ).toBeVisible();
  expect(writes).toHaveLength(2);
  await drawer.getByRole('button', { name: 'Create resource access', exact: true }).click();
  await expect(drawer).toHaveCount(0);
  await page.getByRole('button', { name: 'Open First Customer' }).click();
  await expect(drawer.getByText('First Customer', { exact: true })).toBeVisible();
  await expect(drawer.getByText('record-1', { exact: true })).toHaveCount(0);
  await drawer.getByRole('button', { name: 'Remove', exact: true }).click();
  expect(writes).toHaveLength(3);
  await drawer.getByRole('button', { name: 'Remove', exact: true }).click();
  await expect(drawer).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Open First Customer' })).toBeVisible();
  expect(writes).toEqual([
    {
      path: '/access-api/v1/administration/permissions/P1',
      method: 'PUT',
      body: {
        expectedPolicyVersion: 7,
        code: 'read',
        label: 'Read assigned records',
        grants: [{ capabilityId: 'records.read', scope: 'Tenant' }],
      },
    },
    {
      path: '/access-api/v1/administration/groups/G1/members',
      method: 'PUT',
      body: { expectedPolicyVersion: 7, actorIds: [] },
    },
    {
      path: '/access-api/v1/administration/resource-grants',
      method: 'POST',
      body: {
        expectedPolicyVersion: 7,
        actorId: 'A100',
        capabilityId: 'records.read',
        resourceType: 'customer',
        resourceId: 'record-2',
      },
    },
    {
      path: '/access-api/v1/administration/resource-grants/GRT1',
      method: 'DELETE',
      body: { expectedPolicyVersion: 7 },
    },
  ]);
});

test('loaded-record filtering and enlarged text retain actions and honest counts', async ({
  page,
}) => {
  await fixtures(page);
  await page.getByRole('textbox', { name: 'Filter loaded records' }).fill('no-match');
  await expect(page.getByRole('status')).toHaveText(/0 of 1 loaded records/);
  await page.getByRole('button', { name: 'Clear filters' }).click();
  await expect(page.getByText(member.normalizedEmail, { exact: true })).toBeVisible();
  await page.evaluate(() => {
    document.documentElement.style.fontSize = '200%';
  });
  await expect(page.getByRole('button', { name: 'Create user', exact: true })).toHaveCSS(
    'font-size',
    '26px',
  );
  await page.setViewportSize({ width: 390, height: 844 });
  await expect
    .poll(() => page.evaluate(() => document.documentElement.scrollWidth <= innerWidth))
    .toBe(true);
  await page.getByRole('button', { name: 'Create user', exact: true }).click();
  await expect(page.getByLabel('Email address')).toBeVisible();
  await expect
    .poll(() => page.evaluate(() => document.documentElement.scrollWidth <= innerWidth))
    .toBe(true);
});

test('review preserves a pending identity status instead of labelling the current user disabled', async ({
  page,
}) => {
  await fixtures(page, async (route) => {
    if (!new URL(route.request().url()).pathname.endsWith('/members')) return false;
    await route.fulfill({
      json: {
        policyVersion: 7,
        nextCursor: null,
        items: [{ ...member, status: 'PendingIdentity' }],
      },
    });
    return true;
  });
  await page.getByRole('button', { name: `Open ${member.normalizedEmail}` }).click();
  const drawer = page.getByRole('dialog');
  await expect(drawer.getByText(/Identity activation is pending/)).toBeVisible();
  await drawer.getByRole('button', { name: 'Review change' }).click();
  await expect(
    drawer.locator('.access-review-row').filter({ hasText: 'Account status' }),
  ).toHaveText(/Account statusChangedCurrentPending identityProposedDisabled/);
});
