import { expect, test } from '@playwright/test';

const advisorSession = {
  tenantId: 'T001', actorId: 'A300', authenticationContextId: 'advisor-browser-session',
  subjectVersion: 3, expiresAt: '2027-01-01T00:00:00Z',
};
const adminSession = {
  tenantId: 'T001', actorId: 'A900', authenticationContextId: 'admin-browser-session',
  subjectVersion: 4, expiresAt: '2027-01-01T00:00:00Z',
};
const requestId = 'a'.repeat(32);

test('Customer sees a restricted calculation without loading protected business data', async ({ page }) => {
  const writes: unknown[] = [];
  let protectedCalls = 0;
  await page.route('**/identity-api/**', route => route.fulfill({ json: {
    ...advisorSession, actorId: 'C100', authenticationContextId: 'customer-browser-session',
  } }));
  await page.route('**/product-api/**', route => { protectedCalls += 1; return route.fulfill({ status: 403, json: { error: 'ACCESS_DENIED' } }); });
  await page.route('**/product-api/v1/context', route => route.fulfill({ json: {
    tenantId: 'T001', actorId: 'C100', customerId: 'customer-one', customerName: 'Customer One',
    capabilities: ['financial-profile.profile.read'], environment: 'LOCAL',
    dataDurability: 'Temporary data', productionAccepted: false,
  } }));
  await page.route('**/access-api/**', route => {
    const path = new URL(route.request().url()).pathname;
    const item = {
      requestId: null, target: 'customer.calculations', targetLabel: 'Financial calculations',
      grantExplanation: 'Allows this customer to use calculations based on their financial information.',
      state: 'NotRequested', hasAccess: false, canRequest: true, createdAt: null,
      decisionAt: null, fulfilledAt: null, decisionReason: null,
    };
    if (path.endsWith('/access-requests/customer.calculations/submit')) {
      writes.push(route.request().postDataJSON());
      return route.fulfill({ json: { ...item, requestId, state: 'Submitted', canRequest: false,
        createdAt: '2026-10-28T00:00:00Z' } });
    }
    return route.fulfill({ json: { tenantId: 'T001', actorId: 'C100', policyVersion: 7,
      subjectVersion: 3, items: [item] } });
  });

  await page.goto('/monthly-cashflow');
  await page.getByLabel('Local access key').fill('browser-fixture-only');
  await page.getByRole('button', { name: 'Connect workspace' }).click();
  await expect(page.getByRole('heading', { name: 'This capability needs organization access' })).toBeVisible();
  expect(protectedCalls).toBe(0);
  await page.getByRole('button', { name: 'Request access' }).click();
  await expect(page.getByText(/submitted to an Organization Administrator/)).toBeVisible();
  expect(writes).toEqual([{ operationId: expect.stringMatching(/^[0-9a-f]{32}$/) }]);
  expect(JSON.stringify(writes)).not.toMatch(/actor|tenant|permission|role|grant/i);
});

test('Financial Advisor submits the bounded visible access request without authority fields', async ({ page }) => {
  const writes: unknown[] = [];
  await page.route('**/identity-api/**', route => route.fulfill({ json: advisorSession }));
  await page.route('**/product-api/v1/advisor/customers', route => route.fulfill({ json: {
    tenantId: 'T001', actorId: 'A300', policyVersion: 7, subjectVersion: 3, customers: [],
  } }));
  await page.route('**/access-api/**', async route => {
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith('/advisor/context')) return route.fulfill({ json: {
      tenantId: 'T001', actorId: 'A300', authority: 'FinancialAdvisor', policyVersion: 7,
      subjectVersion: 3, recordedRelationshipCount: 0,
    } });
    if (path.endsWith('/access-requests/financial-advisor.assigned-customer-financial-review/submit') && route.request().method() === 'POST') {
      writes.push(route.request().postDataJSON());
      return route.fulfill({ json: {
        requestId, target: 'financial-advisor.assigned-customer-financial-review',
        targetLabel: 'Assigned customer financial review',
        grantExplanation: 'Allows this Financial Advisor to read assigned customer information with current consent.',
        state: 'Submitted',
        hasAccess: false, canRequest: false, createdAt: '2026-10-28T00:00:00Z',
        decisionAt: null, fulfilledAt: null, decisionReason: null,
      } });
    }
    return route.fulfill({ json: {
      tenantId: 'T001', actorId: 'A300', policyVersion: 7, subjectVersion: 3,
      items: [{ requestId: null, target: 'financial-advisor.assigned-customer-financial-review',
        targetLabel: 'Assigned customer financial review',
        grantExplanation: 'Allows this Financial Advisor to read assigned customer information with current consent.',
        state: 'NotRequested', hasAccess: false, canRequest: true, createdAt: null,
        decisionAt: null, fulfilledAt: null, decisionReason: null }],
    } });
  });

  await page.goto('/advisor');
  await page.getByLabel('Local access key').fill('browser-fixture-only');
  await page.getByRole('button', { name: 'Connect workspace' }).click();
  await expect(page.getByRole('heading', { name: 'Financial Advisor workspace' })).toBeVisible();
  await expect(page.getByText('Restricted')).toBeVisible();
  await page.getByRole('button', { name: 'Request access' }).click();
  await expect(page.getByText('Submitted · Under review')).toBeVisible();
  expect(writes).toHaveLength(1);
  expect(writes[0]).toEqual({ operationId: expect.stringMatching(/^[0-9a-f]{32}$/) });
  expect(JSON.stringify(writes[0])).not.toMatch(/actor|tenant|permission|role|grant/i);
  await page.screenshot({ path: '.artifacts/ui/am-ira-advisor-submitted.png', fullPage: true });
});

