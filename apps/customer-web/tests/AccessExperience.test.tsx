import { FoundationProvider } from '@monergy/ui-foundation';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';

import AccessExperience from '../src/access/AccessExperience';
import { AccessApi, recordId } from '../src/access/accessApi';

const session = {
  tenantId: 'T001',
  actorId: 'A900',
  authenticationContextId: 'test-session',
  subjectVersion: 1,
  expiresAt: '2027-01-01T00:00:00Z',
};
const member = {
  actorId: 'A100',
  normalizedEmail: 'person@example.test',
  status: 'Active',
  tenantAdmin: false,
  businessRoleId: null,
};
const json = (value: unknown, status = 200) =>
  new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json' } });
const mount = () =>
  render(
    <FoundationProvider>
      <AccessExperience />
    </FoundationProvider>,
  );
async function connect() {
  const user = userEvent.setup();
  fireEvent.change(screen.getByLabelText('Local access key'), {
    target: { value: 'test-key-not-a-real-credential' },
  });
  await user.click(screen.getByRole('button', { name: 'Connect workspace' }));
  return user;
}
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('tenant administration trust boundaries', () => {
  it('keeps administration unavailable when the server rejects administrator authority', async () => {
    vi.stubGlobal(
      'fetch',
      vi
        .fn()
        .mockResolvedValueOnce(json(session))
        .mockResolvedValue(json({ error: 'ADMIN_REQUIRED' }, 403)),
    );
    mount();
    await connect();
    expect(await screen.findByText('Tenant administrator access is required.')).toBeVisible();
    expect(screen.queryByRole('button', { name: 'Import people' })).not.toBeInTheDocument();
    expect(screen.getByLabelText('Local access key')).toHaveValue('');
  });

  it(
    'preserves an unsaved edit after a policy conflict and clears tenant data when authority expires',
    { timeout: 30000 },
    async () => {
      let expired = false;
      const writes: unknown[] = [];
      vi.stubGlobal(
        'fetch',
        vi.fn((url: string, options?: RequestInit) => {
          if (url.includes('/identity-api/')) return Promise.resolve(json(session));
          if (expired) return Promise.resolve(json({ error: 'SESSION_NOT_CURRENT' }, 401));
          if (options?.method === 'PUT') {
            writes.push(JSON.parse(typeof options.body === 'string' ? options.body : '{}'));
            return Promise.resolve(json({ error: 'POLICY_VERSION_CONFLICT' }, 409));
          }
          return Promise.resolve(
            json({
              policyVersion: 7,
              items: url.includes('/roles') ? [] : [member],
              nextCursor: null,
            }),
          );
        }),
      );
      mount();
      const user = await connect();
      await user.click(await screen.findByRole('button', { name: 'Open person@example.test' }));
      await user.click(await screen.findByRole('switch', { name: 'Tenant administrator' }));
      await user.click(screen.getByRole('button', { name: 'Review change' }));
      await user.click(await screen.findByRole('button', { name: 'Save change' }));
      expect(
        await screen.findByText(/workspace changed or this operation conflicts/),
      ).toBeVisible();
      expect(screen.getByRole('switch', { name: 'Tenant administrator' })).toBeChecked();
      expect(writes).toEqual([
        { expectedPolicyVersion: 7, businessRoleId: null, active: true, tenantAdmin: true },
      ]);
      await user.click(screen.getByLabelText('Close'));
      expired = true;
      await user.click(screen.getByRole('button', { name: 'Refresh' }));
      expect(await screen.findByRole('button', { name: 'Connect workspace' })).toBeVisible();
      expect(screen.queryByText('person@example.test')).not.toBeInTheDocument();
    },
  );

  it(
    'never stages an invalid CSV and preserves the staged revision when processing',
    { timeout: 30000 },
    async () => {
      let valid = false;
      const calls: { url: string; body: unknown }[] = [];
      vi.stubGlobal(
        'fetch',
        vi.fn((url: string, options?: RequestInit) => {
          const body: unknown = options?.body
            ? JSON.parse(typeof options.body === 'string' ? options.body : '{}')
            : null;
          calls.push({ url, body });
          if (url.includes('/identity-api/')) return Promise.resolve(json(session));
          if (url.endsWith('/preview'))
            return Promise.resolve(
              json({
                valid,
                policyVersion: 7,
                contentHash: 'hash',
                rowCount: 1,
                errors: valid ? [] : [{ rowNumber: 2, field: 'email', code: 'INVALID_EMAIL' }],
              }),
            );
          if (url.endsWith('/commit'))
            return Promise.resolve(
              json({
                id: 'import-one',
                status: 'Pending',
                policyVersion: 8,
                rowCount: 1,
                identities: [],
              }),
            );
          if (url.endsWith('/process'))
            return Promise.resolve(
              json({
                id: 'import-one',
                status: 'Activated',
                policyVersion: 9,
                rowCount: 1,
                identities: [
                  { rowNumber: 2, normalizedEmail: member.normalizedEmail, actorId: 'ci-person' },
                ],
              }),
            );
          return Promise.resolve(json({ policyVersion: 7, items: [member], nextCursor: null }));
        }),
      );
      mount();
      const user = await connect();
      await user.click(await screen.findByRole('button', { name: 'Import people' }));
      await user.click(screen.getByText('Paste CSV instead'));
      fireEvent.change(await screen.findByRole('textbox', { name: 'CSV contents' }), {
        target: { value: 'email,role_code,group_codes\ninvalid,,' },
      });
      await user.click(screen.getByRole('button', { name: 'Validate entire file' }));
      expect(await screen.findByText('1 issues to resolve')).toBeVisible();
      expect(calls.some((call) => call.url.endsWith('/commit'))).toBe(false);
      valid = true;

      fireEvent.change(screen.getByRole('textbox', { name: 'CSV contents' }), {
        target: { value: 'email,role_code,group_codes\nperson@example.test,,' },
      });
      await user.click(screen.getByRole('button', { name: 'Validate entire file' }));
      await user.click(await screen.findByRole('button', { name: 'Confirm onboarding' }));
      await user.click(await screen.findByRole('button', { name: 'Onboard people' }));
      expect(await screen.findByText('Your people are ready.')).toBeVisible();
      const staged = calls.find((call) => call.url.endsWith('/commit'));
      const processed = calls.find((call) => call.url.endsWith('/process'));
      expect(processed?.url.replace('/process', '')).toBe(staged?.url.replace('/commit', ''));
      expect(processed?.body).toEqual({ expectedPolicyVersion: 8 });
      expect(staged?.body).toEqual({
        expectedPolicyVersion: 7,
        csv: 'email,role_code,group_codes\nperson@example.test,,',
        contentHash: 'hash',
      });
    },
  );

  it('rejects mixed-revision pagination and addresses resource grants by grant ID', async () => {
    const fetcher = vi
      .fn()
      .mockResolvedValueOnce(json({ policyVersion: 1, items: [], nextCursor: 'next' }))
      .mockResolvedValueOnce(json({ policyVersion: 2, items: [], nextCursor: null }));
    vi.stubGlobal('fetch', fetcher);
    await expect(new AccessApi(session).all('roles')).rejects.toMatchObject({ status: 409 });
    expect(recordId({ id: 'grant-1', actorId: 'A100' })).toBe('grant-1');
    await waitFor(() => {
      expect(fetcher).toHaveBeenCalledTimes(2);
    });
  });
});
