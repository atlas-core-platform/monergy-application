import { FoundationProvider } from '@monergy/ui-foundation';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, it, vi } from 'vitest';
import { AccessApi } from '../src/access/accessApi';
import { Context } from '../src/access/workspaceSession';
import EvidenceExperience from '../src/product/EvidenceExperience';

const session = {
  tenantId: 'T001',
  actorId: 'A100',
  authenticationContextId: 'customer-session',
  subjectVersion: 2,
  expiresAt: '2027-01-01T00:00:00Z',
};

function mount(api: AccessApi, capabilities = ['evidence.document.upload']) {
  render(
    <FoundationProvider appearance="midnight">
      <Context
        value={{
          status: 'authorized',
          session,
          api,
          authorization: {
            area: 'product',
            context: {
              tenantId: 'T001',
              actorId: 'A100',
              customerId: 'reference-customer',
              customerName: 'Reference Customer',
              customerDetail: 'Primary UAT customer',
              capabilities,
              environment: 'Local UAT',
              dataDurability: 'Runtime only',
              productionAccepted: false,
            },
          },
          message: '',
          recovery: null,
          connect: () => Promise.resolve(),
          retry: () => Promise.resolve(),
          signOut: () => Promise.resolve(),
          cancel: () => undefined,
        }}
      >
        <EvidenceExperience />
      </Context>
    </FoundationProvider>,
  );
}

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

it('reviews the exact file before submitting and shows the owner-confirmed receipt', async () => {
  const api = new AccessApi(session);
  const submit = vi.spyOn(api, 'product').mockResolvedValue({
    operationId: 'a'.repeat(32),
    documentId: `document-${'a'.repeat(32)}`,
    documentVersionId: `document-version-${'a'.repeat(32)}`,
    evidenceId: `evidence-${'a'.repeat(32)}`,
    version: 1,
    originalFileName: 'statement.txt',
    contentType: 'text/plain',
    contentLength: 16,
    contentSha256: 'b'.repeat(64),
    acceptedAt: '2026-10-14T00:00:00Z',
    dataDurability: 'Runtime only',
  });
  vi.spyOn(crypto, 'randomUUID').mockReturnValue('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa');
  mount(api);
  const user = userEvent.setup();
  const input = document.querySelector('input[type="file"]');
  expect(input).toBeInstanceOf(HTMLInputElement);
  await user.upload(
    input as HTMLInputElement,
    new File(['account evidence'], 'statement.txt', { type: 'text/plain' }),
  );
  const drawer = await screen.findByRole('dialog', { name: 'Review evidence upload' });
  expect(drawer).toHaveTextContent('statement.txt');
  expect(drawer).toHaveTextContent('Reference Customer');
  expect(drawer).toHaveTextContent('Current LOCAL/UAT runtime only');
  expect(submit).not.toHaveBeenCalled();
  await user.click(screen.getByRole('button', { name: 'Upload evidence' }));
  await waitFor(() => {
    expect(submit).toHaveBeenCalledTimes(1);
  });
  expect(submit.mock.calls[0]?.[0]).toBe('v1/evidence');
  expect(submit.mock.calls[0]?.[2]).toMatchObject({
    operationId: 'a'.repeat(32),
    originalFileName: 'statement.txt',
    contentType: 'text/plain',
  });
  expect(await screen.findByText('CONFIRMED')).toBeVisible();
  expect(screen.getByText(`document-version-${'a'.repeat(32)}`)).toBeVisible();
});

it('does not expose upload when the current role lacks the capability', () => {
  mount(new AccessApi(session), []);
  expect(screen.getByText('Evidence upload is not included in your current role')).toBeVisible();
  expect(document.querySelector('input[type="file"]')).toBeDisabled();
});
