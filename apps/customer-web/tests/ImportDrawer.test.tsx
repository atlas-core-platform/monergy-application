import { FoundationProvider } from '@monergy/ui-foundation';
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import { ImportDrawer } from '../src/access/ImportDrawer';
import { AccessApi } from '../src/access/accessApi';

const session = {
  tenantId: 'T001',
  actorId: 'A900',
  authenticationContextId: 'test-only',
  subjectVersion: 1,
  expiresAt: '2027-01-01T00:00:00Z',
};
const json = (value: unknown, status = 200) =>
  new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json' } });
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

it('creates one user through reviewed, versioned onboarding and recovers an uncertain commit without duplicating it', async () => {
  let valid = false;
  let processed = 0;
  const calls: { url: string; body: unknown; method?: string }[] = [];
  const changed = vi.fn();
  const expired = vi.fn();
  vi.stubGlobal(
    'fetch',
    vi.fn((url: string, options?: RequestInit) => {
      calls.push({
        url,
        body: options?.body
          ? JSON.parse(typeof options.body === 'string' ? options.body : '{}')
          : null,
        method: options?.method,
      });
      if (url.endsWith('/preview'))
        return Promise.resolve(
          json({
            valid,
            policyVersion: 7,
            contentHash: 'server-hash',
            rowCount: 1,
            errors: valid ? [] : [{ rowNumber: 2, field: 'email', code: 'DUPLICATE_EMAIL' }],
          }),
        );
      if (url.endsWith('/commit'))
        return Promise.resolve(json({ error: 'CONNECTION_UNAVAILABLE' }, 503));
      if (url.endsWith('/process')) {
        processed += 1;
        return Promise.resolve(
          json({
            id: 'durable-operation',
            status: processed === 1 ? 'Failed' : 'Activated',
            policyVersion: 22,
            rowCount: 1,
            identities: [
              {
                rowNumber: 2,
                normalizedEmail: 'new@example.test',
                actorId: processed === 1 ? null : 'A123',
              },
            ],
          }),
        );
      }
      if (url.includes('/imports/'))
        return Promise.resolve(
          json({
            id: 'durable-operation',
            status: 'PendingIdentity',
            policyVersion: 8,
            rowCount: 1,
            identities: [],
          }),
        );
      return Promise.resolve(
        json({
          policyVersion: 21,
          items: url.includes('/roles')
            ? [{ id: 'R1', label: 'Financial analyst', code: 'analyst' }]
            : url.includes('/groups')
              ? [{ id: 'G1', label: 'Advisory team', code: 'advisory' }]
              : [],
          nextCursor: null,
        }),
      );
    }),
  );
  render(
    <FoundationProvider>
      <ImportDrawer
        api={new AccessApi(session)}
        importId={null}
        single
        onClose={vi.fn()}
        onChanged={changed}
        onExpired={expired}
      />
    </FoundationProvider>,
  );
  await waitFor(() => {
    expect(screen.getByLabelText('Email address')).toBeEnabled();
  });
  fireEvent.change(screen.getByLabelText('Email address'), {
    target: { value: 'new@example.test' },
  });
  fireEvent.mouseDown(screen.getByRole('combobox', { name: 'Business role' }));
  fireEvent.click(
    await screen.findByText('Financial analyst', { selector: '.ant-select-item-option-content' }),
  );
  const teams = screen.getByRole('combobox', { name: 'Teams' });
  fireEvent.mouseDown(teams);
  fireEvent.click(
    await screen.findByText('Advisory team', { selector: '.ant-select-item-option-content' }),
  );
  fireEvent.keyDown(teams, { key: 'Escape', code: 'Escape' });
  fireEvent.click(screen.getByRole('button', { name: 'Review person' }));
  expect(await screen.findByText('duplicate email')).toBeVisible();
  expect(calls.some((call) => call.url.endsWith('/commit'))).toBe(false);
  valid = true;
  fireEvent.click(screen.getByRole('button', { name: 'Review person' }));
  await screen.findByRole('region', { name: 'Review onboarding' });
  const review = within(screen.getByRole('region', { name: 'Review onboarding' }));
  expect(review.getByText('new@example.test')).toBeVisible();
  expect(review.getByText('Financial analyst')).toBeVisible();
  expect(review.getByText('Advisory team')).toBeVisible();
  expect(review.getByText('No', { exact: true })).toBeVisible();
  fireEvent.click(screen.getByRole('button', { name: 'Create person' }));
  expect(await screen.findByText(/Your request has not been confirmed/)).toBeVisible();
  expect(screen.getByRole('button', { name: 'Review person' })).toBeDisabled();
  expect(screen.getByLabelText('Email address')).toBeDisabled();
  const commit = calls.find((call) => call.url.endsWith('/commit'));
  expect(commit?.body).toEqual({
    expectedPolicyVersion: 7,
    contentHash: 'server-hash',
    csv: 'email,role_code,group_codes\n"new@example.test","analyst","advisory"\n',
  });
  fireEvent.click(screen.getByRole('button', { name: 'Check status' }));
  expect(await screen.findByText('Onboarding is not complete.')).toBeVisible();
  expect(
    calls.some((call) => call.url === commit?.url.replace('/commit', '') && call.method === 'GET'),
  ).toBe(true);
  fireEvent.click(screen.getByRole('button', { name: 'Review and retry' }));
  fireEvent.click(screen.getByRole('button', { name: 'Retry onboarding' }));
  expect(await screen.findByText('Failed', { exact: true })).toBeVisible();
  expect(changed).not.toHaveBeenCalled();
  fireEvent.click(screen.getByRole('button', { name: 'Review and retry' }));
  fireEvent.click(screen.getByRole('button', { name: 'Retry onboarding' }));
  expect(await screen.findByText('Person created.')).toBeVisible();
  expect(screen.getByText('Next step: the person activates their own account')).toBeVisible();
  expect(screen.getByText(/No password or signed-in session has been created/)).toBeVisible();
  expect(screen.getByText(/Do not use your administrator session/)).toBeVisible();
  expect(changed).toHaveBeenCalledOnce();
  expect(calls.filter((call) => call.url.endsWith('/commit'))).toHaveLength(1);
  expect(calls.filter((call) => call.url.endsWith('/process')).map((call) => call.body)).toEqual([
    { expectedPolicyVersion: 21 },
    { expectedPolicyVersion: 21 },
  ]);
  expect(expired).not.toHaveBeenCalled();
}, 30000);

