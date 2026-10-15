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
              environment: 'Test environment',
              dataDurability: 'Temporary data',
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

it('reviews the exact file before submitting and shows the upload confirmation', async () => {
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
    dataDurability: 'Temporary data',
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
      return Promise.resolve({ items: listed ? [item] : [], dataDurability: 'Temporary data' });
    listed = true;
    return Promise.resolve(receipt);
  });
  vi.spyOn(crypto, 'randomUUID').mockReturnValue('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa');
  mount(api);
  const user = userEvent.setup();
  expect(await screen.findByText('No documents yet.')).toBeVisible();
  const input = document.querySelector('input[type="file"]');
  expect(input).toBeInstanceOf(HTMLInputElement);
  await user.upload(
    input as HTMLInputElement,
    new File(['account evidence'], 'statement.txt', { type: 'text/plain' }),
  );
  const drawer = await screen.findByRole('dialog', { name: 'Review document upload' });
  expect(drawer).toHaveTextContent('statement.txt');
  expect(drawer).toHaveTextContent('Reference Customer');
  expect(drawer).toHaveTextContent('Test environment · Temporary data');
  expect(submit.mock.calls.filter((call) => call[1] === 'POST')).toHaveLength(0);
  await user.click(screen.getByRole('button', { name: 'Upload document' }));
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
  expect(await screen.findByText('UPLOADED')).toBeVisible();
  await waitFor(() => {
    expect(
      screen.queryByRole('dialog', { name: 'Review document upload' }),
    ).not.toBeInTheDocument();
  });
  await user.click(screen.getByRole('button', { name: 'View document details for statement.txt' }));
  const details = screen.getByRole('dialog', { name: 'Document details' });
  await user.click(within(details).getByText('Technical details'));
  expect(details).toHaveTextContent(`document-version-${'a'.repeat(32)}`);
});

it('does not expose upload when the current role lacks the capability', () => {
  const api = new AccessApi(session);
  vi.spyOn(api, 'product').mockResolvedValue({ items: [], dataDurability: 'Temporary data' });
  mount(api, ['evidence.document.read']);
  expect(screen.getByText('Document upload is not included in your current access')).toBeVisible();
  expect(document.querySelector('input[type="file"]')).toBeDisabled();
});

