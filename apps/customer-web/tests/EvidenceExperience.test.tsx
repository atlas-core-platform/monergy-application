import { FoundationProvider } from '@monergy/ui-foundation';
import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
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
  capabilities = [
    'evidence.document.upload',
    'evidence.document.read',
    'document-intelligence.document.process',
  ],
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

it('reviews authorized structured-text processing and displays source facts without profile mutation', async () => {
  const api = new AccessApi(session);
  const item = {
    documentId: 'document-003',
    documentVersionId: 'document-version-003',
    evidenceId: 'evidence-003',
    version: 1,
    sourceName: 'Customer upload',
    originalFileName: 'financial-facts.txt',
    contentType: 'text/plain',
    contentLength: 96,
    contentSha256: 'e'.repeat(64),
    receivedAt: '2026-10-14T00:00:00Z',
    createdAt: '2026-10-14T00:00:00Z',
  };
  const result = {
    job: {
      jobId: `job-processing-${'c'.repeat(32)}`,
      jobName: 'document-processing',
      ownerService: 'Document Intelligence Service',
      payloadReference: item.documentVersionId,
      customerId: 'reference-customer',
      state: 'Completed',
      attempt: 1,
      failureCode: null,
      retryable: false,
      updatedAt: '2026-10-14T00:00:01Z',
    },
    processing: {
      processingId: `processing-${'c'.repeat(32)}`,
      documentVersionId: item.documentVersionId,
      state: 'Completed',
      failureCode: null,
      retryable: false,
      updatedAt: '2026-10-14T00:00:01Z',
    },
    facts: [
      {
        sourceFactId: 'source-fact-001',
        customerId: 'reference-customer',
        factType: 'INCOME',
        label: 'Employment income',
        candidateValue: 125000,
        currency: 'USD',
        effectiveDate: '2026-10-01',
        sourceLocation: 'line 1',
        confidence: 0.98,
        evidenceId: item.evidenceId,
        documentVersionId: item.documentVersionId,
        extractionVersion: 'fixture-extractor/1.0.0',
        validationVersion: 'canonical-source-fact/1.0.0',
      },
    ],
    dataDurability: 'Runtime only',
    processingMode: 'Structured text reference processor',
  };
  const request = vi.spyOn(api, 'product').mockImplementation((path, method) => {
    if (path === 'v1/evidence' && method === 'GET')
      return Promise.resolve({ items: [item], dataDurability: 'Runtime only' });
    if (path.endsWith('/processing') && method === 'POST') return Promise.resolve(result);
    return Promise.reject(new Error(`Unexpected request: ${String(method)} ${path}`));
  });
  vi.spyOn(crypto, 'randomUUID').mockReturnValue('cccccccc-cccc-cccc-cccc-cccccccccccc');
  mount(api);
  const user = userEvent.setup();
  await user.click(
    await screen.findByRole('button', { name: 'View provenance for financial-facts.txt' }),
  );
  const provenance = screen.getByRole('dialog', { name: 'Evidence provenance' });
  await user.click(within(provenance).getByRole('button', { name: 'Review processing' }));
  const review = await screen.findByRole('dialog', { name: 'Review document processing' });
  expect(review).toHaveTextContent('Candidate facts only');
  expect(review).toHaveTextContent('does not update your financial profile');
  await user.click(within(review).getByRole('button', { name: 'Start processing' }));
  expect(await screen.findByText('Employment income')).toBeVisible();
  expect(review).toHaveTextContent('$125,000.00');
  expect(review).toHaveTextContent('98% confidence');
  expect(request).toHaveBeenCalledWith(
    `v1/evidence/${item.documentVersionId}/processing`,
    'POST',
    { operationId: 'c'.repeat(32) },
    expect.any(AbortSignal),
  );
});