test('Organization Administrator reviews and approves only the bounded request', async ({ page }) => {
  const decisions: { path: string; body: unknown }[] = [];
  await page.route('**/identity-api/**', route => route.fulfill({ json: adminSession }));
  await page.route('**/access-api/**', async route => {
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith('/administration/context')) return route.fulfill({ json: {
      tenantId: 'T001', actorId: 'A900', authority: 'TenantAdmin', policyVersion: 7,
    } });
    if (path.endsWith('/administration/access-requests') && route.request().method() === 'GET')
      return route.fulfill({ json: {
        tenantId: 'T001', actorId: 'A900', policyVersion: 7, nextCursor: null,
        items: [{
          requestId, requester: 'advisor@example.test', requesterRole: 'Financial Advisor',
          target: 'financial-advisor.assigned-customer-financial-review',
          targetLabel: 'View assigned customer financial information', state: 'UnderReview',
          createdAt: '2026-10-28T00:00:00Z', expiresAt: '2026-11-04T00:00:00Z',
          decisionAt: null, decisionReason: null,
          grantExplanation: 'Allows this Financial Advisor to read financial information only for customers separately assigned to them, with required record access and current customer consent.',
        }],
      } });
    if (path.endsWith(`/administration/access-requests/${requestId}/approve`)) {
      decisions.push({ path, body: route.request().postDataJSON() });
      return route.fulfill({ json: {
        requestId, requester: 'advisor@example.test', requesterRole: 'Financial Advisor',
        target: 'financial-advisor.assigned-customer-financial-review',
        targetLabel: 'View assigned customer financial information', state: 'Fulfilled',
        createdAt: '2026-10-28T00:00:00Z', expiresAt: '2026-11-04T00:00:00Z',
        decisionAt: '2026-10-28T01:00:00Z', decisionReason: null,
        grantExplanation: 'Allows this Financial Advisor to read financial information only for customers separately assigned to them, with required record access and current customer consent.',
      } });
    }
    return route.fulfill({ status: 404, json: { error: 'NOT_FOUND' } });
  });

  await page.goto('/access/requests');
  await page.getByLabel('Local access key').fill('browser-fixture-only');
  await page.getByRole('button', { name: 'Connect workspace' }).click();
  await expect(page.getByRole('heading', { name: 'Access requests' })).toBeVisible();
  await expect(page.getByText('advisor@example.test')).toBeVisible();
  await expect(page.getByText('Under review')).toBeVisible();
  await page.getByRole('button', { name: 'Approve' }).click();
  await page.getByRole('button', { name: 'Approve access' }).click();
  await expect(page.getByRole('cell', { name: 'Fulfilled' })).toBeVisible();
  expect(decisions).toHaveLength(1);
  expect(decisions[0]?.body).toEqual({ operationId: expect.stringMatching(/^[0-9a-f]{32}$/) });
  await page.screenshot({ path: '.artifacts/ui/am-ira-admin-fulfilled.png', fullPage: true });
});