it('lets a tenant administrator cancel only a pending credential invitation and preserves organization access', async () => {
  let cancelled = false;
  const calls: { url: string; method?: string; body?: string | null }[] = [];
  vi.stubGlobal(
    'fetch',
    vi.fn((url: string, options?: RequestInit) => {
      calls.push({
        url,
        method: options?.method,
        body: typeof options?.body === 'string' ? options.body : null,
      });
      if (url === '/access-api/v1/administration/imports/op-cancel')
        return Promise.resolve(
          json({
            id: 'op-cancel',
            status: 'Activated',
            policyVersion: 31,
            rowCount: 1,
            identities: [
              { rowNumber: 2, normalizedEmail: 'pending@example.test', actorId: 'A123' },
            ],
          }),
        );
      if (url === '/identity-api/local/v1/administration/persona-invitations')
        return Promise.resolve(
          json({
            tenantId: 'T001',
            items: [
              {
                actorId: 'A123',
                email: 'pending@example.test',
                state: cancelled ? 'Cancelled' : 'Pending',
                issuedAt: '2026-10-26T18:00:00Z',
                consumedAt: null,
              },
              {
                actorId: 'A999',
                email: 'unrelated@example.test',
                state: 'Pending',
                issuedAt: '2026-10-26T18:00:00Z',
                consumedAt: null,
              },
            ],
          }),
        );
      if (
        url === '/identity-api/local/v1/administration/persona-invitations/A123/cancel' &&
        options?.method === 'POST'
      ) {
        cancelled = true;
        return Promise.resolve(json({ actorId: 'A123', state: 'Cancelled' }));
      }
      return Promise.resolve(json({ error: 'NOT_FOUND' }, 404));
    }),
  );

  render(
    <FoundationProvider>
      <ImportDrawer
        api={new AccessApi(session)}
        importId="op-cancel"
        onClose={vi.fn()}
        onChanged={vi.fn()}
        onExpired={vi.fn()}
      />
    </FoundationProvider>,
  );

  expect(await screen.findByText('Person access activated.')).toBeVisible();
  fireEvent.click(screen.getByRole('button', { name: 'Check invitation status' }));
  const invitationStatus = await screen.findByRole('region', {
    name: 'Credential invitation status',
  });
  expect(within(invitationStatus).getByText('Pending', { exact: true })).toBeVisible();
  expect(within(invitationStatus).queryByText('unrelated@example.test')).toBeNull();

  fireEvent.click(
    within(invitationStatus).getByRole('button', {
      name: 'Cancel invitation for pending@example.test',
    }),
  );
  const confirmation = await screen.findByRole('tooltip');
  expect(within(confirmation).getByText('Cancel credential invitation?')).toBeInTheDocument();
  expect(
    within(confirmation).getByText(
      'This revokes only the pending credential invitation. Organization membership and assigned access remain active.',
    ),
  ).toBeInTheDocument();
  const confirmCancellation = within(confirmation).getByRole('button', {
    name: 'Cancel invitation',
  });
  act(() => {
    confirmCancellation.click();
    confirmCancellation.click();
  });

  expect(
    await screen.findByText(
      'Credential invitation for pending@example.test was cancelled. Organization membership and assigned access remain active.',
    ),
  ).toBeVisible();
  await waitFor(() => {
    expect(within(invitationStatus).getByText('Cancelled', { exact: true })).toBeVisible();
  });
  expect(
    within(invitationStatus).queryByRole('button', {
      name: 'Cancel invitation for pending@example.test',
    }),
  ).toBeNull();

  const mutation = calls.find((call) => call.url.endsWith('/persona-invitations/A123/cancel'));
  expect(mutation).toEqual({
    url: '/identity-api/local/v1/administration/persona-invitations/A123/cancel',
    method: 'POST',
    body: '{}',
  });
  expect(
    calls.filter((call) => call.url.endsWith('/persona-invitations/A123/cancel')),
  ).toHaveLength(1);
  expect(
    calls.filter(
      (call) => call.url === '/identity-api/local/v1/administration/persona-invitations',
    ),
  ).toHaveLength(2);
}, 30000);

