import { FoundationProvider } from '@monergy/ui-foundation';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, it, vi } from 'vitest';

import type { AccessApi } from '../src/access/accessApi';
import { AccessRequests } from '../src/access/AccessRequests';

const id = 'a'.repeat(32);
const item = {
  requestId: id,
  requester: 'advisor@example.test',
  requesterRole: 'Financial Advisor',
  target: 'financial-advisor.assigned-customer-financial-review',
  targetLabel: 'View assigned customer financial information',
  state: 'UnderReview',
  createdAt: new Date(Date.now() - 120_000).toISOString(),
  expiresAt: new Date(Date.now() + 3600_000).toISOString(),
  decisionAt: null,
  decisionReason: null,
  grantExplanation: 'Allows this Financial Advisor to read financial information only for separately assigned customers with required record access and current customer consent.',
} as const;
const page = { tenantId: 'T001', actorId: 'A900', policyVersion: 7, items: [item], nextCursor: null };
const mount = (request: ReturnType<typeof vi.fn>) => render(
  <FoundationProvider appearance="midnight">
    <AccessRequests api={{ request } as unknown as AccessApi} tenantId="T001" actorId="A900" />
  </FoundationProvider>,
);
afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });

it('shows the bounded request in safe business language and approves it exactly once', async () => {
  vi.stubGlobal('crypto', { randomUUID: () => 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb' });
  const writes: { path: string; body: unknown }[] = [];
  const request = vi.fn((path: string, method = 'GET', body?: unknown) => {
    if (method === 'GET') return Promise.resolve(page);
    writes.push({ path, body });
    return Promise.resolve({ ...item, state: 'Fulfilled', decisionAt: new Date().toISOString() });
  });
  mount(request);
  const user = userEvent.setup();
  expect(await screen.findByText('advisor@example.test')).toBeVisible();
  expect(screen.getByText('Financial Advisor')).toBeVisible();
  expect(screen.getByText(/separately assigned/)).toBeVisible();
  expect(screen.queryByText(/permission ID|actor ID|credential/i)).not.toBeInTheDocument();
  await user.click(screen.getByRole('button', { name: 'Approve' }));
  await user.click(await screen.findByRole('button', { name: 'Approve access' }));
  expect(await screen.findByText(/approved and fulfilled/)).toBeVisible();
  expect(writes).toEqual([{
    path: `administration/access-requests/${id}/approve`,
    body: { operationId: 'b'.repeat(32) },
  }]);
  expect(screen.queryByRole('button', { name: 'Approve' })).not.toBeInTheDocument();
});

it('rejects without implying that access or customer consent changed', async () => {
  vi.stubGlobal('crypto', { randomUUID: () => 'cccccccc-cccc-cccc-cccc-cccccccccccc' });
  const request = vi.fn((_path: string, method = 'GET') => method === 'GET'
    ? Promise.resolve(page)
    : Promise.resolve({ ...item, state: 'Rejected', decisionAt: new Date().toISOString(),
        decisionReason: 'Not approved by your Organization Administrator' }));
  mount(request);
  const user = userEvent.setup();
  await user.click(await screen.findByRole('button', { name: 'Reject' }));
  await user.click(await screen.findByRole('button', { name: 'Reject request' }));
  expect(await screen.findByText(/No access was granted/)).toBeVisible();
  expect(screen.getByText('Rejected')).toBeVisible();
});

it('fails closed on a tenant-mismatched owner page', async () => {
  const request = vi.fn(() => Promise.resolve({ ...page, tenantId: 'T002' }));
  mount(request);
  await waitFor(() => { expect(request).toHaveBeenCalled(); });
  expect(await screen.findByText(/connection was interrupted/)).toBeVisible();
  expect(screen.queryByText('advisor@example.test')).not.toBeInTheDocument();
});