it('loads, filters and inspects the document library', async () => {
  const api = new AccessApi(session);
  vi.spyOn(api, 'product').mockResolvedValue({
    dataDurability: 'Temporary data',
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
  await user.type(screen.getByLabelText('Find documents'), 'annual');
  expect(screen.getByText('annual-statement.pdf')).toBeVisible();
  expect(screen.queryByText('income.txt')).not.toBeInTheDocument();
  await user.click(
    screen.getByRole('button', { name: 'View document details for annual-statement.pdf' }),
  );
  const drawer = await screen.findByRole('dialog', { name: 'Document details' });
  expect(drawer).toHaveTextContent('Technical details');
  await user.click(within(drawer).getByText('Technical details'));
  expect(drawer).toHaveTextContent('document-version-001');
});

it('keeps the library hidden without evidence read authority', () => {
  const api = new AccessApi(session);
  const request = vi.spyOn(api, 'product');
  mount(api, ['evidence.document.upload']);
  expect(screen.getByText("You don't have access to these documents")).toBeVisible();
  expect(request).not.toHaveBeenCalled();
});

it('requires explicit source-fact selection before updating Financial information', async () => {
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
    dataDurability: 'Temporary data',
    processingMode: 'Structured text reference processor',
  };
  const promotion = {
    profile: {
      financialProfileId: 'financial-profile-reference-customer',
      revision: 1,
      updatedAt: '2026-10-14T00:00:02Z',
      facts: [
        {
          financialFactId: 'financial-fact-001',
          factType: 'INCOME',
          label: 'Employment income',
          value: 125000,
          currency: 'USD',
          effectiveDate: '2026-10-01',
          revision: 1,
          source: {
            sourceName: 'financial-facts.txt',
            documentVersion: item.documentVersionId,
            recordedAt: '2026-10-14T00:00:02Z',
            technicalSourceFactId: 'source-fact-001',
          },
        },
      ],
      dataDurability: 'Temporary data',
    },
    confirmed: 1,
  };
  const request = vi.spyOn(api, 'product').mockImplementation((path, method) => {
    if (path === 'v1/evidence' && method === 'GET')
      return Promise.resolve({ items: [item], dataDurability: 'Temporary data' });
    if (path.endsWith('/processing') && method === 'POST') return Promise.resolve(result);
    if (path.includes('v1/financial-profile/from-processing/') && method === 'POST')
      return Promise.resolve(promotion);
    return Promise.reject(new Error(`Unexpected request: ${String(method)} ${path}`));
  });
  vi.spyOn(crypto, 'randomUUID')
    .mockReturnValueOnce('cccccccc-cccc-cccc-cccc-cccccccccccc')
    .mockReturnValueOnce('dddddddd-dddd-dddd-dddd-dddddddddddd');
  mount(api, [
    'evidence.document.upload',
    'evidence.document.read',
    'document-intelligence.document.process',
    'financial-profile.profile.update',
  ]);
  const user = userEvent.setup();
  await user.click(
    await screen.findByRole('button', { name: 'View document details for financial-facts.txt' }),
  );
  const provenance = screen.getByRole('dialog', { name: 'Document details' });
  await user.click(within(provenance).getByRole('button', { name: 'Extract details' }));
  const review = await screen.findByRole('dialog', { name: 'Extract and review details' });
  expect(review).toHaveTextContent('Review before adding anything to your financial information');
  expect(review).toHaveTextContent('Your financial information is not changed by this step');
  await user.click(within(review).getByRole('button', { name: 'Extract details' }));
  expect(await screen.findByText('Employment income')).toBeVisible();
  expect(review).toHaveTextContent('$125,000.00');
  expect(review).toHaveTextContent('98% confidence');
  expect(request).toHaveBeenCalledWith(
    `v1/evidence/${item.documentVersionId}/processing`,
    'POST',
    { operationId: 'c'.repeat(32) },
    expect.any(AbortSignal),
  );
  const add = within(review).getByRole('button', { name: 'Add selected information' });
  expect(add).toBeDisabled();
  expect(
    request.mock.calls.filter(([path]) =>
      String(path).includes('v1/financial-profile/from-processing/'),
    ),
  ).toHaveLength(0);

  await user.click(
    within(review).getByRole('checkbox', {
      name: 'Add Employment income to financial information',
    }),
  );
  expect(add).toBeEnabled();
  await user.click(add);

  expect(await screen.findByText('Your financial information was updated')).toBeVisible();
  expect(review).toHaveTextContent('1 reviewed item confirmed');
  expect(request).toHaveBeenCalledWith(
    `v1/financial-profile/from-processing/${result.job.jobId}`,
    'POST',
    {
      operationId: 'd'.repeat(32),
      sourceFactIds: ['source-fact-001'],
    },
    undefined,
  );
});


it('opens the exact referenced document when navigating from financial information', async () => {
  window.history.replaceState(null, '', '/evidence?documentVersion=document-version-002');
  const api = new AccessApi(session);
  vi.spyOn(api, 'product').mockResolvedValue({
    dataDurability: 'Temporary data',
    items: [
      {
        documentId: 'document-001',
        documentVersionId: 'document-version-001',
        evidenceId: 'evidence-001',
        version: 1,
        sourceName: 'Customer upload',
        originalFileName: 'first.txt',
        contentType: 'text/plain',
        contentLength: 10,
        contentSha256: 'a'.repeat(64),
        receivedAt: '2026-10-14T00:00:00Z',
        createdAt: '2026-10-14T00:00:00Z',
      },
      {
        documentId: 'document-002',
        documentVersionId: 'document-version-002',
        evidenceId: 'evidence-002',
        version: 1,
        sourceName: 'Customer upload',
        originalFileName: 'source.txt',
        contentType: 'text/plain',
        contentLength: 10,
        contentSha256: 'b'.repeat(64),
        receivedAt: '2026-10-15T00:00:00Z',
        createdAt: '2026-10-15T00:00:00Z',
      },
    ],
  });
  mount(api);

  const drawer = await screen.findByRole('dialog', { name: 'Document details' });
  expect(drawer).toHaveTextContent('source.txt');
  expect(drawer).not.toHaveTextContent('first.txt');
  window.history.replaceState(null, '', '/');
});