it('refreshes the authoritative invitation state when cancellation loses a terminal-state race', async () => {
  let terminal = false;
  vi.stubGlobal(
    'fetch',
    vi.fn((url: string, options?: RequestInit) => {
      if (url === '/access-api/v1/administration/imports/op-race')
        return Promise.resolve(
          json({
            id: 'op-race',
            status: 'Activated',
            policyVersion: 32,
            rowCount: 1,
            identities: [{ rowNumber: 2, normalizedEmail: 'race@example.test', actorId: 'A124' }],
          }),
        );
      if (url === '/identity-api/local/v1/administration/persona-invitations')
        return Promise.resolve(
          json({
            tenantId: 'T001',
            items: [
              {
                actorId: 'A124',
                email: 'race@example.test',
                state: terminal ? 'Expired' : 'Pending',
                issuedAt: '2026-10-26T18:00:00Z',
                consumedAt: null,
              },
            ],
          }),
        );
      if (
        url === '/identity-api/local/v1/administration/persona-invitations/A124/cancel' &&
        options?.method === 'POST'
      ) {
        terminal = true;
        return Promise.resolve(json({ actorId: 'A124', state: 'NotCancelled' }));
      }
      return Promise.resolve(json({ error: 'NOT_FOUND' }, 404));
    }),
  );

  render(
    <FoundationProvider>
      <ImportDrawer
        api={new AccessApi(session)}
        importId="op-race"
        onClose={vi.fn()}
        onChanged={vi.fn()}
        onExpired={vi.fn()}
      />
    </FoundationProvider>,
  );

  expect(await screen.findByText('Person access activated.')).toBeVisible();
  fireEvent.click(screen.getByRole('button', { name: 'Check invitation status' }));
  const invitationStatus = await screen.findByRole('region', {
    name: 'Credential invitation status',
  });
  fireEvent.click(
    within(invitationStatus).getByRole('button', {
      name: 'Cancel invitation for race@example.test',
    }),
  );
  const confirmation = await screen.findByRole('tooltip');
  fireEvent.click(within(confirmation).getByRole('button', { name: 'Cancel invitation' }));

  expect(
    await screen.findByText(
      'The credential invitation for race@example.test was no longer pending. Current status was refreshed.',
    ),
  ).toBeVisible();
  expect(within(invitationStatus).getByText('Expired', { exact: true })).toBeVisible();
  expect(
    within(invitationStatus).queryByRole('button', {
      name: 'Cancel invitation for race@example.test',
    }),
  ).toBeNull();
  expect(
    screen.queryByText(/Credential invitation for race@example.test was cancelled/),
  ).toBeNull();
}, 30000);

