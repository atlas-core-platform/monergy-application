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

function mount(
  api: AccessApi,
  capabilities = ['evidence.document.upload', 'evidence.document.read'],
) {
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
  const receipt = {
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
  } as const;
  const item = {
    documentId: receipt.documentId,
    documentVersionId: receipt.documentVersionId,
    evidenceId: receipt.evidenceId,
    version: receipt.version,
    sourceName: 'Customer upload',
    originalFileName: receipt.originalFileName,
    contentType: receipt.contentType,
    contentLength: receipt.contentLength,
    contentSha256: receipt.contentSha256,
    receivedAt: receipt.acceptedAt,
    createdAt: receipt.acceptedAt,
  };
  let listed = false;
  const submit = vi.spyOn(api, 'product').mockImplementation((_, method) => {
    if (method === 'GET')
      return Promise.resolve({ items: listed ? [item] : [], dataDurability: 'Runtime only' });
    listed = true;
    return Promise.resolve(receipt);
  });
  vi.spyOn(crypto, 'randomUUID').mockReturnValue('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa');
  mount(api);
  const user = userEvent.setup();
  expect(await screen.findByText('Your verified evidence library is ready.')).toBeVisible();
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
  expect(submit.mock.calls.filter((call) => call[1] === 'POST')).toHaveLength(0);
  await user.click(screen.getByRole('button', { name: 'Upload evidence' }));
  await waitFor(() => {
    expect(submit).toHaveBeenCalledTimes(3);
  });
  const write = submit.mock.calls.find((call) => call[1] === 'POST');
  expect(write?.[0]).toBe('v1/evidence');
  expect(write?.[2]).toMatchObject({
    operationId: 'a'.repeat(32),
    originalFileName: 'statement.txt',
    contentType: 'text/plain',
  });
  expect(await screen.findByText('VERIFIED')).toBeVisible();
  await waitFor(() => {
    expect(
      screen.queryByRole('dialog', { name: 'Review evidence upload' }),
    ).not.toBeInTheDocument();
  });
  await user.click(screen.getByRole('button', { name: 'View provenance for statement.txt' }));
  expect(screen.getByRole('dialog', { name: 'Evidence provenance' })).toHaveTextContent(
    `document-version-${'a'.repeat(32)}`,
  );
});

it('does not expose upload when the current role lacks the capability', () => {
  const api = new AccessApi(session);
  vi.spyOn(api, 'product').mockResolvedValue({ items: [], dataDurability: 'Runtime only' });
  mount(api, ['evidence.document.read']);
  expect(screen.getByText('Evidence upload is not included in your current role')).toBeVisible();
  expect(document.querySelector('input[type="file"]')).toBeDisabled();
});

it('loads, filters and inspects the owner-confirmed evidence library', async () => {
  const api = new AccessApi(session);
  vi.spyOn(api, 'product').mockResolvedValue({
    dataDurability: 'Runtime only',
    items: [
      {
        documentId: 'document-001',
        documentVersionId: 'document-version-001',
        evidenceId: 'evidence-001',
        version: 1,
        sourceName: 'Customer upload',
        originalFileName: 'annual-statement.pdf',
        contentType: 'application/pdf',
        contentLength: 1024,
        contentSha256: 'c'.repeat(64),
        receivedAt: '2026-10-14T00:00:00Z',
        createdAt: '2026-10-14T00:00:00Z',
      },
      {
        documentId: 'document-002',
        documentVersionId: 'document-version-002',
        evidenceId: 'evidence-002',
        version: 1,
        sourceName: 'Customer upload',
        originalFileName: 'income.txt',
        contentType: 'text/plain',
        contentLength: 24,
        contentSha256: 'd'.repeat(64),
        receivedAt: '2026-10-13T00:00:00Z',
        createdAt: '2026-10-13T00:00:00Z',
      },
    ],
  });
  mount(api);
  const user = userEvent.setup();

  expect(await screen.findByText('annual-statement.pdf')).toBeVisible();
  expect(screen.getByText('income.txt')).toBeVisible();
  await user.type(screen.getByLabelText('Find evidence'), 'annual');
  expect(screen.getByText('annual-statement.pdf')).toBeVisible();
  expect(screen.queryByText('income.txt')).not.toBeInTheDocument();
  await user.click(
    screen.getByRole('button', { name: 'View provenance for annual-statement.pdf' }),
  );
  const drawer = await screen.findByRole('dialog', { name: 'Evidence provenance' });
  expect(drawer).toHaveTextContent('document-version-001');
  expect(drawer).toHaveTextContent('Raw document content is not exposed');
});

it('keeps the library hidden without evidence read authority', () => {
  const api = new AccessApi(session);
  const request = vi.spyOn(api, 'product');
  mount(api, ['evidence.document.upload']);
  expect(
    screen.getByText('Evidence library access is not included in your current role'),
  ).toBeVisible();
  expect(request).not.toHaveBeenCalled();
});
