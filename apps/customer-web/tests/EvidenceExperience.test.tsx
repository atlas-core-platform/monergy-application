import { readFileSync } from 'node:fs';
import { URL as FileUrl } from 'node:url';
import { financialXlsxType } from '../src/product/productApi';
import { FoundationProvider } from '@monergy/ui-foundation';
import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, it, vi } from 'vitest';
import { AccessApi, AccessApiError } from '../src/access/accessApi';
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
  window.history.replaceState(null, '', '/');
});
async function openCompletedProcessingResult(user: ReturnType<typeof userEvent.setup>) {
  // Wait for the owner-backed activity row, not an unrelated page control.
  // This gives a precise failing boundary if the durable processing list never loads.
  const activity = within(screen.getByRole('region', { name: 'Recent document processing' }));
  expect(await activity.findByText('Completed')).toBeVisible();
  await user.click(activity.getByRole('button', { name: 'View result' }));
}


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

it.each(['text/plain', 'image/png', 'application/pdf', 'image/jpeg', financialXlsxType])('requires explicit source-fact selection before updating Financial information from %s', async (contentType) => {
  const api = new AccessApi(session);
  const item = {
    documentId: 'document-003',
    documentVersionId: 'document-version-003',
    evidenceId: 'evidence-003',
    version: 1,
    sourceName: 'Customer upload',
    originalFileName: 'financial-facts.txt',
    contentType,
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
        extractionVersion: contentType === financialXlsxType ? 'financial-xlsx/1.0.0' : contentType === 'image/jpeg' ? 'salary-slip-jpeg-ocr/1.0.0' : contentType === 'application/pdf' ? 'salary-slip-pdf-ocr/1.0.0' : contentType === 'image/png' ? 'salary-slip-png-ocr/1.0.0' : 'fixture-extractor/1.0.0',
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
  await screen.findByText('financial-facts.txt');
  await user.click(
    screen.getByRole('button', { name: 'View document details for financial-facts.txt' }),
  );
  const provenance = screen.getByRole('dialog', { name: 'Document details' });
  await user.click(within(provenance).getByRole('button', { name: 'Extract details' }));
  const review = await screen.findByRole('dialog', { name: 'Extract and review details' });
  expect(review).toHaveTextContent('Review before adding anything to your financial information');
  expect(review).toHaveTextContent('Your financial information is not changed by this step');
  await user.click(within(review).getByRole('button', { name: 'Extract details' }));
  expect(await screen.findByText('Employment income')).toBeVisible();
  if (contentType !== 'text/plain' && contentType !== financialXlsxType) expect(review).toHaveTextContent('Recognized as a salary slip');
  expect(review).toHaveTextContent('$125,000.00');
  expect(review).toHaveTextContent(contentType === financialXlsxType ? 'Copied from Excel' : '98% confidence');
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
      path.includes('v1/financial-profile/from-processing/'),
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

it('opens the exact authorized source document version from Financial Passport history', async () => {
  window.history.replaceState(null, '', '/evidence?version=document-version-002');
  const api = new AccessApi(session);
  vi.spyOn(api, 'product').mockImplementation((path) => {
    if (path === 'v1/evidence')
      return Promise.resolve({ dataDurability: 'Temporary data', items: [] });
    if (path === 'v1/evidence/source/document-version-002')
      return Promise.resolve({
        documentId: 'document-002',
        documentVersionId: 'document-version-002',
        evidenceId: 'evidence-002',
        version: 2,
        sourceName: 'Customer upload',
        originalFileName: 'updated-income.txt',
        contentType: 'text/plain',
        contentSha256: 'b'.repeat(64),
        receivedAt: '2026-10-15T00:00:00Z',
        dataDurability: 'Temporary data',
      });
    return Promise.reject(new Error('unexpected product endpoint'));
  });

  mount(api, ['evidence.document.read']);

  const drawer = await screen.findByRole('dialog', { name: 'Source document details' });
  expect(drawer).toHaveTextContent('updated-income.txt');
  expect(drawer).toHaveTextContent('Version');
  expect(drawer).toHaveTextContent('2');
  expect(drawer).toHaveTextContent('Customer upload');
  expect(window.location.pathname).toBe('/evidence');
  expect(window.location.search).toBe('');
});

it('fails closed when a requested source document version is outside current Evidence access', async () => {
  window.history.replaceState(null, '', '/evidence?version=document-version-hidden');
  const api = new AccessApi(session);
  vi.spyOn(api, 'product').mockImplementation((path) => {
    if (path === 'v1/evidence')
      return Promise.resolve({ dataDurability: 'Temporary data', items: [] });
    if (path === 'v1/evidence/source/document-version-hidden')
      return Promise.reject(new AccessApiError(404, 'EVIDENCE_VERSION_NOT_FOUND'));
    return Promise.reject(new Error('unexpected product endpoint'));
  });

  mount(api, ['evidence.document.read']);

  expect(
    await screen.findByText("The source document version isn't available in your current document access."),
  ).toBeVisible();
  expect(
    screen.queryByRole('dialog', { name: 'Source document details' }),
  ).not.toBeInTheDocument();
});


it('sorts Documents deterministically and links an exact document version to trusted financial information', async () => {
  const api = new AccessApi(session);
  const documents = [
    {
      documentId: 'document-002',
      documentVersionId: 'document-version-002',
      evidenceId: 'evidence-002',
      version: 1,
      sourceName: 'Customer upload',
      originalFileName: 'zeta-expense.txt',
      contentType: 'text/plain',
      contentLength: 32,
      contentSha256: 'd'.repeat(64),
      receivedAt: '2026-10-15T08:00:00Z',
      createdAt: '2026-10-15T08:00:00Z',
    },
    {
      documentId: 'document-001',
      documentVersionId: 'document-version-001',
      evidenceId: 'evidence-001',
      version: 1,
      sourceName: 'Customer upload',
      originalFileName: 'annual-income.pdf',
      contentType: 'application/pdf',
      contentLength: 1024,
      contentSha256: 'c'.repeat(64),
      receivedAt: '2026-10-14T08:00:00Z',
      createdAt: '2026-10-14T08:00:00Z',
    },
  ];
  vi.spyOn(api, 'product').mockImplementation((path) => {
    if (path === 'v1/evidence')
      return Promise.resolve({ dataDurability: 'Temporary data', items: documents });
    if (path === 'v1/financial-profile')
      return Promise.resolve({
        financialProfileId: 'financial-profile-reference-customer',
        revision: 1,
        updatedAt: '2026-10-15T09:00:00Z',
        dataDurability: 'Temporary data',
        facts: [
          {
            financialFactId: 'ff-income',
            factType: 'INCOME',
            label: 'Employment income',
            value: 125000,
            currency: 'USD',
            effectiveDate: '2026-10-01',
            revision: 1,
            source: {
              sourceName: 'annual-income.pdf',
              documentVersion: 'document-version-001',
              recordedAt: '2026-10-15T09:00:00Z',
              technicalSourceFactId: 'source-income',
              technicalProvenanceId: 'provenance-income',
            },
          },
          {
            financialFactId: 'ff-future',
            factType: 'UNSUPPORTED',
            label: 'Future investment',
            value: 500,
            currency: 'USD',
            effectiveDate: '2026-10-01',
            revision: 1,
            source: {
              sourceName: 'annual-income.pdf',
              documentVersion: 'document-version-001',
              recordedAt: '2026-10-15T09:00:00Z',
              technicalSourceFactId: 'source-future',
              technicalProvenanceId: 'provenance-future',
            },
          },
        ],
      });
    return Promise.reject(new Error('unexpected product endpoint'));
  });

  mount(api, ['evidence.document.read', 'financial-profile.profile.read']);
  const user = userEvent.setup();

  await screen.findByText('zeta-expense.txt');
  const initial = screen.getAllByRole('button', { name: /View document details for/ });
  expect(initial[0]).toHaveAccessibleName('View document details for zeta-expense.txt');
  expect(screen.getByText('1 trusted item')).toBeVisible();
  expect(screen.getByText('0 trusted items')).toBeVisible();

  await user.click(screen.getByLabelText('Sort documents'));
  await user.click(await screen.findByText('File name A–Z'));

  const sorted = screen.getAllByRole('button', { name: /View document details for/ });
  expect(sorted[0]).toHaveAccessibleName('View document details for annual-income.pdf');

  await user.click(
    screen.getByRole('button', { name: 'View document details for annual-income.pdf' }),
  );
  const drawer = await screen.findByRole('dialog', { name: 'Document details' });
  expect(drawer).toHaveTextContent('Trusted information using this document version');
  expect(drawer).toHaveTextContent('Employment income');
  expect(drawer).toHaveTextContent('$125,000.00');
  expect(drawer).not.toHaveTextContent('Future investment');
  expect(within(drawer).getByRole('link', { name: /Employment income/ })).toHaveAttribute(
    'href',
    '/finances?fact=ff-income',
  );
  expect(drawer).toHaveTextContent('Customer upload');
  expect(drawer).toHaveTextContent('Received');
});

it('shows a truthful no-link state only after Financial Profile has loaded successfully', async () => {
  const api = new AccessApi(session);
  vi.spyOn(api, 'product').mockImplementation((path) => {
    if (path === 'v1/evidence')
      return Promise.resolve({
        dataDurability: 'Temporary data',
        items: [
          {
            documentId: 'document-002',
            documentVersionId: 'document-version-002',
            evidenceId: 'evidence-002',
            version: 1,
            sourceName: 'Customer upload',
            originalFileName: 'unlinked.pdf',
            contentType: 'application/pdf',
            contentLength: 256,
            contentSha256: 'e'.repeat(64),
            receivedAt: '2026-10-15T08:00:00Z',
            createdAt: '2026-10-15T08:00:00Z',
          },
        ],
      });
    if (path === 'v1/financial-profile')
      return Promise.resolve({
        financialProfileId: 'financial-profile-reference-customer',
        revision: 0,
        updatedAt: null,
        dataDurability: 'Temporary data',
        facts: [],
      });
    return Promise.reject(new Error('unexpected product endpoint'));
  });

  mount(api, ['evidence.document.read', 'financial-profile.profile.read']);
  const user = userEvent.setup();
  await screen.findByText('unlinked.pdf');
  await user.click(
    screen.getByRole('button', { name: 'View document details for unlinked.pdf' }),
  );

  const drawer = await screen.findByRole('dialog', { name: 'Document details' });
  expect(drawer).toHaveTextContent(
    'No trusted financial information currently uses this document version.',
  );
  expect(screen.getByText('0 trusted items')).toBeVisible();
});

it('never turns an unavailable Financial Profile into a false zero-linked state', async () => {
  const api = new AccessApi(session);
  vi.spyOn(api, 'product').mockImplementation((path) => {
    if (path === 'v1/evidence')
      return Promise.resolve({
        dataDurability: 'Temporary data',
        items: [
          {
            documentId: 'document-003',
            documentVersionId: 'document-version-003',
            evidenceId: 'evidence-003',
            version: 1,
            sourceName: 'Customer upload',
            originalFileName: 'unknown-links.pdf',
            contentType: 'application/pdf',
            contentLength: 256,
            contentSha256: 'f'.repeat(64),
            receivedAt: '2026-10-15T08:00:00Z',
            createdAt: '2026-10-15T08:00:00Z',
          },
        ],
      });
    if (path === 'v1/financial-profile')
      return Promise.reject(new AccessApiError(503, 'FINANCIAL_PROFILE_OWNER_UNAVAILABLE'));
    return Promise.reject(new Error('unexpected product endpoint'));
  });

  mount(api, ['evidence.document.read', 'financial-profile.profile.read']);
  const user = userEvent.setup();
  await screen.findByText('unknown-links.pdf');
  await user.click(
    screen.getByRole('button', { name: 'View document details for unknown-links.pdf' }),
  );

  const drawer = await screen.findByRole('dialog', { name: 'Document details' });
  expect(drawer).toHaveTextContent("We couldn't refresh linked financial information.");
  expect(drawer).not.toHaveTextContent(
    'No trusted Income or Expense information currently uses this document version.',
  );
  expect(screen.queryByText('0 trusted items')).not.toBeInTheDocument();
});

it('keeps linked Financial information hidden when it is outside the current access package', async () => {
  const api = new AccessApi(session);
  vi.spyOn(api, 'product').mockResolvedValue({
    dataDurability: 'Temporary data',
    items: [
      {
        documentId: 'document-004',
        documentVersionId: 'document-version-004',
        evidenceId: 'evidence-004',
        version: 1,
        sourceName: 'Customer upload',
        originalFileName: 'document-only.pdf',
        contentType: 'application/pdf',
        contentLength: 256,
        contentSha256: 'a'.repeat(64),
        receivedAt: '2026-10-15T08:00:00Z',
        createdAt: '2026-10-15T08:00:00Z',
      },
    ],
  });

  mount(api, ['evidence.document.read']);
  const user = userEvent.setup();
  await screen.findByText('document-only.pdf');
  await user.click(
    screen.getByRole('button', { name: 'View document details for document-only.pdf' }),
  );

  const drawer = await screen.findByRole('dialog', { name: 'Document details' });
  expect(drawer).toHaveTextContent(
    'Linked financial information is not included in your current access.',
  );
});


it('recovers recent durable document-processing activity and opens the matching authorized Document', async () => {
  const api = new AccessApi(session);
  const item = {
    documentId: 'document-activity',
    documentVersionId: 'document-version-activity',
    evidenceId: 'evidence-activity',
    version: 1,
    sourceName: 'Customer upload',
    originalFileName: 'activity-statement.txt',
    contentType: 'text/plain',
    contentLength: 64,
    contentSha256: '9'.repeat(64),
    receivedAt: '2026-10-16T08:00:00Z',
    createdAt: '2026-10-16T08:00:00Z',
  };
  const request = vi.spyOn(api, 'product').mockImplementation((path) => {
    if (path === 'v1/evidence')
      return Promise.resolve({ items: [item], dataDurability: 'Temporary data' });
    if (path === 'v1/processing-activity')
      return Promise.resolve({
        dataDurability: 'Temporary data',
        items: [
          {
            jobId: 'job-processing-activity',
            documentVersionId: item.documentVersionId,
            state: 'Completed',
            attempt: 1,
            updatedAt: '2026-10-16T09:00:00Z',
          },
        ],
      });
    return Promise.reject(new Error('unexpected product endpoint'));
  });

  mount(api, ['evidence.document.read', 'job-management.job.read']);
  const user = userEvent.setup();

  const activity = within(
    screen.getByRole('region', { name: 'Recent document processing' }),
  );
  expect(await activity.findByText('activity-statement.txt')).toBeVisible();
  expect(activity.getByText('Completed')).toBeVisible();
  expect(activity.getByText(/Attempt 1/)).toBeVisible();
  expect(request).toHaveBeenCalledWith(
    'v1/processing-activity',
    'GET',
    undefined,
    expect.any(AbortSignal),
  );

  await user.click(activity.getByRole('button', { name: 'Open document' }));
  expect(await screen.findByRole('dialog', { name: 'Document details' })).toHaveTextContent(
    'activity-statement.txt',
  );
  expect(screen.queryByRole('button', { name: /retry/i })).not.toBeInTheDocument();
  expect(screen.queryByRole('button', { name: /cancel/i })).not.toBeInTheDocument();
});

it('shows failed processing as a read-only failure state without exposing retry or support controls', async () => {
  const api = new AccessApi(session);
  vi.spyOn(api, 'product').mockImplementation((path) => {
    if (path === 'v1/evidence')
      return Promise.resolve({ items: [], dataDurability: 'Temporary data' });
    if (path === 'v1/processing-activity')
      return Promise.resolve({
        dataDurability: 'Temporary data',
        items: [
          {
            jobId: 'job-processing-failed',
            documentVersionId: 'document-version-failed',
            state: 'Failed',
            attempt: 2,
            updatedAt: '2026-10-16T09:00:00Z',
          },
        ],
      });
    return Promise.reject(new Error('unexpected product endpoint'));
  });

  mount(api, ['evidence.document.read', 'job-management.job.read']);

  expect(await screen.findByText("Couldn't complete")).toBeVisible();
  expect(screen.getByText('Document processing')).toBeVisible();
  expect(screen.getByText(/Attempt 2/)).toBeVisible();
  expect(
    screen.getByText(/Retry, cancellation, reconciliation and support controls are not part/),
  ).toBeVisible();
  expect(screen.queryByRole('button', { name: /retry/i })).not.toBeInTheDocument();
  expect(screen.queryByRole('button', { name: /cancel/i })).not.toBeInTheDocument();
});

it('never turns unavailable Job Management activity into an empty history', async () => {
  const api = new AccessApi(session);
  vi.spyOn(api, 'product').mockImplementation((path) => {
    if (path === 'v1/evidence')
      return Promise.resolve({ items: [], dataDurability: 'Temporary data' });
    if (path === 'v1/processing-activity')
      return Promise.reject(new AccessApiError(503, 'JOB_OWNER_UNAVAILABLE'));
    return Promise.reject(new Error('unexpected product endpoint'));
  });

  mount(api, ['evidence.document.read', 'job-management.job.read']);

  expect(
    await screen.findByText("We couldn't refresh document processing activity."),
  ).toBeVisible();
  expect(screen.queryByText('No document processing activity yet.')).not.toBeInTheDocument();
});

it('does not call processing activity when the current access package lacks job read authority', () => {
  const api = new AccessApi(session);
  const request = vi.spyOn(api, 'product').mockResolvedValue({
    items: [],
    dataDurability: 'Temporary data',
  });

  mount(api, ['evidence.document.read']);

  expect(
    screen.getByText("Processing activity isn't included in your current access"),
  ).toBeVisible();
  expect(request).not.toHaveBeenCalledWith(
    'v1/processing-activity',
    expect.anything(),
    expect.anything(),
    expect.anything(),
  );
});


it('loads authorized Document version history lazily and opens an exact historical version', async () => {
  const api = new AccessApi(session);
  const current = {
    documentId: 'document-history',
    documentVersionId: 'document-version-002',
    evidenceId: 'evidence-002',
    version: 2,
    sourceName: 'Customer upload',
    originalFileName: 'income-updated.txt',
    contentType: 'text/plain',
    contentLength: 40,
    contentSha256: '2'.repeat(64),
    receivedAt: '2026-10-16T09:00:00Z',
    createdAt: '2026-10-16T09:00:00Z',
  };
  const request = vi.spyOn(api, 'product').mockImplementation((path) => {
    if (path === 'v1/evidence')
      return Promise.resolve({ items: [current], dataDurability: 'Temporary data' });
    if (path === 'v1/evidence/document-history/versions')
      return Promise.resolve({
        documentId: 'document-history',
        dataDurability: 'Temporary data',
        versions: [
          {
            documentVersionId: 'document-version-002',
            evidenceId: 'evidence-002',
            version: 2,
            sourceName: 'Customer upload',
            originalFileName: 'income-updated.txt',
            contentType: 'text/plain',
            contentSha256: '2'.repeat(64),
            receivedAt: '2026-10-16T09:00:00Z',
          },
          {
            documentVersionId: 'document-version-001',
            evidenceId: 'evidence-001',
            version: 1,
            sourceName: 'Customer upload',
            originalFileName: 'income-original.txt',
            contentType: 'text/plain',
            contentSha256: '1'.repeat(64),
            receivedAt: '2026-10-15T09:00:00Z',
          },
        ],
      });
    if (path === 'v1/evidence/source/document-version-001')
      return Promise.resolve({
        documentId: 'document-history',
        documentVersionId: 'document-version-001',
        evidenceId: 'evidence-001',
        version: 1,
        sourceName: 'Customer upload',
        originalFileName: 'income-original.txt',
        contentType: 'text/plain',
        contentSha256: '1'.repeat(64),
        receivedAt: '2026-10-15T09:00:00Z',
        dataDurability: 'Temporary data',
      });
    return Promise.reject(new Error('unexpected product endpoint'));
  });

  mount(api, ['evidence.document.read']);
  const user = userEvent.setup();
  await screen.findByText('income-updated.txt');
  await user.click(
    screen.getByRole('button', { name: 'View document details for income-updated.txt' }),
  );

  const drawer = await screen.findByRole('dialog', { name: 'Document details' });
  expect(await within(drawer).findByText('Versions of this document')).toBeVisible();
  expect(within(drawer).getByText('2 versions')).toBeVisible();
  expect(within(drawer).getByText('Version 2')).toBeVisible();
  expect(within(drawer).getByText('Version 1')).toBeVisible();
  expect(within(drawer).getByText('income-original.txt')).toBeVisible();
  expect(within(drawer).getByRole('button', { name: 'Current view' })).toBeDisabled();

  await user.click(within(drawer).getByRole('button', { name: 'Open version' }));

  const source = await screen.findByRole('dialog', { name: 'Source document details' });
  expect(source).toHaveTextContent('income-original.txt');
  expect(source).toHaveTextContent('Version');
  expect(source).toHaveTextContent('1');
  expect(request).toHaveBeenCalledWith(
    'v1/evidence/source/document-version-001',
    'GET',
    undefined,
    undefined,
  );
});

it('keeps Document history unavailable distinct from a one-version history', async () => {
  const api = new AccessApi(session);
  vi.spyOn(api, 'product').mockImplementation((path) => {
    if (path === 'v1/evidence')
      return Promise.resolve({
        items: [
          {
            documentId: 'document-history-failure',
            documentVersionId: 'document-version-failure',
            evidenceId: 'evidence-failure',
            version: 1,
            sourceName: 'Customer upload',
            originalFileName: 'history-failure.pdf',
            contentType: 'application/pdf',
            contentLength: 256,
            contentSha256: 'f'.repeat(64),
            receivedAt: '2026-10-16T09:00:00Z',
            createdAt: '2026-10-16T09:00:00Z',
          },
        ],
        dataDurability: 'Temporary data',
      });
    if (path === 'v1/evidence/document-history-failure/versions')
      return Promise.reject(new AccessApiError(503, 'EVIDENCE_OWNER_UNAVAILABLE'));
    return Promise.reject(new Error('unexpected product endpoint'));
  });

  mount(api, ['evidence.document.read']);
  const user = userEvent.setup();
  await screen.findByText('history-failure.pdf');
  await user.click(
    screen.getByRole('button', { name: 'View document details for history-failure.pdf' }),
  );

  const drawer = await screen.findByRole('dialog', { name: 'Document details' });
  expect(
    await within(drawer).findByText("We couldn't refresh this document's version history."),
  ).toBeVisible();
  expect(within(drawer).queryByText('1 version')).not.toBeInTheDocument();
});

it('fails closed when Document history response identity does not match the selected Document', async () => {
  const api = new AccessApi(session);
  vi.spyOn(api, 'product').mockImplementation((path) => {
    if (path === 'v1/evidence')
      return Promise.resolve({
        items: [
          {
            documentId: 'document-history-expected',
            documentVersionId: 'document-version-expected',
            evidenceId: 'evidence-expected',
            version: 1,
            sourceName: 'Customer upload',
            originalFileName: 'history-identity.pdf',
            contentType: 'application/pdf',
            contentLength: 256,
            contentSha256: 'a'.repeat(64),
            receivedAt: '2026-10-16T09:00:00Z',
            createdAt: '2026-10-16T09:00:00Z',
          },
        ],
        dataDurability: 'Temporary data',
      });
    if (path === 'v1/evidence/document-history-expected/versions')
      return Promise.resolve({
        documentId: 'document-history-other',
        dataDurability: 'Temporary data',
        versions: [],
      });
    return Promise.reject(new Error('unexpected product endpoint'));
  });

  mount(api, ['evidence.document.read']);
  const user = userEvent.setup();
  await screen.findByText('history-identity.pdf');
  await user.click(
    screen.getByRole('button', { name: 'View document details for history-identity.pdf' }),
  );

  const drawer = await screen.findByRole('dialog', { name: 'Document details' });
  expect(
    await within(drawer).findByText("We couldn't refresh this document's version history."),
  ).toBeVisible();
});


it.each([undefined, 'salary-slip-pdf-ocr/1.0.0', 'salary-slip-jpeg-ocr/1.0.0', 'loan-statement-png-ocr/1.0.0', 'loan-statement-jpeg-ocr/1.0.0', 'loan-statement-pdf-ocr/1.0.0', 'insurance-policy-png-ocr/1.0.0', 'insurance-policy-jpeg-ocr/1.0.0', 'insurance-policy-pdf-ocr/1.0.0'])('reopens a completed result with extraction version %s read-only from durable activity', async (extractionVersion) => {
  const api = new AccessApi(session);
  const jpeg = extractionVersion?.includes('-jpeg-');
  const png = extractionVersion?.includes('-png-');
  const loan = extractionVersion?.startsWith('loan-statement-');
  const insurance = extractionVersion?.startsWith('insurance-policy-');
  const document = {
    documentId: 'document-result',
    documentVersionId: 'document-version-result',
    evidenceId: 'evidence-result',
    version: 1,
    sourceName: 'Customer upload',
    originalFileName: jpeg ? 'recovered-document.jpg' : png ? 'recovered-document.png' : extractionVersion ? 'recovered-document.pdf' : 'recovered-result.txt',
    contentType: jpeg ? 'image/jpeg' : png ? 'image/png' : extractionVersion ? 'application/pdf' : 'text/plain',
    contentLength: 64,
    contentSha256: '7'.repeat(64),
    receivedAt: '2026-10-16T08:00:00Z',
    createdAt: '2026-10-16T08:00:00Z',
  };
  const request = vi.spyOn(api, 'product').mockImplementation((path) => {
    if (path === 'v1/evidence')
      return Promise.resolve({ items: [document], dataDurability: 'Temporary data' });
    if (path === 'v1/processing-activity')
      return Promise.resolve({
        dataDurability: 'Temporary data',
        items: [
          {
            jobId: 'job-processing-recovered',
            documentVersionId: document.documentVersionId,
            state: 'Completed',
            attempt: 1,
            updatedAt: '2026-10-16T09:00:00Z',
          },
        ],
      });
    if (path === 'v1/processing-result/job-processing-recovered')
      return Promise.resolve({
        jobId: 'job-processing-recovered',
        documentVersionId: document.documentVersionId,
        state: 'Completed',
        updatedAt: '2026-10-16T09:00:00Z',
        dataDurability: 'Temporary data',
        facts: [
          {
            extractionVersion,
            sourceFactId: 'source-fact-income',
            factType: insurance ? 'INSURANCE' : loan ? 'LOAN' : 'INCOME',
            label: insurance ? 'Sum assured stated on policy' : loan ? 'Outstanding loan balance stated on document' : 'Employment income',
            candidateValue: 125000,
            currency: 'USD',
            effectiveDate: '2026-10-01',
            sourceLocation: 'line 1',
            confidence: 0.98,
            documentVersionId: document.documentVersionId,
          },
          {
            sourceFactId: 'source-fact-expense',
            extractionVersion: insurance ? extractionVersion : undefined,
            factType: insurance ? 'INSURANCE' : 'EXPENSE',
            label: insurance ? 'Premium stated on policy (frequency not established)' : 'Housing',
            candidateValue: 2500,
            currency: 'USD',
            effectiveDate: '2026-10-01',
            sourceLocation: 'line 2',
            confidence: 0.96,
            documentVersionId: document.documentVersionId,
          },
        ].filter((fact) => !extractionVersion || fact.factType === (insurance ? 'INSURANCE' : loan ? 'LOAN' : 'INCOME')),
      });
    return Promise.reject(new Error('unexpected product endpoint'));
  });

  mount(api, ['evidence.document.read', 'job-management.job.read']);
  const user = userEvent.setup();

  await openCompletedProcessingResult(user);

  const drawer = await screen.findByRole('dialog', { name: 'Processing result' });
  expect(drawer).toHaveTextContent(document.originalFileName);
  if (extractionVersion) expect(drawer).toHaveTextContent(insurance ? 'Recognized as an insurance policy' : loan ? 'Recognized as a loan statement' : 'Recognized as a salary slip');
  expect(drawer).toHaveTextContent(insurance ? 'Sum assured stated on policy' : loan ? 'Outstanding loan balance stated on document' : 'Employment income');
  if (insurance) {
    expect(drawer).toHaveTextContent('Premium stated on policy (frequency not established)');
    expect(drawer).toHaveTextContent('does not verify active cover');
    expect(drawer).not.toHaveTextContent('Recognized as a salary slip');
  }
  if (loan) expect(drawer).toHaveTextContent('It does not establish your current debt');
  expect(drawer).toHaveTextContent('$125,000.00');
  expect(drawer).toHaveTextContent('98% confidence');
  if (!extractionVersion) expect(drawer).toHaveTextContent('Housing');
  expect(drawer).toHaveTextContent('This recovered view is read-only');
  expect(within(drawer).queryByRole('checkbox')).not.toBeInTheDocument();
  expect(
    within(drawer).queryByRole('button', { name: 'Add selected information' }),
  ).not.toBeInTheDocument();
  expect(request).toHaveBeenCalledWith(
    'v1/processing-result/job-processing-recovered',
    'GET',
    undefined,
    undefined,
  );
});

it('does not expose recovered processing content with job-read alone', async () => {
  const api = new AccessApi(session);
  const request = vi.spyOn(api, 'product').mockImplementation((path) => {
    if (path === 'v1/processing-activity')
      return Promise.resolve({
        dataDurability: 'Temporary data',
        items: [
          {
            jobId: 'job-processing-status-only',
            documentVersionId: 'document-version-status-only',
            state: 'Completed',
            attempt: 1,
            updatedAt: '2026-10-16T09:00:00Z',
          },
        ],
      });
    return Promise.reject(new Error('unexpected product endpoint'));
  });

  mount(api, ['job-management.job.read']);

  expect(await screen.findByText('Completed')).toBeVisible();
  expect(screen.queryByRole('button', { name: 'View result' })).not.toBeInTheDocument();
  expect(
    request.mock.calls.some(([path]) => path.startsWith('v1/processing-result/')),
  ).toBe(false);
});

it('keeps failed processing activity as status only and never offers a recovered result', async () => {
  const api = new AccessApi(session);
  vi.spyOn(api, 'product').mockImplementation((path) => {
    if (path === 'v1/evidence')
      return Promise.resolve({ items: [], dataDurability: 'Temporary data' });
    if (path === 'v1/processing-activity')
      return Promise.resolve({
        dataDurability: 'Temporary data',
        items: [
          {
            jobId: 'job-processing-failed-result',
            documentVersionId: 'document-version-failed-result',
            state: 'Failed',
            attempt: 1,
            updatedAt: '2026-10-16T09:00:00Z',
          },
        ],
      });
    return Promise.reject(new Error('unexpected product endpoint'));
  });

  mount(api, ['evidence.document.read', 'job-management.job.read']);

  expect(await screen.findByText("Couldn't complete")).toBeVisible();
  expect(screen.queryByRole('button', { name: 'View result' })).not.toBeInTheDocument();
});

it('fails closed when a recovered processing result is no longer available', async () => {
  const api = new AccessApi(session);
  vi.spyOn(api, 'product').mockImplementation((path) => {
    if (path === 'v1/evidence')
      return Promise.resolve({
        items: [
          {
            documentId: 'document-gone',
            documentVersionId: 'document-version-gone',
            evidenceId: 'evidence-gone',
            version: 1,
            sourceName: 'Customer upload',
            originalFileName: 'gone.txt',
            contentType: 'text/plain',
            contentLength: 24,
            contentSha256: '8'.repeat(64),
            receivedAt: '2026-10-16T08:00:00Z',
            createdAt: '2026-10-16T08:00:00Z',
          },
        ],
        dataDurability: 'Temporary data',
      });
    if (path === 'v1/processing-activity')
      return Promise.resolve({
        dataDurability: 'Temporary data',
        items: [
          {
            jobId: 'job-processing-gone',
            documentVersionId: 'document-version-gone',
            state: 'Completed',
            attempt: 1,
            updatedAt: '2026-10-16T09:00:00Z',
          },
        ],
      });
    if (path === 'v1/processing-result/job-processing-gone')
      return Promise.reject(new AccessApiError(404, 'PROCESSING_RESULT_NOT_FOUND'));
    return Promise.reject(new Error('unexpected product endpoint'));
  });

  mount(api, ['evidence.document.read', 'job-management.job.read']);
  const user = userEvent.setup();
  await openCompletedProcessingResult(user);

  const drawer = await screen.findByRole('dialog', { name: 'Processing result' });
  expect(drawer).toHaveTextContent(
    "This processing result isn't available in your current access.",
  );
  expect(drawer).not.toHaveTextContent('Add selected information');
});


it('resumes explicit Financial review from a recovered completed result', async () => {
  const api = new AccessApi(session);
  const document = {
    documentId: 'document-resume',
    documentVersionId: 'document-version-resume',
    evidenceId: 'evidence-resume',
    version: 1,
    sourceName: 'Customer upload',
    originalFileName: 'resume-review.txt',
    contentType: 'text/plain',
    contentLength: 64,
    contentSha256: '6'.repeat(64),
    receivedAt: '2026-10-16T08:00:00Z',
    createdAt: '2026-10-16T08:00:00Z',
  };
  const emptyProfile = {
    financialProfileId: 'financial-profile-reference-customer',
    revision: 0,
    updatedAt: null,
    dataDurability: 'Temporary data',
    facts: [],
  };
  const promotedProfile = {
    financialProfileId: 'financial-profile-reference-customer',
    revision: 1,
    updatedAt: '2026-10-16T09:05:00Z',
    dataDurability: 'Temporary data',
    facts: [
      {
        financialFactId: 'financial-fact-income',
        factType: 'INCOME',
        label: 'Employment income',
        value: 125000,
        currency: 'USD',
        effectiveDate: '2026-10-01',
        revision: 1,
        source: {
          sourceName: 'resume-review.txt',
          documentVersion: document.documentVersionId,
          recordedAt: '2026-10-16T09:05:00Z',
          technicalSourceFactId: 'source-fact-income',
          technicalProvenanceId: 'provenance-income',
        },
      },
    ],
  };
  const request = vi.spyOn(api, 'product').mockImplementation((path, method, body) => {
    if (path === 'v1/evidence')
      return Promise.resolve({ items: [document], dataDurability: 'Temporary data' });
    if (path === 'v1/financial-profile')
      return Promise.resolve(emptyProfile);
    if (path === 'v1/processing-activity')
      return Promise.resolve({
        dataDurability: 'Temporary data',
        items: [
          {
            jobId: 'job-processing-resume',
            documentVersionId: document.documentVersionId,
            state: 'Completed',
            attempt: 1,
            updatedAt: '2026-10-16T09:00:00Z',
          },
        ],
      });
    if (path === 'v1/processing-result/job-processing-resume')
      return Promise.resolve({
        jobId: 'job-processing-resume',
        documentVersionId: document.documentVersionId,
        state: 'Completed',
        updatedAt: '2026-10-16T09:00:00Z',
        dataDurability: 'Temporary data',
        facts: [
          {
            sourceFactId: 'source-fact-income',
            factType: 'INCOME',
            label: 'Employment income',
            candidateValue: 125000,
            currency: 'USD',
            effectiveDate: '2026-10-01',
            sourceLocation: 'line 1',
            confidence: 0.98,
            documentVersionId: document.documentVersionId,
          },
        ],
      });
    if (
      path === 'v1/financial-profile/from-processing/job-processing-resume' &&
      method === 'POST'
    ) {
      expect(body).toEqual(
        expect.objectContaining({
          operationId: expect.stringMatching(/^[0-9a-f]{32}$/),
          sourceFactIds: ['source-fact-income'],
        }),
      );
      return Promise.resolve({
        profile: promotedProfile,
        confirmed: 1,
      });
    }
    return Promise.reject(new Error('unexpected product endpoint'));
  });

  mount(api, [
    'evidence.document.read',
    'job-management.job.read',
    'financial-profile.profile.read',
    'financial-profile.profile.update',
  ]);
  const user = userEvent.setup();

  await openCompletedProcessingResult(user);
  const drawer = await screen.findByRole('dialog', { name: 'Processing result' });

  expect(drawer).toHaveTextContent('Resume review from this completed processing result');
  const checkbox = within(drawer).getByRole('checkbox', {
    name: 'Add Employment income to financial information',
  });
  await user.click(checkbox);
  await user.click(within(drawer).getByRole('button', { name: 'Add selected information' }));

  expect(
    await within(drawer).findByText('1 item added to Financial information'),
  ).toBeVisible();
  expect(request).toHaveBeenCalledWith(
    'v1/financial-profile/from-processing/job-processing-resume',
    'POST',
    expect.objectContaining({ sourceFactIds: ['source-fact-income'] }),
    undefined,
  );
  expect(
    within(drawer).queryByRole('checkbox', {
      name: 'Add Employment income to financial information',
    }),
  ).not.toBeInTheDocument();
  expect(drawer).toHaveTextContent('ALREADY TRUSTED');
  expect(
    within(drawer).getByRole('link', { name: 'View trusted Financial information' }),
  ).toHaveAttribute('href', '/finances?fact=financial-fact-income');
  expect(drawer).toHaveTextContent('All supported items from this run are already trusted');
});

it('keeps recovered review read-only when Financial Profile update is not authorized', async () => {
  const api = new AccessApi(session);
  vi.spyOn(api, 'product').mockImplementation((path) => {
    if (path === 'v1/evidence')
      return Promise.resolve({
        items: [
          {
            documentId: 'document-readonly-resume',
            documentVersionId: 'document-version-readonly-resume',
            evidenceId: 'evidence-readonly-resume',
            version: 1,
            sourceName: 'Customer upload',
            originalFileName: 'readonly-resume.txt',
            contentType: 'text/plain',
            contentLength: 32,
            contentSha256: '5'.repeat(64),
            receivedAt: '2026-10-16T08:00:00Z',
            createdAt: '2026-10-16T08:00:00Z',
          },
        ],
        dataDurability: 'Temporary data',
      });
    if (path === 'v1/financial-profile')
      return Promise.resolve({
        financialProfileId: 'financial-profile-reference-customer',
        revision: 0,
        updatedAt: null,
        dataDurability: 'Temporary data',
        facts: [],
      });
    if (path === 'v1/processing-activity')
      return Promise.resolve({
        dataDurability: 'Temporary data',
        items: [
          {
            jobId: 'job-processing-readonly-resume',
            documentVersionId: 'document-version-readonly-resume',
            state: 'Completed',
            attempt: 1,
            updatedAt: '2026-10-16T09:00:00Z',
          },
        ],
      });
    if (path === 'v1/processing-result/job-processing-readonly-resume')
      return Promise.resolve({
        jobId: 'job-processing-readonly-resume',
        documentVersionId: 'document-version-readonly-resume',
        state: 'Completed',
        updatedAt: '2026-10-16T09:00:00Z',
        dataDurability: 'Temporary data',
        facts: [
          {
            sourceFactId: 'source-fact-income',
            factType: 'INCOME',
            label: 'Employment income',
            candidateValue: 125000,
            currency: 'USD',
            effectiveDate: '2026-10-01',
            sourceLocation: 'line 1',
            confidence: 0.98,
            documentVersionId: 'document-version-readonly-resume',
          },
        ],
      });
    return Promise.reject(new Error('unexpected product endpoint'));
  });

  mount(api, [
    'evidence.document.read',
    'job-management.job.read',
    'financial-profile.profile.read',
  ]);
  const user = userEvent.setup();

  await openCompletedProcessingResult(user);
  const drawer = await screen.findByRole('dialog', { name: 'Processing result' });

  expect(drawer).toHaveTextContent('This recovered view is read-only');
  expect(within(drawer).queryByRole('checkbox')).not.toBeInTheDocument();
  expect(
    within(drawer).queryByRole('button', { name: 'Add selected information' }),
  ).not.toBeInTheDocument();
});


it('reconciles mixed recovered candidates and submits only still-untrusted source facts', async () => {
  const api = new AccessApi(session);
  const documentVersionId = 'document-version-mixed-review';
  const trustedIncome = {
    financialFactId: 'financial-fact-trusted-income',
    factType: 'INCOME',
    label: 'Employment income',
    value: 125000,
    currency: 'USD',
    effectiveDate: '2026-10-01',
    revision: 1,
    source: {
      sourceName: 'mixed-review.txt',
      documentVersion: documentVersionId,
      recordedAt: '2026-10-16T09:00:00Z',
      technicalSourceFactId: 'source-fact-income',
      technicalProvenanceId: 'provenance-income',
    },
  };
  const trustedExpense = {
    financialFactId: 'financial-fact-trusted-expense',
    factType: 'EXPENSE',
    label: 'Housing',
    value: 2500,
    currency: 'USD',
    effectiveDate: '2026-10-01',
    revision: 1,
    source: {
      sourceName: 'mixed-review.txt',
      documentVersion: documentVersionId,
      recordedAt: '2026-10-16T09:05:00Z',
      technicalSourceFactId: 'source-fact-expense',
      technicalProvenanceId: 'provenance-expense',
    },
  };
  vi.spyOn(api, 'product').mockImplementation((path, method, body) => {
    if (path === 'v1/evidence')
      return Promise.resolve({
        dataDurability: 'Temporary data',
        items: [
          {
            documentId: 'document-mixed-review',
            documentVersionId,
            evidenceId: 'evidence-mixed-review',
            version: 1,
            sourceName: 'Customer upload',
            originalFileName: 'mixed-review.txt',
            contentType: 'text/plain',
            contentLength: 64,
            contentSha256: '4'.repeat(64),
            receivedAt: '2026-10-16T08:00:00Z',
            createdAt: '2026-10-16T08:00:00Z',
          },
        ],
      });
    if (path === 'v1/financial-profile')
      return Promise.resolve({
        financialProfileId: 'financial-profile-reference-customer',
        revision: 1,
        updatedAt: '2026-10-16T09:00:00Z',
        dataDurability: 'Temporary data',
        facts: [trustedIncome],
      });
    if (path === 'v1/processing-activity')
      return Promise.resolve({
        dataDurability: 'Temporary data',
        items: [
          {
            jobId: 'job-processing-mixed-review',
            documentVersionId,
            state: 'Completed',
            attempt: 1,
            updatedAt: '2026-10-16T09:00:00Z',
          },
        ],
      });
    if (path === 'v1/processing-result/job-processing-mixed-review')
      return Promise.resolve({
        jobId: 'job-processing-mixed-review',
        documentVersionId,
        state: 'Completed',
        updatedAt: '2026-10-16T09:00:00Z',
        dataDurability: 'Temporary data',
        facts: [
          {
            sourceFactId: 'source-fact-income',
            factType: 'INCOME',
            label: 'Employment income',
            candidateValue: 125000,
            currency: 'USD',
            effectiveDate: '2026-10-01',
            sourceLocation: 'line 1',
            confidence: 0.98,
            documentVersionId,
          },
          {
            sourceFactId: 'source-fact-expense',
            factType: 'EXPENSE',
            label: 'Housing',
            candidateValue: 2500,
            currency: 'USD',
            effectiveDate: '2026-10-01',
            sourceLocation: 'line 2',
            confidence: 0.96,
            documentVersionId,
          },
        ],
      });
    if (
      path === 'v1/financial-profile/from-processing/job-processing-mixed-review' &&
      method === 'POST'
    ) {
      expect(body).toEqual(
        expect.objectContaining({
          sourceFactIds: ['source-fact-expense'],
        }),
      );
      return Promise.resolve({
        confirmed: 1,
        profile: {
          financialProfileId: 'financial-profile-reference-customer',
          revision: 2,
          updatedAt: '2026-10-16T09:05:00Z',
          dataDurability: 'Temporary data',
          facts: [trustedIncome, trustedExpense],
        },
      });
    }
    return Promise.reject(new Error('unexpected product endpoint'));
  });

  mount(api, [
    'evidence.document.read',
    'job-management.job.read',
    'financial-profile.profile.read',
    'financial-profile.profile.update',
  ]);
  const user = userEvent.setup();

  await openCompletedProcessingResult(user);
  const drawer = await screen.findByRole('dialog', { name: 'Processing result' });

  expect(drawer).toHaveTextContent('ALREADY TRUSTED');
  expect(
    within(drawer).getByRole('link', { name: 'View trusted Financial information' }),
  ).toHaveAttribute('href', '/finances?fact=financial-fact-trusted-income');
  expect(
    within(drawer).queryByRole('checkbox', {
      name: 'Add Employment income to financial information',
    }),
  ).not.toBeInTheDocument();

  const expense = within(drawer).getByRole('checkbox', {
    name: 'Add Housing to financial information',
  });
  await user.click(expense);
  await user.click(within(drawer).getByRole('button', { name: 'Add selected information' }));

  expect(
    await within(drawer).findByText('1 item added to Financial information'),
  ).toBeVisible();
  expect(
    within(drawer).getAllByRole('link', { name: 'View trusted Financial information' }),
  ).toHaveLength(2);
  expect(within(drawer).queryByRole('checkbox')).not.toBeInTheDocument();
});

it('shows an all-trusted recovered review without duplicate promotion controls after reload', async () => {
  const api = new AccessApi(session);
  const documentVersionId = 'document-version-all-trusted';
  const facts = [
    {
      financialFactId: 'financial-fact-all-income',
      factType: 'INCOME',
      label: 'Employment income',
      value: 125000,
      currency: 'USD',
      effectiveDate: '2026-10-01',
      revision: 1,
      source: {
        sourceName: 'all-trusted.txt',
        documentVersion: documentVersionId,
        recordedAt: '2026-10-16T09:00:00Z',
        technicalSourceFactId: 'source-fact-all-income',
        technicalProvenanceId: 'provenance-all-income',
      },
    },
    {
      financialFactId: 'financial-fact-all-expense',
      factType: 'EXPENSE',
      label: 'Housing',
      value: 2500,
      currency: 'USD',
      effectiveDate: '2026-10-01',
      revision: 1,
      source: {
        sourceName: 'all-trusted.txt',
        documentVersion: documentVersionId,
        recordedAt: '2026-10-16T09:01:00Z',
        technicalSourceFactId: 'source-fact-all-expense',
        technicalProvenanceId: 'provenance-all-expense',
      },
    },
  ];
  const request = vi.spyOn(api, 'product').mockImplementation((path) => {
    if (path === 'v1/evidence')
      return Promise.resolve({
        dataDurability: 'Temporary data',
        items: [
          {
            documentId: 'document-all-trusted',
            documentVersionId,
            evidenceId: 'evidence-all-trusted',
            version: 1,
            sourceName: 'Customer upload',
            originalFileName: 'all-trusted.txt',
            contentType: 'text/plain',
            contentLength: 64,
            contentSha256: '3'.repeat(64),
            receivedAt: '2026-10-16T08:00:00Z',
            createdAt: '2026-10-16T08:00:00Z',
          },
        ],
      });
    if (path === 'v1/financial-profile')
      return Promise.resolve({
        financialProfileId: 'financial-profile-reference-customer',
        revision: 2,
        updatedAt: '2026-10-16T09:01:00Z',
        dataDurability: 'Temporary data',
        facts,
      });
    if (path === 'v1/processing-activity')
      return Promise.resolve({
        dataDurability: 'Temporary data',
        items: [
          {
            jobId: 'job-processing-all-trusted',
            documentVersionId,
            state: 'Completed',
            attempt: 1,
            updatedAt: '2026-10-16T09:00:00Z',
          },
        ],
      });
    if (path === 'v1/processing-result/job-processing-all-trusted')
      return Promise.resolve({
        jobId: 'job-processing-all-trusted',
        documentVersionId,
        state: 'Completed',
        updatedAt: '2026-10-16T09:00:00Z',
        dataDurability: 'Temporary data',
        facts: [
          {
            sourceFactId: 'source-fact-all-income',
            factType: 'INCOME',
            label: 'Employment income',
            candidateValue: 125000,
            currency: 'USD',
            effectiveDate: '2026-10-01',
            sourceLocation: 'line 1',
            confidence: 0.98,
            documentVersionId,
          },
          {
            sourceFactId: 'source-fact-all-expense',
            factType: 'EXPENSE',
            label: 'Housing',
            candidateValue: 2500,
            currency: 'USD',
            effectiveDate: '2026-10-01',
            sourceLocation: 'line 2',
            confidence: 0.96,
            documentVersionId,
          },
        ],
      });
    return Promise.reject(new Error('unexpected product endpoint'));
  });

  mount(api, [
    'evidence.document.read',
    'job-management.job.read',
    'financial-profile.profile.read',
    'financial-profile.profile.update',
  ]);
  const user = userEvent.setup();

  await openCompletedProcessingResult(user);
  const drawer = await screen.findByRole('dialog', { name: 'Processing result' });

  expect(drawer).toHaveTextContent('All supported items from this run are already trusted');
  expect(within(drawer).getAllByText('ALREADY TRUSTED')).toHaveLength(2);
  expect(within(drawer).queryByRole('checkbox')).not.toBeInTheDocument();
  expect(
    within(drawer).queryByRole('button', { name: 'Add selected information' }),
  ).not.toBeInTheDocument();
  expect(
    request.mock.calls.some(([path]) =>
      path.startsWith('v1/financial-profile/from-processing/'),
    ),
  ).toBe(false);
});

it('keeps recovered review non-selectable when trusted Financial state is unavailable', async () => {
  const api = new AccessApi(session);
  const documentVersionId = 'document-version-profile-unavailable';
  vi.spyOn(api, 'product').mockImplementation((path) => {
    if (path === 'v1/evidence')
      return Promise.resolve({
        dataDurability: 'Temporary data',
        items: [
          {
            documentId: 'document-profile-unavailable',
            documentVersionId,
            evidenceId: 'evidence-profile-unavailable',
            version: 1,
            sourceName: 'Customer upload',
            originalFileName: 'profile-unavailable.txt',
            contentType: 'text/plain',
            contentLength: 64,
            contentSha256: '2'.repeat(64),
            receivedAt: '2026-10-16T08:00:00Z',
            createdAt: '2026-10-16T08:00:00Z',
          },
        ],
      });
    if (path === 'v1/financial-profile')
      return Promise.reject(new AccessApiError(503, 'FINANCIAL_PROFILE_OWNER_UNAVAILABLE'));
    if (path === 'v1/processing-activity')
      return Promise.resolve({
        dataDurability: 'Temporary data',
        items: [
          {
            jobId: 'job-processing-profile-unavailable',
            documentVersionId,
            state: 'Completed',
            attempt: 1,
            updatedAt: '2026-10-16T09:00:00Z',
          },
        ],
      });
    if (path === 'v1/processing-result/job-processing-profile-unavailable')
      return Promise.resolve({
        jobId: 'job-processing-profile-unavailable',
        documentVersionId,
        state: 'Completed',
        updatedAt: '2026-10-16T09:00:00Z',
        dataDurability: 'Temporary data',
        facts: [
          {
            sourceFactId: 'source-fact-profile-unavailable',
            factType: 'INCOME',
            label: 'Employment income',
            candidateValue: 125000,
            currency: 'USD',
            effectiveDate: '2026-10-01',
            sourceLocation: 'line 1',
            confidence: 0.98,
            documentVersionId,
          },
        ],
      });
    return Promise.reject(new Error('unexpected product endpoint'));
  });

  mount(api, [
    'evidence.document.read',
    'job-management.job.read',
    'financial-profile.profile.read',
    'financial-profile.profile.update',
  ]);
  const user = userEvent.setup();

  await openCompletedProcessingResult(user);
  const drawer = await screen.findByRole('dialog', { name: 'Processing result' });

  expect(
    await within(drawer).findByText('Trusted Financial information could not be reconciled'),
  ).toBeVisible();
  expect(within(drawer).queryByRole('checkbox')).not.toBeInTheDocument();
  expect(
    within(drawer).queryByRole('button', { name: 'Add selected information' }),
  ).not.toBeInTheDocument();
});


it('does not reconcile a source fact from a different document version as already trusted', async () => {
  const api = new AccessApi(session);
  const documentVersionId = 'document-version-current-review';
  vi.spyOn(api, 'product').mockImplementation((path) => {
    if (path === 'v1/evidence')
      return Promise.resolve({
        dataDurability: 'Temporary data',
        items: [
          {
            documentId: 'document-current-review',
            documentVersionId,
            evidenceId: 'evidence-current-review',
            version: 1,
            sourceName: 'Customer upload',
            originalFileName: 'current-review.txt',
            contentType: 'text/plain',
            contentLength: 64,
            contentSha256: '1'.repeat(64),
            receivedAt: '2026-10-16T08:00:00Z',
            createdAt: '2026-10-16T08:00:00Z',
          },
        ],
      });
    if (path === 'v1/financial-profile')
      return Promise.resolve({
        financialProfileId: 'financial-profile-reference-customer',
        revision: 1,
        updatedAt: '2026-10-16T09:00:00Z',
        dataDurability: 'Temporary data',
        facts: [
          {
            financialFactId: 'financial-fact-other-version',
            factType: 'INCOME',
            label: 'Employment income',
            value: 125000,
            currency: 'USD',
            effectiveDate: '2026-10-01',
            revision: 1,
            source: {
              sourceName: 'older-review.txt',
              documentVersion: 'document-version-other-review',
              recordedAt: '2026-10-15T09:00:00Z',
              technicalSourceFactId: 'source-fact-shared-id',
              technicalProvenanceId: 'provenance-other-version',
            },
          },
        ],
      });
    if (path === 'v1/processing-activity')
      return Promise.resolve({
        dataDurability: 'Temporary data',
        items: [
          {
            jobId: 'job-processing-current-review',
            documentVersionId,
            state: 'Completed',
            attempt: 1,
            updatedAt: '2026-10-16T09:00:00Z',
          },
        ],
      });
    if (path === 'v1/processing-result/job-processing-current-review')
      return Promise.resolve({
        jobId: 'job-processing-current-review',
        documentVersionId,
        state: 'Completed',
        updatedAt: '2026-10-16T09:00:00Z',
        dataDurability: 'Temporary data',
        facts: [
          {
            sourceFactId: 'source-fact-shared-id',
            factType: 'INCOME',
            label: 'Employment income',
            candidateValue: 125000,
            currency: 'USD',
            effectiveDate: '2026-10-01',
            sourceLocation: 'line 1',
            confidence: 0.98,
            documentVersionId,
          },
        ],
      });
    return Promise.reject(new Error('unexpected product endpoint'));
  });

  mount(api, [
    'evidence.document.read',
    'job-management.job.read',
    'financial-profile.profile.read',
    'financial-profile.profile.update',
  ]);
  const user = userEvent.setup();

  await openCompletedProcessingResult(user);
  const drawer = await screen.findByRole('dialog', { name: 'Processing result' });

  expect(drawer).not.toHaveTextContent('ALREADY TRUSTED');
  expect(
    within(drawer).getByRole('checkbox', {
      name: 'Add Employment income to financial information',
    }),
  ).toBeEnabled();
});

it.each(['text/plain', 'image/png', 'image/jpeg', 'application/pdf'])('requires explicit review of a Loan amount from %s before updating authoritative Financial information', async (contentType) => {
  const suffix = contentType === 'image/png' ? 'png' : contentType === 'image/jpeg' ? 'jpg' : contentType === 'application/pdf' ? 'pdf' : 'txt';
  const label = contentType === 'text/plain' ? 'Car loan amount stated on document' : 'Outstanding loan balance stated on document';
  const api = new AccessApi(session);
  const item = {
    documentId: 'document-loan-003',
    documentVersionId: 'document-version-loan-003',
    evidenceId: 'evidence-loan-003',
    version: 1,
    sourceName: 'Customer upload',
    originalFileName: `loan-statement.${suffix}`,
    contentType,
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
        sourceFactId: 'source-fact-loan-001',
        customerId: 'reference-customer',
        factType: 'LOAN',
        label: label,
        candidateValue: 400000,
        currency: 'USD',
        effectiveDate: '2026-10-01',
        sourceLocation: 'line 1',
        confidence: 0.98,
        evidenceId: item.evidenceId,
        documentVersionId: item.documentVersionId,
        extractionVersion: contentType === 'text/plain' ? 'fixture-extractor/1.0.0' : `loan-statement-${suffix === 'jpg' ? 'jpeg' : suffix}-ocr/1.0.0`,
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
          financialFactId: 'financial-fact-loan-001',
          factType: 'LOAN',
          label: label,
          value: 400000,
          currency: 'USD',
          effectiveDate: '2026-10-01',
          revision: 1,
          source: {
            sourceName: item.originalFileName,
            documentVersion: item.documentVersionId,
            recordedAt: '2026-10-14T00:00:02Z',
            technicalSourceFactId: 'source-fact-loan-001',
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
  await screen.findByText(item.originalFileName);
  await user.click(
    screen.getByRole('button', { name: `View document details for ${item.originalFileName}` }),
  );
  const provenance = screen.getByRole('dialog', { name: 'Document details' });
  await user.click(within(provenance).getByRole('button', { name: 'Extract details' }));
  const review = await screen.findByRole('dialog', { name: 'Extract and review details' });
  expect(review).toHaveTextContent('Review before adding anything to your financial information');
  expect(review).toHaveTextContent('Your financial information is not changed by this step');
  await user.click(within(review).getByRole('button', { name: 'Extract details' }));
  expect(await screen.findByText(label)).toBeVisible();
  expect(review).toHaveTextContent('$400,000.00');
  if (contentType !== 'text/plain') {
    expect(review).toHaveTextContent('Recognized as a loan statement');
    expect(review).toHaveTextContent("It does not establish your current debt");
    expect(review).not.toHaveTextContent('Recognized as a salary slip');
  }
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
      path.includes('v1/financial-profile/from-processing/'),
    ),
  ).toHaveLength(0);

  await user.click(
    within(review).getByRole('checkbox', {
      name: `Add ${label} to financial information`,
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
      sourceFactIds: ['source-fact-loan-001'],
    },
    undefined,
  );
});


it.each(['text/csv', 'application/vnd.ms-excel', ''])('uploads CSV with browser media type %s using the exact CSV payload', async (mediaType) => {
  const api = new AccessApi(session);
  const csv = 'Category,Description,Amount,Currency,Date,Source\r\nINCOME,Salary,125000,INR,2026-10-01,Salary statement\r\n';
  const submit = vi.spyOn(api, 'product').mockImplementation((_, method) =>
    Promise.resolve(method === 'POST' ? {
      operationId: 'a'.repeat(32), documentId: 'csv-document', documentVersionId: 'csv-version',
      evidenceId: 'csv-evidence', version: 1, originalFileName: 'financial.csv', contentType: 'text/csv',
      contentLength: csv.length, contentSha256: 'b'.repeat(64), acceptedAt: '2026-10-17T00:00:00Z',
      dataDurability: 'Temporary data',
    } : { items: [], dataDurability: 'Temporary data' }),
  );
  vi.spyOn(crypto, 'randomUUID').mockReturnValue('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa');
  mount(api);
  const template = screen.getByRole('link', { name: 'financial information template' });
  expect(template).toHaveAttribute('download', 'monergy-financial-information.csv');
  expect(decodeURIComponent(template.getAttribute('href') ?? '')).toContain('Category,Description,Amount,Currency,Date,Source');
  const user = userEvent.setup();
  const input = document.querySelector<HTMLInputElement>('input[type="file"]');
  if (!input) throw new Error('Document upload input is missing.');
  await user.upload(input, new File([csv], 'financial.csv', { type: mediaType }));
  expect(screen.getByRole('dialog', { name: 'Review document upload' })).toHaveTextContent('Financial information CSV');
  await user.click(screen.getByRole('button', { name: 'Upload document' }));
  await waitFor(() => {
    expect(submit.mock.calls.some((call) => call[1] === 'POST')).toBe(true);
  });
  const write = submit.mock.calls.find((call) => call[1] === 'POST');
  expect(write?.[0]).toBe('v1/evidence');
  expect(write?.[2]).toMatchObject({ contentType: 'text/csv', originalFileName: 'financial.csv', contentBase64: btoa(csv) });
  expect(await screen.findByText('Your document was uploaded.')).toBeVisible();
  // The upload drawer closes asynchronously; let it release its modal state before the next mount.
  await waitFor(() => {
    expect(screen.queryByRole('dialog', { name: 'Review document upload' })).not.toBeInTheDocument();
  });
});

it.each([true, false])('enables CSV extraction only with current processing access: %s', async (allowed) => {
  const api = new AccessApi(session);
  const request = vi.spyOn(api, 'product').mockResolvedValue({ items: [{
    documentId: 'csv-document', documentVersionId: 'csv-version', evidenceId: 'csv-evidence', version: 1,
    sourceName: 'Customer upload', originalFileName: 'financial.csv', contentType: 'text/csv', contentLength: 80,
    contentSha256: 'b'.repeat(64), receivedAt: '2026-10-17T00:00:00Z', createdAt: '2026-10-17T00:00:00Z',
  }], dataDurability: 'Temporary data' });
  mount(api, allowed ? ['evidence.document.read', 'document-intelligence.document.process'] : ['evidence.document.read']);
  const user = userEvent.setup();
  await screen.findByText('financial.csv');
  await user.click(screen.getByRole('button', { name: 'View document details for financial.csv' }));
  const details = screen.getByRole('dialog', { name: 'Document details' });
  const extract = within(details).getByRole('button', { name: 'Extract details' });
  if (allowed) {
    expect(extract).toBeEnabled();
    await user.click(extract);
    const review = screen.getByRole('dialog', { name: 'Extract and review details' });
    expect(within(review).getByRole('button', { name: 'Extract details' })).toBeEnabled();
  } else {
    expect(extract).toBeDisabled();
  }
  expect(request.mock.calls.some((call) => call[1] === 'POST')).toBe(false);
});

it.each([
  [422, 'EVIDENCE_SCAN_REJECTED', 'This file failed the security check and was not accepted. Close this review and choose another file.'],
  [503, 'EVIDENCE_SCAN_UNAVAILABLE', 'The file security check is unavailable. This file has not been accepted. Try again when the check is available.'],
])('shows the precise file-check outcome for %s without confirming an upload', async (status, code, message) => {
  const api = new AccessApi(session);
  const submit = vi.spyOn(api, 'product').mockImplementation((_path, method) => method === 'POST'
    ? Promise.reject(new AccessApiError(status, code))
    : Promise.resolve({ items: [], dataDurability: 'Temporary data' }));
  mount(api); const user = userEvent.setup(); await screen.findByText('No documents yet.');
  const input = document.querySelector('input[type="file"]'); expect(input).toBeInstanceOf(HTMLInputElement);
  await user.upload(input as HTMLInputElement, new File(['account evidence'], 'statement.txt', { type: 'text/plain' }));
  const drawer = await screen.findByRole('dialog', { name: 'Review document upload' });
  await user.click(within(drawer).getByRole('button', { name: 'Upload document' }));
  expect(await within(drawer).findByText(message)).toBeVisible();
  expect(screen.queryByText('Your document was uploaded.')).not.toBeInTheDocument();
  expect(submit.mock.calls.filter(([, method]) => method === 'POST')).toHaveLength(1);
});

it.each([
  ['processing.document.unclassified', "We couldn't recognize a supported document."],
  ['processing.ocr.low-confidence', 'Some document details were unclear.'],
  ['processing.loan-statement.incomplete', 'We need one clear outstanding balance'],
  ['processing.insurance-policy.incomplete', 'We need one clear sum assured, one premium amount'],
  ['processing.salary-slip.incomplete', 'We need one clear net pay amount'],
  ['processing.ocr.unavailable', 'Image text extraction is temporarily unavailable.'],
  ['processing.pdf.unsupported', 'Use a readable, unlocked, single-page salary-slip, loan-statement or insurance-policy PDF'],
  ['processing.jpeg.unsupported', 'Use a clear, upright JPEG image.'],
])('explains document extraction failure %s without offering financial adoption', async (code, message) => {
  const api = new AccessApi(session);
  const pdf = code === 'processing.pdf.unsupported';
  const jpeg = code === 'processing.jpeg.unsupported';
  const item = { documentId: 'png-document', documentVersionId: 'png-version', evidenceId: 'png-evidence', version: 1,
    sourceName: 'Customer upload', originalFileName: jpeg ? 'salary-slip.jpg' : pdf ? 'salary-slip.pdf' : 'salary-slip.png', contentType: jpeg ? 'image/jpeg' : pdf ? 'application/pdf' : 'image/png', contentLength: 100,
    contentSha256: 'b'.repeat(64), receivedAt: '2026-10-18T00:00:00Z', createdAt: '2026-10-18T00:00:00Z' };
  const request = vi.spyOn(api, 'product').mockImplementation((path, method) => {
    if (method === 'POST') return Promise.resolve({ job: { jobId: 'job-processing-png', state: 'Failed', failureCode: code },
      processing: { state: 'Failed', failureCode: code }, facts: [], processingMode: 'Document extraction' });
    return Promise.resolve({ items: path === 'v1/evidence' ? [item] : [], dataDurability: 'Temporary data' });
  });
  mount(api, ['evidence.document.read', 'document-intelligence.document.process', 'financial-profile.profile.update']);
  const user = userEvent.setup(); await screen.findByText(item.originalFileName);
  await user.click(screen.getByRole('button', { name: `View document details for ${item.originalFileName}` }));
  await user.click(within(screen.getByRole('dialog', { name: 'Document details' })).getByRole('button', { name: 'Extract details' }));
  const review = screen.getByRole('dialog', { name: 'Extract and review details' });
  await user.click(within(review).getByRole('button', { name: 'Extract details' }));
  expect(await within(review).findByText((text) => text.startsWith(message))).toBeVisible();
  expect(within(review).queryByRole('checkbox')).not.toBeInTheDocument();
  expect(request.mock.calls.filter(([path]) => path.includes('financial-profile/from-processing'))).toHaveLength(0);
});

it.each([financialXlsxType, 'application/zip', 'application/octet-stream', ''])('uploads XLSX with browser media type %s using the original workbook bytes', async (mediaType) => {
  const api = new AccessApi(session);
  // Fixed repository fixture; no user-controlled path. FileUrl also avoids Vite asset-URL rewriting.
  // eslint-disable-next-line security/detect-non-literal-fs-filename
  const bytes = Uint8Array.from(readFileSync(new FileUrl('../public/templates/monergy-financial-information.xlsx', import.meta.url)));
  const submit = vi.spyOn(api, 'product').mockImplementation((_, method) =>
    Promise.resolve(method === 'POST' ? {
      operationId: 'a'.repeat(32), documentId: 'xlsx-document', documentVersionId: 'xlsx-version',
      evidenceId: 'xlsx-evidence', version: 1, originalFileName: 'financial.xlsx', contentType: financialXlsxType,
      contentLength: bytes.length, contentSha256: 'b'.repeat(64), acceptedAt: '2026-10-17T00:00:00Z',
      dataDurability: 'Temporary data',
    } : { items: [], dataDurability: 'Temporary data' }),
  );
  vi.spyOn(crypto, 'randomUUID').mockReturnValue('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa');
  mount(api);
  const template = screen.getByRole('link', { name: 'Excel financial information template' });
  expect(template).toHaveAttribute('download', 'monergy-financial-information.xlsx');
  expect(template).toHaveAttribute('href', '/templates/monergy-financial-information.xlsx');
  const user = userEvent.setup();
  const input = document.querySelector<HTMLInputElement>('input[type="file"]');
  if (!input) throw new Error('Document upload input is missing.');
  await user.upload(input, new File([bytes], 'financial.xlsx', { type: mediaType }));
  expect(screen.getByRole('dialog', { name: 'Review document upload' })).toHaveTextContent('Financial information Excel');
  await user.click(screen.getByRole('button', { name: 'Upload document' }));
  await waitFor(() => {
    expect(submit.mock.calls.some((call) => call[1] === 'POST')).toBe(true);
  });
  const write = submit.mock.calls.find((call) => call[1] === 'POST');
  expect(write?.[0]).toBe('v1/evidence');
  expect(write?.[2]).toMatchObject({ contentType: financialXlsxType, originalFileName: 'financial.xlsx', contentBase64: btoa(String.fromCharCode(...bytes)) });
  expect(await screen.findByText('Your document was uploaded.')).toBeVisible();
  // The upload drawer closes asynchronously; let it release its modal state before the next mount.
  await waitFor(() => {
    expect(screen.queryByRole('dialog', { name: 'Review document upload' })).not.toBeInTheDocument();
  });
});


it.each([true, false])('enables XLSX extraction only with current processing access: %s', async (allowed) => {
  const api = new AccessApi(session);
  const request = vi.spyOn(api, 'product').mockResolvedValue({ items: [{
    documentId: 'xlsx-document', documentVersionId: 'xlsx-version', evidenceId: 'xlsx-evidence', version: 1,
    sourceName: 'Customer upload', originalFileName: 'financial.xlsx', contentType: financialXlsxType, contentLength: 80,
    contentSha256: 'b'.repeat(64), receivedAt: '2026-10-17T00:00:00Z', createdAt: '2026-10-17T00:00:00Z',
  }], dataDurability: 'Temporary data' });
  mount(api, allowed ? ['evidence.document.read', 'document-intelligence.document.process'] : ['evidence.document.read']);
  const user = userEvent.setup();
  await screen.findByText('financial.xlsx');
  await user.click(screen.getByRole('button', { name: 'View document details for financial.xlsx' }));
  const details = screen.getByRole('dialog', { name: 'Document details' });
  const extract = within(details).getByRole('button', { name: 'Extract details' });
  if (allowed) {
    expect(extract).toBeEnabled();
    await user.click(extract);
    const review = screen.getByRole('dialog', { name: 'Extract and review details' });
    expect(within(review).getByRole('button', { name: 'Extract details' })).toBeEnabled();
  } else {
    expect(extract).toBeDisabled();
  }
  expect(request.mock.calls.some((call) => call[1] === 'POST')).toBe(false);
});


it.each(['png', 'jpeg', 'pdf'])('reviews both insurance figures from %s and adopts only the selected premium', async (format) => {
  const api = new AccessApi(session);
  const item = { documentId: 'policy-document', documentVersionId: 'policy-version', evidenceId: 'policy-evidence', version: 1,
    sourceName: 'Customer upload', originalFileName: 'insurance-policy.' + (format === 'jpeg' ? 'jpg' : format),
    contentType: format === 'pdf' ? 'application/pdf' : 'image/' + format, contentLength: 1234,
    contentSha256: 'e'.repeat(64), receivedAt: '2026-10-18T00:00:00Z', createdAt: '2026-10-18T00:00:00Z' };
  const premiumLabel = 'Premium stated on policy (frequency not established)';
  const source = { customerId: 'reference-customer', factType: 'INSURANCE', currency: 'USD', effectiveDate: '2026-10-01',
    sourceLocation: 'Insurance policy OCR: page 1', confidence: 0.98, evidenceId: item.evidenceId,
    documentVersionId: item.documentVersionId, extractionVersion: `insurance-policy-${format}-ocr/1.0.0`, validationVersion: 'canonical-source-fact/1.0.0' };
  const result = { job: { jobId: `job-processing-${'c'.repeat(32)}`, jobName: 'document-processing', ownerService: 'Document Intelligence Service',
      payloadReference: item.documentVersionId, customerId: 'reference-customer', state: 'Completed', attempt: 1, failureCode: null,
      retryable: false, updatedAt: '2026-10-18T00:00:01Z' },
    processing: { processingId: `processing-${'c'.repeat(32)}`, documentVersionId: item.documentVersionId, state: 'Completed',
      failureCode: null, retryable: false, updatedAt: '2026-10-18T00:00:01Z' },
    facts: [{ ...source, sourceFactId: 'policy-sum', label: 'Sum assured stated on policy', candidateValue: 400000 },
      { ...source, sourceFactId: 'policy-premium', label: premiumLabel, candidateValue: 2500 }],
    dataDurability: 'Temporary data', processingMode: 'Document extraction' };
  const request = vi.spyOn(api, 'product').mockImplementation((path, method) => {
    if (path === 'v1/evidence' && method === 'GET') return Promise.resolve({ items: [item], dataDurability: 'Temporary data' });
    if (path.endsWith('/processing') && method === 'POST') return Promise.resolve(result);
    if (path.includes('v1/financial-profile/from-processing/') && method === 'POST') return Promise.resolve({ confirmed: 1,
      profile: { financialProfileId: 'financial-profile-reference-customer', revision: 1, updatedAt: '2026-10-18T00:00:02Z',
        dataDurability: 'Temporary data', facts: [{ financialFactId: 'policy-trusted-premium', factType: 'INSURANCE', label: premiumLabel,
          value: 2500, currency: 'USD', effectiveDate: '2026-10-01', revision: 1,
          source: { sourceName: item.originalFileName, documentVersion: item.documentVersionId, recordedAt: '2026-10-18T00:00:02Z', technicalSourceFactId: 'policy-premium' } }] } });
    return Promise.reject(new Error('Unexpected policy endpoint: ' + path));
  });
  vi.spyOn(crypto, 'randomUUID').mockReturnValueOnce('cccccccc-cccc-cccc-cccc-cccccccccccc').mockReturnValueOnce('dddddddd-dddd-dddd-dddd-dddddddddddd');
  mount(api, ['evidence.document.read', 'document-intelligence.document.process', 'financial-profile.profile.update']);
  const user = userEvent.setup(); await screen.findByText(item.originalFileName);
  await user.click(screen.getByRole('button', { name: `View document details for ${item.originalFileName}` }));
  await user.click(within(screen.getByRole('dialog', { name: 'Document details' })).getByRole('button', { name: 'Extract details' }));
  const review = await screen.findByRole('dialog', { name: 'Extract and review details' });
  await user.click(within(review).getByRole('button', { name: 'Extract details' }));
  expect(await within(review).findByText('Recognized as an insurance policy')).toBeVisible();
  expect(review).toHaveTextContent('does not verify active cover, cash value, authenticity or premium frequency');
  expect(review).toHaveTextContent('$400,000.00'); expect(review).toHaveTextContent('$2,500.00');
  expect(review).not.toHaveTextContent('Recognized as a salary slip'); expect(review).not.toHaveTextContent('Recognized as a loan statement');
  const add = within(review).getByRole('button', { name: 'Add selected information' }); expect(add).toBeDisabled();
  expect(request.mock.calls.filter(([path]) => path.includes('/from-processing/'))).toHaveLength(0);
  await user.click(within(review).getByRole('checkbox', { name: `Add ${premiumLabel} to financial information` }));
  expect(within(review).getByRole('checkbox', { name: 'Add Sum assured stated on policy to financial information' })).not.toBeChecked();
  await user.click(add);
  expect(await screen.findByText('Your financial information was updated')).toBeVisible();
  expect(review).toHaveTextContent('1 reviewed item confirmed');
  expect(request).toHaveBeenCalledWith(`v1/financial-profile/from-processing/${result.job.jobId}`, 'POST',
    { operationId: 'd'.repeat(32), sourceFactIds: ['policy-premium'] }, undefined);
});