it('lets a tenant administrator request first delivery and resend without seeing activation secrets', async () => {
  const calls: { url: string; method?: string; body?: string | null }[] = [];
  vi.stubGlobal(
    'fetch',
    vi.fn((url: string, options?: RequestInit) => {
      calls.push({
        url,
        method: options?.method,
        body: typeof options?.body === 'string' ? options.body : null,
      });
      if (url === '/access-api/v1/administration/imports/op-send')
        return Promise.resolve(
          json({
            id: 'op-send',
            status: 'Activated',
            policyVersion: 33,
            rowCount: 2,
            identities: [
              { rowNumber: 2, normalizedEmail: 'expired@example.test', actorId: 'A125' },
              { rowNumber: 3, normalizedEmail: 'new@example.test', actorId: 'A126' },
            ],
          }),
        );
      if (url === '/identity-api/local/v1/administration/persona-invitations')
        return Promise.resolve(
          json({
            tenantId: 'T001',
            deliveryEnabled: true,
            items: [
              {
                actorId: 'A125',
                email: 'expired@example.test',
                state: 'Expired',
                issuedAt: '2026-10-27T18:00:00Z',
                consumedAt: null,
              },
            ],
          }),
        );
      const delivery = /\/persona-invitations\/(A125|A126)\/send$/.exec(url);
      if (delivery && options?.method === 'POST')
        return Promise.resolve(json({ actorId: delivery[1], state: 'Accepted' }, 202));
      return Promise.resolve(json({ error: 'NOT_FOUND' }, 404));
    }),
  );

  render(
    <FoundationProvider>
      <ImportDrawer
        api={new AccessApi(session)}
        importId="op-send"
        onClose={vi.fn()}
        onChanged={vi.fn()}
        onExpired={vi.fn()}
      />
    </FoundationProvider>,
  );

  expect(await screen.findByText('Person access activated.')).toBeVisible();
  fireEvent.click(screen.getByRole('button', { name: 'Check invitation status' }));
  const invitationStatus = await screen.findByRole('region', {
    name: 'Credential invitation status',
  });
  expect(within(invitationStatus).getByText('Not sent')).toBeVisible();

  fireEvent.click(
    within(invitationStatus).getByRole('button', { name: 'Send invitation to new@example.test' }),
  );
  let confirmation = await screen.findByRole('tooltip');
  expect(within(confirmation).getByText('Send credential invitation?')).toBeInTheDocument();
  const confirmFirst = within(confirmation).getByRole('button', { name: 'Send invitation' });
  act(() => {
    confirmFirst.click();
    confirmFirst.click();
  });
  expect(
    await screen.findByText(
      'Invitation delivery was requested for new@example.test. Refresh status after the recipient delivery completes.',
    ),
  ).toBeVisible();
  await waitFor(() => {
    expect(screen.queryByRole('tooltip')).toBeNull();
  });

  fireEvent.click(
    within(invitationStatus).getByRole('button', {
      name: 'Resend invitation to expired@example.test',
    }),
  );
  confirmation = await screen.findByRole('tooltip');
  expect(within(confirmation).getByText('Resend credential invitation?')).toBeInTheDocument();
  fireEvent.click(within(confirmation).getByRole('button', { name: 'Resend invitation' }));
  expect(
    await screen.findByText(
      'Invitation delivery was requested for expired@example.test. Refresh status after the recipient delivery completes.',
    ),
  ).toBeVisible();

  expect(calls.filter((call) => call.url.endsWith('/persona-invitations/A126/send'))).toEqual([
    {
      url: '/identity-api/local/v1/administration/persona-invitations/A126/send',
      method: 'POST',
      body: '{}',
    },
  ]);
  expect(calls.filter((call) => call.url.endsWith('/persona-invitations/A125/send'))).toHaveLength(
    1,
  );
  expect(within(invitationStatus).getAllByText('Delivery requested')).toHaveLength(2);
  expect(document.body.textContent).not.toMatch(/challenge|verification code:|password:/i);
}, 30000);

