import { FoundationProvider } from '@monergy/ui-foundation';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
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
  const user = userEvent.setup();
  await waitFor(() => {
    expect(screen.getByLabelText('Email address')).toBeEnabled();
  });
  fireEvent.change(screen.getByLabelText('Email address'), {
    target: { value: 'new@example.test' },
  });
  await user.click(screen.getByRole('combobox', { name: 'Business role' }));
  await user.click(
    await screen.findByText('Financial analyst', { selector: '.ant-select-item-option-content' }),
  );
  await user.click(screen.getByRole('combobox', { name: 'Groups' }));
  await user.click(
    await screen.findByText('Advisory team', { selector: '.ant-select-item-option-content' }),
  );
  await user.keyboard('{Escape}');
  await user.click(screen.getByRole('button', { name: 'Review user' }));
  expect(await screen.findByText('duplicate email')).toBeVisible();
  expect(calls.some((call) => call.url.endsWith('/commit'))).toBe(false);
  valid = true;
  await user.click(screen.getByRole('button', { name: 'Review user' }));
  await screen.findByRole('region', { name: 'Review onboarding' });
  const review = within(screen.getByRole('region', { name: 'Review onboarding' }));
  expect(review.getByText('new@example.test')).toBeVisible();
  expect(review.getByText('Financial analyst')).toBeVisible();
  expect(review.getByText('Advisory team')).toBeVisible();
  expect(review.getByText('No', { exact: true })).toBeVisible();
  await user.click(screen.getByRole('button', { name: 'Create user' }));
  expect(await screen.findByText(/Your request has not been confirmed/)).toBeVisible();
  expect(screen.getByRole('button', { name: 'Review user' })).toBeDisabled();
  expect(screen.getByLabelText('Email address')).toBeDisabled();
  const commit = calls.find((call) => call.url.endsWith('/commit'));
  expect(commit?.body).toEqual({
    expectedPolicyVersion: 7,
    contentHash: 'server-hash',
    csv: 'email,role_code,group_codes\n"new@example.test","analyst","advisory"\n',
  });
  await user.click(screen.getByRole('button', { name: 'Check status' }));
  expect(await screen.findByText('Onboarding is not complete.')).toBeVisible();
  expect(
    calls.some((call) => call.url === commit?.url.replace('/commit', '') && call.method === 'GET'),
  ).toBe(true);
  await user.click(screen.getByRole('button', { name: 'Review and retry' }));
  await user.click(screen.getByRole('button', { name: 'Retry onboarding' }));
  expect(await screen.findByText('Failed', { exact: true })).toBeVisible();
  expect(changed).not.toHaveBeenCalled();
  await user.click(screen.getByRole('button', { name: 'Review and retry' }));
  await user.click(screen.getByRole('button', { name: 'Retry onboarding' }));
  expect(await screen.findByText('User created.')).toBeVisible();
  expect(changed).toHaveBeenCalledOnce();
  expect(calls.filter((call) => call.url.endsWith('/commit'))).toHaveLength(1);
  expect(calls.filter((call) => call.url.endsWith('/process')).map((call) => call.body)).toEqual([
    { expectedPolicyVersion: 21 },
    { expectedPolicyVersion: 21 },
  ]);
  expect(expired).not.toHaveBeenCalled();
}, 30000);
