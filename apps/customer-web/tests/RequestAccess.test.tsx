import { FoundationProvider } from '@monergy/ui-foundation';
import { cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, it, vi } from 'vitest';

import type { AccessApi } from '../src/access/accessApi';
import { RequestAccessGate } from '../src/access/RequestAccess';
import { Context } from '../src/access/workspaceSession';

const session = {
  tenantId: 'T001', actorId: 'C100', authenticationContextId: 'customer-session',
  subjectVersion: 2, expiresAt: '2027-01-01T00:00:00Z',
};
const status = (overrides: Record<string, unknown> = {}) => ({
  requestId: null,
  target: 'customer.calculations',
  targetLabel: 'Financial calculations',
  grantExplanation: 'Allows this customer to use calculations based on their financial information.',
  state: 'NotRequested', hasAccess: false, canRequest: true,
  createdAt: null, decisionAt: null, fulfilledAt: null, decisionReason: null,
  ...overrides,
});
const page = (item = status()) => ({
  tenantId: 'T001', actorId: 'C100', policyVersion: 8, subjectVersion: 2, items: [item],
});

function mount(request: ReturnType<typeof vi.fn>, refreshContext = vi.fn(() => Promise.resolve()), capabilities: string[] = []) {
  render(<FoundationProvider appearance="midnight"><Context value={{
    status: 'authorized', session, api: { request } as unknown as AccessApi,
    authorization: { area: 'product', context: {
      tenantId: 'T001', actorId: 'C100', customerId: 'customer-one', customerName: 'Customer One',
      capabilities, environment: 'LOCAL', dataDurability: 'Temporary', productionAccepted: false,
    } }, message: '', recovery: null, connect: () => Promise.resolve(), refreshContext,
    retry: () => Promise.resolve(), signOut: () => Promise.resolve(), cancel: () => undefined,
  }}><RequestAccessGate target="customer.calculations"
      requiredCapabilities={['financial-rules.calculation.execute', 'financial-profile.profile.read']}>
      <div>Protected calculation result</div>
    </RequestAccessGate></Context></FoundationProvider>);
  return refreshContext;
}

afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });

it('keeps the protected feature unmounted and submits only target plus operation id', async () => {
  vi.stubGlobal('crypto', { randomUUID: () => 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa' });
  const request = vi.fn((path: string, method = 'GET', body?: unknown) => {
    if (method === 'GET') return Promise.resolve(page());
    expect(path).toBe('access-requests/customer.calculations/submit');
    expect(body).toEqual({ operationId: 'a'.repeat(32) });
    return Promise.resolve(status({ requestId: 'b'.repeat(32), state: 'Submitted', canRequest: false,
      createdAt: '2026-10-28T12:00:00Z' }));
  });
  mount(request);
  expect(await screen.findByRole('heading', { name: 'This capability needs organization access' })).toBeVisible();
  expect(screen.queryByText('Protected calculation result')).not.toBeInTheDocument();
  await userEvent.click(screen.getByRole('button', { name: 'Request access' }));
  expect(await screen.findByText(/submitted to an Organization Administrator/)).toBeVisible();
  expect(request.mock.calls.filter(([, method]) => method === 'POST')).toHaveLength(1);
});

it('refreshes the governed product context after fulfillment', async () => {
  let reads = 0;
  const request = vi.fn(() => Promise.resolve(++reads === 1
    ? page(status({ requestId: 'b'.repeat(32), state: 'Submitted', canRequest: false,
        createdAt: '2026-10-28T12:00:00Z' }))
    : page(status({ requestId: 'b'.repeat(32), state: 'Fulfilled', hasAccess: true, canRequest: false,
        createdAt: '2026-10-28T12:00:00Z', decisionAt: '2026-10-28T12:05:00Z',
        fulfilledAt: '2026-10-28T12:05:00Z' }))));
  const refreshContext = mount(request);
  await screen.findByText('Your request is under review');
  await userEvent.click(screen.getByRole('button', { name: 'Refresh status' }));
  expect(refreshContext).toHaveBeenCalledTimes(1);
});

it('renders the protected feature without calling request APIs when current capabilities suffice', () => {
  const request = vi.fn();
  mount(request, vi.fn(() => Promise.resolve()),
    ['financial-rules.calculation.execute', 'financial-profile.profile.read']);
  expect(screen.getByText('Protected calculation result')).toBeVisible();
  expect(request).not.toHaveBeenCalled();
});