it('shows failed recipient delivery as attention without changing organization access', async () => {
  const calls: string[] = [];
  vi.stubGlobal(
    'fetch',
    vi.fn((url: string, options?: RequestInit) => {
      if (url === '/access-api/v1/administration/imports/op-delivery-failed')
        return Promise.resolve(
          json({
            id: 'op-delivery-failed',
            status: 'Activated',
            policyVersion: 35,
            rowCount: 1,
            identities: [
              { rowNumber: 2, normalizedEmail: 'failed@example.test', actorId: 'A128' },
            ],
          }),
        );
      if (url === '/identity-api/local/v1/administration/persona-invitations')
        return Promise.resolve(
          json({
            tenantId: 'T001',
            deliveryEnabled: true,
            items: [
              {
                actorId: 'A128',
                email: 'failed@example.test',
                state: 'Delivery failed',
                issuedAt: null,
                consumedAt: null,
              },
            ],
          }),
        );
      if (
        url === '/identity-api/local/v1/administration/persona-invitations/A128/send' &&
        options?.method === 'POST'
      ) {
        calls.push(url);
        return Promise.resolve(json({ actorId: 'A128', state: 'Accepted' }, 202));
      }
      return Promise.resolve(json({ error: 'NOT_FOUND' }, 404));
    }),
  );

  render(
    <FoundationProvider>
      <ImportDrawer
        api={new AccessApi(session)}
        importId="op-delivery-failed"
        onClose={vi.fn()}
        onChanged={vi.fn()}
        onExpired={vi.fn()}
      />
    </FoundationProvider>,
  );

  expect(await screen.findByText('Person access activated.')).toBeVisible();
  fireEvent.click(screen.getByRole('button', { name: 'Check invitation status' }));
  const invitationStatus = await screen.findByRole('region', {
    name: 'Credential invitation status',
  });
  expect(within(invitationStatus).getByText('Delivery failed')).toBeVisible();
  expect(
    within(invitationStatus).getByText('1 credential invitation delivery needs attention.'),
  ).toBeVisible();
  expect(
    within(invitationStatus).getByText(
      'Monergy could not confirm recipient delivery before the bounded attempt or expiry limit. Review the address with the person, then use Resend invitation. Organization access is unchanged.',
    ),
  ).toBeVisible();
  expect(within(invitationStatus).getByText('Not yet delivered')).toBeVisible();

  fireEvent.click(
    within(invitationStatus).getByRole('button', {
      name: 'Resend invitation to failed@example.test',
    }),
  );
  const confirmation = await screen.findByRole('tooltip');
  fireEvent.click(within(confirmation).getByRole('button', { name: 'Resend invitation' }));
  expect(
    await screen.findByText(
      'Invitation delivery was requested for failed@example.test. Refresh status after the recipient delivery completes.',
    ),
  ).toBeVisible();
  expect(calls).toEqual([
    '/identity-api/local/v1/administration/persona-invitations/A128/send',
  ]);
  expect(document.body.textContent).not.toMatch(/attempt count|smtp|challenge|password:/i);
}, 30000);

it('does not offer administrator send when authenticated email delivery is disabled', async () => {
  vi.stubGlobal(
    'fetch',
    vi.fn((url: string) => {
      if (url === '/access-api/v1/administration/imports/op-no-email')
        return Promise.resolve(
          json({
            id: 'op-no-email',
            status: 'Activated',
            policyVersion: 34,
            rowCount: 1,
            identities: [{ rowNumber: 2, normalizedEmail: 'tester@example.test', actorId: 'A127' }],
          }),
        );
      if (url === '/identity-api/local/v1/administration/persona-invitations')
        return Promise.resolve(json({ tenantId: 'T001', deliveryEnabled: false, items: [] }));
      return Promise.resolve(json({ error: 'NOT_FOUND' }, 404));
    }),
  );

  render(
    <FoundationProvider>
      <ImportDrawer
        api={new AccessApi(session)}
        importId="op-no-email"
        onClose={vi.fn()}
        onChanged={vi.fn()}
        onExpired={vi.fn()}
      />
    </FoundationProvider>,
  );

  expect(await screen.findByText('Person access activated.')).toBeVisible();
  fireEvent.click(screen.getByRole('button', { name: 'Check invitation status' }));
  const invitationStatus = await screen.findByRole('region', {
    name: 'Credential invitation status',
  });
  expect(
    within(invitationStatus).getByText(
      'Administrator email delivery is not enabled for this profile.',
    ),
  ).toBeVisible();
  expect(
    within(invitationStatus).queryByRole('button', { name: /invitation to tester@example\.test/ }),
  ).toBeNull();
}, 30000);