test('Organization Administrator rejects and the Financial Advisor sees the safe rejected state', async ({ page }) => {
  let persona: 'admin' | 'advisor' = 'admin';
  let state: 'UnderReview' | 'Rejected' = 'UnderReview';
  const decisions: unknown[] = [];
  await page.route('**/identity-api/**', route => route.fulfill({
    json: persona === 'admin' ? adminSession : advisorSession,
  }));
  await page.route('**/product-api/v1/advisor/customers', route => route.fulfill({ json: {
    tenantId: 'T001', actorId: 'A300', policyVersion: 7, subjectVersion: 3, customers: [],
  } }));
  await page.route('**/access-api/**', route => {
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith('/administration/context')) return route.fulfill({ json: {
      tenantId: 'T001', actorId: 'A900', authority: 'TenantAdmin', policyVersion: 7,
    } });
    if (path.endsWith('/administration/access-requests') && route.request().method() === 'GET')
      return route.fulfill({ json: {
        tenantId: 'T001', actorId: 'A900', policyVersion: 7, nextCursor: null,
        items: [{
          requestId, requester: 'advisor@example.test', requesterRole: 'Financial Advisor',
          target: 'financial-advisor.assigned-customer-financial-review',
          targetLabel: 'Assigned customer financial review', state,
          createdAt: '2026-10-28T00:00:00Z', expiresAt: '2026-11-04T00:00:00Z',
          decisionAt: state === 'Rejected' ? '2026-10-28T01:00:00Z' : null,
          decisionReason: state === 'Rejected' ? 'Not approved by your Organization Administrator' : null,
          grantExplanation: 'Allows this Financial Advisor to read assigned customer information with current consent.',
        }],
      } });
    if (path.endsWith(`/administration/access-requests/${requestId}/reject`)) {
      decisions.push(route.request().postDataJSON());
      state = 'Rejected';
      return route.fulfill({ json: {
        requestId, requester: 'advisor@example.test', requesterRole: 'Financial Advisor',
        target: 'financial-advisor.assigned-customer-financial-review',
        targetLabel: 'Assigned customer financial review', state,
        createdAt: '2026-10-28T00:00:00Z', expiresAt: '2026-11-04T00:00:00Z',
        decisionAt: '2026-10-28T01:00:00Z',
        decisionReason: 'Not approved by your Organization Administrator',
        grantExplanation: 'Allows this Financial Advisor to read assigned customer information with current consent.',
      } });
    }
    if (path.endsWith('/advisor/context')) return route.fulfill({ json: {
      tenantId: 'T001', actorId: 'A300', authority: 'FinancialAdvisor', policyVersion: 7,
      subjectVersion: 3, recordedRelationshipCount: 0,
    } });
    if (path.endsWith('/access-requests')) return route.fulfill({ json: {
      tenantId: 'T001', actorId: 'A300', policyVersion: 7, subjectVersion: 3,
      items: [{
        requestId, target: 'financial-advisor.assigned-customer-financial-review',
        targetLabel: 'Assigned customer financial review',
        grantExplanation: 'Allows this Financial Advisor to read assigned customer information with current consent.',
        state: 'Rejected', hasAccess: false, canRequest: true,
        createdAt: '2026-10-28T00:00:00Z', decisionAt: '2026-10-28T01:00:00Z',
        fulfilledAt: null, decisionReason: 'Not approved by your Organization Administrator',
      }],
    } });
    return route.fulfill({ status: 404, json: { error: 'NOT_FOUND' } });
  });

  await page.goto('/access/requests');
  await page.getByLabel('Local access key').fill('browser-fixture-only');
  await page.getByRole('button', { name: 'Connect workspace' }).click();
  await page.getByRole('button', { name: 'Reject' }).click();
  await page.getByRole('button', { name: 'Reject request' }).click();
  await expect(page.getByText('Rejected', { exact: true })).toBeVisible();
  expect(decisions).toEqual([{ operationId: expect.stringMatching(/^[0-9a-f]{32}$/) }]);

  persona = 'advisor';
  await page.goto('/advisor');
  await page.getByLabel('Local access key').fill('browser-fixture-only');
  await page.getByRole('button', { name: 'Connect workspace' }).click();
  await expect(page.getByText('The request was not approved')).toBeVisible();
  await expect(page.getByText('Not approved by your Organization Administrator')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Request access' })).toBeEnabled();
});
