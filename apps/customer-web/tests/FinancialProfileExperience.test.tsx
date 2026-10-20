import { FoundationProvider } from '@monergy/ui-foundation';
import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, it, vi } from 'vitest';
import { AccessApi } from '../src/access/accessApi';
import { Context } from '../src/access/workspaceSession';
import FinancialProfileExperience from '../src/product/FinancialProfileExperience';

const session = {
  tenantId: 'T001',
  actorId: 'A100',
  authenticationContextId: 'customer-session',
  subjectVersion: 2,
  expiresAt: '2027-01-01T00:00:00Z',
};

function mount(api: AccessApi, capabilities = ['financial-profile.profile.read']) {
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
        <FinancialProfileExperience />
      </Context>
    </FoundationProvider>,
  );
}

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  window.history.replaceState(null, '', '/');
});

it('shows trusted financial information with source history and hides raw ids by default', async () => {
  const api = new AccessApi(session);
  vi.spyOn(api, 'product').mockResolvedValue({
    financialProfileId: 'financial-profile-reference-customer',
    revision: 2,
    updatedAt: '2026-10-15T12:00:00Z',
    dataDurability: 'Temporary data',
    facts: [
      {
        financialFactId: 'ff-income',
        factType: 'INCOME',
        label: 'Employment income',
        value: 125000,
        currency: 'USD',
        effectiveDate: '2026-10-01',
        revision: 2,
        source: {
          sourceName: 'customer-statement.txt',
          documentVersion: 'document-version-001',
          recordedAt: '2026-10-15T12:00:00Z',
          technicalSourceFactId: 'source-fact-001',
          technicalProvenanceId: 'provenance-base-001',
        },
      },
    ],
  });

  mount(api);

  expect(await screen.findByText('Employment income')).toBeVisible();
  expect(screen.getByText('$125,000.00')).toBeVisible();
  expect(screen.getByText('Source: customer-statement.txt')).toBeVisible();
  expect(screen.getByText('Current revision')).toBeVisible();
  expect(screen.queryByText('ff-income')).not.toBeInTheDocument();

  await userEvent.setup().click(screen.getByText('Technical details'));
  expect(screen.getByText('ff-income')).toBeVisible();
  expect(screen.getByText('document-version-001')).toBeVisible();
  expect(screen.getByText('source-fact-001')).toBeVisible();
});

it('opens Passport category deep links and switches between supported financial categories', async () => {
  window.history.replaceState(null, '', '/finances?type=EXPENSE');
  const api = new AccessApi(session);
  vi.spyOn(api, 'product').mockResolvedValue({
    financialProfileId: 'financial-profile-reference-customer',
    revision: 2,
    updatedAt: '2026-10-15T12:00:00Z',
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
          sourceName: 'income-statement.txt',
          documentVersion: 'document-version-001',
          recordedAt: '2026-10-15T12:00:00Z',
          technicalSourceFactId: 'source-fact-income',
          technicalProvenanceId: 'provenance-income',
        },
      },
      {
        financialFactId: 'ff-expense',
        factType: 'EXPENSE',
        label: 'Rent',
        value: 2500,
        currency: 'USD',
        effectiveDate: '2026-10-01',
        revision: 1,
        source: {
          sourceName: 'expense-statement.txt',
          documentVersion: 'document-version-002',
          recordedAt: '2026-10-14T12:00:00Z',
          technicalSourceFactId: 'source-fact-expense',
          technicalProvenanceId: 'provenance-expense',
        },
      },
    ],
  });

  mount(api);

  expect(await screen.findByText('Rent')).toBeVisible();
  expect(screen.queryByText('Employment income')).not.toBeInTheDocument();

  const user = userEvent.setup();
  await user.click(screen.getByRole('button', { name: 'Income' }));
  expect(await screen.findByText('Employment income')).toBeVisible();
  expect(screen.queryByText('Rent')).not.toBeInTheDocument();
  expect(window.location.pathname + window.location.search).toBe('/finances?type=INCOME');

  await user.click(screen.getByRole('button', { name: 'All' }));
  expect(screen.getByText('Employment income')).toBeVisible();
  expect(screen.getByText('Rent')).toBeVisible();
  expect(window.location.pathname + window.location.search).toBe('/finances');
});

it('shows a truthful empty state for a Passport category with no trusted items', async () => {
  window.history.replaceState(null, '', '/finances?type=EXPENSE');
  const api = new AccessApi(session);
  vi.spyOn(api, 'product').mockResolvedValue({
    financialProfileId: 'financial-profile-reference-customer',
    revision: 1,
    updatedAt: '2026-10-15T12:00:00Z',
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
          sourceName: 'income-statement.txt',
          documentVersion: 'document-version-001',
          recordedAt: '2026-10-15T12:00:00Z',
          technicalSourceFactId: 'source-fact-income',
          technicalProvenanceId: 'provenance-income',
        },
      },
    ],
  });

  mount(api);

  expect(
    await screen.findByRole('heading', { name: 'No trusted expense information yet.' }),
  ).toBeVisible();
  expect(screen.queryByText('Employment income')).not.toBeInTheDocument();
  expect(screen.getByRole('link', { name: 'Review documents' })).toHaveAttribute('href', '/evidence');
  expect(screen.getByRole('button', { name: 'Show all financial information' })).toBeVisible();
});

it('opens a bounded trusted-fact drill-down with revision and source history', async () => {
  const api = new AccessApi(session);
  const current = {
    financialFactId: 'ff-income',
    factType: 'INCOME',
    label: 'Employment income',
    value: 125000,
    currency: 'USD',
    effectiveDate: '2026-10-01',
    revision: 2,
    source: {
      sourceName: 'income-current.txt',
      documentVersion: 'document-version-002',
      recordedAt: '2026-10-15T12:00:00Z',
      technicalSourceFactId: 'source-fact-002',
      technicalProvenanceId: 'provenance-002',
    },
  };
  const revisionOne = {
    ...current,
    value: 120000,
    revision: 1,
    source: {
      sourceName: 'income-original.txt',
      documentVersion: 'document-version-001',
      recordedAt: '2026-10-14T12:00:00Z',
      technicalSourceFactId: 'source-fact-001',
      technicalProvenanceId: 'provenance-001',
    },
  };

  vi.spyOn(api, 'product').mockImplementation((path) => {
    if (path === 'v1/financial-profile')
      return Promise.resolve({
        financialProfileId: 'financial-profile-reference-customer',
        revision: 2,
        updatedAt: current.source.recordedAt,
        dataDurability: 'Temporary data',
        facts: [current],
      });
    if (path === 'v1/financial-profile/facts/ff-income')
      return Promise.resolve({ current, history: [current, revisionOne] });
    return Promise.reject(new Error('unexpected product endpoint'));
  });

  mount(api);
  const user = userEvent.setup();
  await user.click(await screen.findByRole('button', { name: 'View source & history' }));

  const drawer = await screen.findByRole('dialog', { name: 'Financial information details' });
  expect(drawer).toHaveTextContent('Employment income');
  expect(drawer).toHaveTextContent('$125,000.00');
  expect(drawer).toHaveTextContent('Revision 2');
  expect(drawer).toHaveTextContent('Revision 1');
  expect(drawer).toHaveTextContent('$120,000.00');
  expect(drawer).toHaveTextContent('income-current.txt');
  expect(drawer).toHaveTextContent('income-original.txt');

  const sourceLinks = within(drawer).getAllByRole('link', { name: 'View this source document' });
  expect(sourceLinks[0]).toHaveAttribute(
    'href',
    '/evidence?version=document-version-002',
  );
  expect(sourceLinks[1]).toHaveAttribute(
    'href',
    '/evidence?version=document-version-001',
  );

  await user.click(within(drawer).getByText('Technical details'));
  expect(drawer).toHaveTextContent('provenance-002');
  expect(drawer).toHaveTextContent('source-fact-001');
  expect(drawer).toHaveTextContent('document-version-001');
});

it('opens a Passport deep link directly to the requested trusted fact', async () => {
  window.history.replaceState(null, '', '/finances?fact=ff-income');
  const api = new AccessApi(session);
  vi.spyOn(api, 'product').mockImplementation((path) => {
    const fact = {
      financialFactId: 'ff-income',
      factType: 'INCOME',
      label: 'Employment income',
      value: 125000,
      currency: 'USD',
      effectiveDate: '2026-10-01',
      revision: 1,
      source: {
        sourceName: 'income-statement.txt',
        documentVersion: 'document-version-001',
        recordedAt: '2026-10-15T12:00:00Z',
        technicalSourceFactId: 'source-fact-001',
        technicalProvenanceId: 'provenance-001',
      },
    };
    if (path === 'v1/financial-profile')
      return Promise.resolve({
        financialProfileId: 'financial-profile-reference-customer',
        revision: 1,
        updatedAt: fact.source.recordedAt,
        dataDurability: 'Temporary data',
        facts: [fact],
      });
    if (path === 'v1/financial-profile/facts/ff-income')
      return Promise.resolve({ current: fact, history: [fact] });
    return Promise.reject(new Error('unexpected product endpoint'));
  });

  mount(api);

  const drawer = await screen.findByRole('dialog', { name: 'Financial information details' });
  expect(await within(drawer).findByText('Employment income', { selector: 'h2' })).toBeVisible();
  expect(drawer).toHaveTextContent('1 revision');
});

it('shows explicitly reviewed Loan facts with exact source provenance, not invented loan totals', async () => {
  window.history.replaceState(null, '', '/finances?type=LOAN');
  const api = new AccessApi(session);
  vi.spyOn(api, 'product').mockResolvedValue({
    financialProfileId: 'financial-profile-reference-customer',
    revision: 3,
    updatedAt: '2026-10-17T06:00:00Z',
    dataDurability: 'Temporary data',
    facts: [{
      financialFactId: 'ff-loan-source',
      factType: 'LOAN',
      label: 'Car loan principal stated in document',
      value: 400000,
      currency: 'INR',
      effectiveDate: '2026-10-16',
      revision: 1,
      source: {
        sourceName: 'loan-statement.txt',
        documentVersion: 'document-version-loan',
        recordedAt: '2026-10-17T06:00:00Z',
        technicalSourceFactId: 'source-fact-loan',
        technicalProvenanceId: 'provenance-loan',
      },
    }],
  });
  mount(api);
  expect(await screen.findByText('Car loan principal stated in document')).toBeVisible();
  expect(screen.getByRole('heading', { name: 'Loan information' })).toBeVisible();
  expect(screen.getByText('Source: loan-statement.txt')).toBeVisible();
  expect(screen.queryByText(/Total loan outstanding/i)).not.toBeInTheDocument();
  expect(window.location.pathname + window.location.search).toBe('/finances?type=LOAN');
  await userEvent.setup().click(screen.getByRole('button', { name: 'Expenses' }));
  expect(await screen.findByRole('heading', { name: 'No trusted expense information yet.' })).toBeVisible();
  expect(screen.queryByText('Car loan principal stated in document')).not.toBeInTheDocument();
});

it('shows reviewed Asset source figures with exact source, not a verified valuation', async () => {
  window.history.replaceState(null, '', '/finances?type=ASSET');
  const api = new AccessApi(session);
  vi.spyOn(api, 'product').mockResolvedValue({
    financialProfileId: 'financial-profile-reference-customer',
    revision: 4,
    updatedAt: '2026-10-17T06:30:00Z',
    dataDurability: 'Temporary data',
    facts: [{
      financialFactId: 'ff-asset-source',
      factType: 'ASSET',
      label: 'Property value stated in document',
      value: 900000,
      currency: 'INR',
      effectiveDate: '2026-10-16',
      revision: 1,
      source: {
        sourceName: 'property-source.txt',
        documentVersion: 'document-version-asset',
        recordedAt: '2026-10-17T06:30:00Z',
        technicalSourceFactId: 'source-fact-asset',
        technicalProvenanceId: 'provenance-asset',
      },
    }],
  });
  mount(api);
  expect(await screen.findByText('Property value stated in document')).toBeVisible();
  expect(screen.getByRole('heading', { name: 'Asset source figures' })).toBeVisible();
  expect(screen.getByText('Source: property-source.txt')).toBeVisible();
  expect(screen.queryByText(/Total net worth|Verified valuation/i)).not.toBeInTheDocument();
  expect(window.location.pathname + window.location.search).toBe('/finances?type=ASSET');
});

it('shows reviewed Investment source figures with exact source, not a market valuation', async () => {
  window.history.replaceState(null, '', '/finances?type=INVESTMENT');
  const api = new AccessApi(session);
  vi.spyOn(api, 'product').mockResolvedValue({
    financialProfileId: 'financial-profile-reference-customer',
    revision: 5,
    updatedAt: '2026-10-17T06:45:00Z',
    dataDurability: 'Temporary data',
    facts: [{
      financialFactId: 'ff-investment-source',
      factType: 'INVESTMENT',
      label: 'Investment amount stated in source document',
      value: 50000,
      currency: 'INR',
      effectiveDate: '2026-10-16',
      revision: 1,
      source: {
        sourceName: 'investment-source.txt',
        documentVersion: 'document-version-investment',
        recordedAt: '2026-10-17T06:45:00Z',
        technicalSourceFactId: 'source-fact-investment',
        technicalProvenanceId: 'provenance-investment',
      },
    }],
  });
  mount(api);
  expect(await screen.findByText('Investment amount stated in source document')).toBeVisible();
  expect(screen.getByRole('heading', { name: 'Investment source figures' })).toBeVisible();
  expect(screen.getByText('Source: investment-source.txt')).toBeVisible();
  expect(window.location.pathname + window.location.search).toBe('/finances?type=INVESTMENT');
  expect(screen.queryByText(/verified NAV|live market value|portfolio return/i)).not.toBeInTheDocument();
});


it('shows a reviewed Insurance source figure linked to exact Evidence history, not policy coverage', async () => {
  window.history.replaceState(null, '', '/finances?type=INSURANCE');
  const api = new AccessApi(session);
  vi.spyOn(api, 'product').mockImplementation(path => path === 'v1/insurance-records' ? Promise.resolve({items:[],hasMore:false,dataDurability:'Temporary data'}) : Promise.resolve({
    financialProfileId: 'financial-profile-reference-customer',
    revision: 6,
    updatedAt: '2026-10-17T09:00:00Z',
    dataDurability: 'Temporary data',
    facts: [{
      financialFactId: 'ff-insurance-source',
      factType: 'INSURANCE',
      label: 'Insurance amount stated in source document',
      value: 120000,
      currency: 'INR',
      effectiveDate: '2026-10-16',
      revision: 1,
      source: {
        sourceName: 'insurance-source.txt',
        documentVersion: 'document-version-insurance',
        recordedAt: '2026-10-17T09:00:00Z',
        technicalSourceFactId: 'source-fact-insurance',
        technicalProvenanceId: 'provenance-insurance',
      },
    }],
  }));
  mount(api,['financial-profile.profile.read','evidence.document.read','financial-profile.profile.update']);
  expect(await screen.findByText('Insurance amount stated in source document')).toBeVisible();
  expect(screen.getByRole('heading', { name: 'Insurance source figures' })).toBeVisible();
  expect(screen.getByText('Source: insurance-source.txt')).toBeVisible();
  expect(screen.queryByText(/verified coverage|claim eligibility|cash value/i)).not.toBeInTheDocument();
  expect(window.location.pathname + window.location.search).toBe('/finances?type=INSURANCE');
  expect(screen.getByRole('heading',{name:'Insurance policies'})).toBeVisible();
  await waitFor(()=>expect(screen.getByRole('button',{name:'Add insurance record'})).toBeEnabled());
  const user=userEvent.setup();await user.click(screen.getByRole('button',{name:'Add insurance record'}));
  const drawer=screen.getByRole('dialog',{name:'Add insurance record'});
  await user.click(within(drawer).getByRole('combobox',{name:'Reviewed insurance source'}));
  await waitFor(()=>expect(screen.getByText(/Insurance amount stated in source document ·/,{selector:'.ant-select-item-option-content'})).toBeVisible());
  expect(within(drawer).getByLabelText('Sum assured')).toHaveValue(null);
  expect(within(drawer).getByLabelText('Premium amount')).toHaveValue(null);
});


it('shows a reviewed Tax source figure without claiming assessed tax or refund', async () => {
  window.history.replaceState(null, '', '/finances?type=TAX');
  const api = new AccessApi(session);
  vi.spyOn(api, 'product').mockImplementation(path=>path==='v1/tax-records'?Promise.resolve({items:[],hasMore:false,dataDurability:'Temporary data'}):Promise.resolve({
    financialProfileId: 'financial-profile-reference-customer',
    revision: 7,
    updatedAt: '2026-10-17T07:00:00Z',
    dataDurability: 'Temporary data',
    facts: [{
      financialFactId: 'ff-tax-source',
      factType: 'TAX',
      label: 'Tax figure from document',
      value: 14000,
      currency: 'INR',
      effectiveDate: '2026-10-16',
      revision: 1,
      source: {
        sourceName: 'tax-statement.txt',
        documentVersion: 'document-version-tax',
        recordedAt: '2026-10-17T07:00:00Z',
        technicalSourceFactId: 'source-fact-tax',
        technicalProvenanceId: 'provenance-tax',
      },
    }],
  }));
  mount(api,['financial-profile.profile.read','evidence.document.read','financial-profile.profile.update']);
  expect(await screen.findByText('Tax figure from document')).toBeVisible();
  expect(screen.getByRole('heading', {name:'Tax source figures'})).toBeVisible();
  expect(screen.getByText('Source: tax-statement.txt')).toBeVisible();
  expect(window.location.pathname + window.location.search).toBe('/finances?type=TAX');
  expect(screen.queryByText(/Assessed tax liability|Refund payable/i)).not.toBeInTheDocument();
  expect(screen.getByRole('heading',{name:'Tax records'})).toBeVisible();
  await waitFor(()=>expect(screen.getByRole('button',{name:'Add tax record'})).toBeEnabled());
  const user=userEvent.setup();await user.click(screen.getByRole('button',{name:'Add tax record'}));const drawer=screen.getByRole('dialog',{name:'Add tax record'});
  await user.click(within(drawer).getByRole('combobox',{name:'Reviewed tax source'}));await waitFor(()=>expect(screen.getByText(/Tax figure from document ·/,{selector:'.ant-select-item-option-content'})).toBeVisible());
  expect(within(drawer).getByLabelText('Tax amount for this record')).toHaveValue(null);
});

it.each(['PROPERTY', 'VEHICLE'])('shows the reviewed %s type on Asset source readback', async (assetType) => {
  window.history.replaceState(null, '', '/finances?type=ASSET');
  const api = new AccessApi(session);
  vi.spyOn(api, 'product').mockImplementation(path=>path==='v1/tax-records'?Promise.resolve({items:[],hasMore:false,dataDurability:'Temporary data'}):Promise.resolve({
    financialProfileId: 'financial-profile-reference-customer',
    revision: 4,
    updatedAt: '2026-10-17T06:30:00Z',
    dataDurability: 'Temporary data',
    facts: [{
      financialFactId: 'ff-asset-source',
      factType: 'ASSET', assetType,
      label: 'Property value stated in document',
      value: 900000,
      currency: 'INR',
      effectiveDate: '2026-10-16',
      revision: 1,
      source: {
        sourceName: 'property-source.txt',
        documentVersion: 'document-version-asset',
        recordedAt: '2026-10-17T06:30:00Z',
        technicalSourceFactId: 'source-fact-asset',
        technicalProvenanceId: 'provenance-asset',
      },
    }],
  }));
  mount(api,['financial-profile.profile.read','evidence.document.read','financial-profile.profile.update']);
  expect(await screen.findByText('Property value stated in document')).toBeVisible();
  expect(screen.getByText('Asset type: ' + (assetType === 'PROPERTY' ? 'Property' : 'Vehicle'))).toBeVisible();
  expect(screen.getByRole('heading', { name: 'Asset source figures' })).toBeVisible();
  expect(screen.getByText('Source: property-source.txt')).toBeVisible();
  expect(screen.queryByText(/Total net worth|Verified valuation/i)).not.toBeInTheDocument();
  expect(window.location.pathname + window.location.search).toBe('/finances?type=ASSET');
});

it.each(['PROPERTY', 'VEHICLE'])('retains the reviewed %s type in exact source history', async (assetType) => {
  window.history.replaceState(null, '', '/finances?fact=ff-income');
  const api = new AccessApi(session);
  vi.spyOn(api, 'product').mockImplementation((path) => {
    const fact = {
      financialFactId: 'ff-income',
      factType: 'ASSET', assetType,
      label: 'Stated asset figure',
      value: 125000,
      currency: 'USD',
      effectiveDate: '2026-10-01',
      revision: 1,
      source: {
        sourceName: 'income-statement.txt',
        documentVersion: 'document-version-001',
        recordedAt: '2026-10-15T12:00:00Z',
        technicalSourceFactId: 'source-fact-001',
        technicalProvenanceId: 'provenance-001',
      },
    };
    if (path === 'v1/financial-profile')
      return Promise.resolve({
        financialProfileId: 'financial-profile-reference-customer',
        revision: 1,
        updatedAt: fact.source.recordedAt,
        dataDurability: 'Temporary data',
        facts: [fact],
      });
    if (path === 'v1/financial-profile/facts/ff-income')
      return Promise.resolve({ current: fact, history: [fact] });
    return Promise.reject(new Error('unexpected product endpoint'));
  });

  mount(api);

  const drawer = await screen.findByRole('dialog', { name: 'Financial information details' });
  expect(await within(drawer).findByText('Stated asset figure', { selector: 'h2' })).toBeVisible();
  expect(drawer).toHaveTextContent('1 revision');
  expect(within(drawer).getAllByText('Asset type: ' + (assetType === 'PROPERTY' ? 'Property' : 'Vehicle'))).toHaveLength(2);
});

it('opens reviewed investment records alongside source figures under the existing investment category',async()=>{
  window.history.replaceState(null,'','/finances?type=INVESTMENT');const api=new AccessApi(session);
  const request=vi.spyOn(api,'product').mockImplementation((path)=>path==='v1/investment-records'
    ?Promise.resolve({items:[],hasMore:false,dataDurability:'Temporary data'})
    :Promise.resolve({financialProfileId:'profile',revision:1,updatedAt:'2026-10-19T00:00:00Z',dataDurability:'Temporary data',facts:[]}));
  mount(api,['financial-profile.profile.read','evidence.document.read','financial-profile.profile.update']);
  expect(await screen.findByRole('heading',{name:'Investment records'})).toBeVisible();
  expect(screen.getByRole('button',{name:'Add investment record'})).toBeDisabled();
  expect(request.mock.calls.some(([path])=>path==='v1/investment-records')).toBe(true);
});

it('opens reviewed income records alongside income source figures',async()=>{
  window.history.replaceState(null,'','/finances?type=INCOME');const api=new AccessApi(session);
  const request=vi.spyOn(api,'product').mockImplementation(path=>path==='v1/income-records'
    ?Promise.resolve({items:[],hasMore:false,dataDurability:'Temporary data'})
    :Promise.resolve({financialProfileId:'profile',revision:1,updatedAt:'2026-10-19T00:00:00Z',dataDurability:'Temporary data',facts:[]}));
  mount(api,['financial-profile.profile.read','evidence.document.read','financial-profile.profile.update']);
  expect(await screen.findByRole('heading',{name:'Income records'})).toBeVisible();expect(screen.getByRole('button',{name:'Add income record'})).toBeDisabled();
  expect(request.mock.calls.some(([path])=>path==='v1/income-records')).toBe(true);
});

it('opens reviewed expense records alongside expense source figures',async()=>{
  window.history.replaceState(null,'','/finances?type=EXPENSE');const api=new AccessApi(session);
  const request=vi.spyOn(api,'product').mockImplementation(path=>path==='v1/expense-records'
    ?Promise.resolve({items:[],hasMore:false,dataDurability:'Temporary data'})
    :Promise.resolve({financialProfileId:'profile',revision:1,updatedAt:'2026-10-19T00:00:00Z',dataDurability:'Temporary data',facts:[]}));
  mount(api,['financial-profile.profile.read','evidence.document.read','financial-profile.profile.update']);
  expect(await screen.findByRole('heading',{name:'Expense records'})).toBeVisible();expect(screen.getByRole('button',{name:'Add expense record'})).toBeDisabled();
  expect(request.mock.calls.some(([path])=>path==='v1/expense-records')).toBe(true);
});

it.each([true,false])('guards the liability records route with current document access (%s)',async documentAccess=>{
  window.history.replaceState(null,'','/finances?type=LIABILITY');const api=new AccessApi(session);
  const request=vi.spyOn(api,'product').mockImplementation(path=>path==='v1/liability-records'
    ?Promise.resolve({items:[],hasMore:false,dataDurability:'Temporary data'})
    :Promise.resolve({financialProfileId:'profile',revision:1,updatedAt:'2026-10-19T00:00:00Z',dataDurability:'Temporary data',facts:[]}));
  mount(api,['financial-profile.profile.read',...(documentAccess?['evidence.document.read','financial-profile.profile.update']:[])]);
  if(documentAccess){expect(await screen.findByRole('heading',{name:'Liability records'})).toBeVisible();expect(screen.getByRole('button',{name:'Add liability record'})).toBeDisabled();}
  else expect(await screen.findByText('Document access is required to view liability records and their sources.')).toBeVisible();
  expect(request.mock.calls.some(([path])=>path==='v1/liability-records')).toBe(documentAccess);
});

it('opens liabilities from the financial navigation using reviewed loan sources without creating or totaling debt',async()=>{
  const api=new AccessApi(session);const request=vi.spyOn(api,'product').mockImplementation(path=>path==='v1/liability-records'
    ?Promise.resolve({items:[],hasMore:false,dataDurability:'Temporary data'})
    :Promise.resolve({financialProfileId:'profile',revision:1,updatedAt:'2026-10-19T00:00:00Z',dataDurability:'Temporary data',facts:[{
      financialFactId:'loan-source',factType:'LOAN',label:'Reviewed debt figure',value:100,currency:'USD',effectiveDate:'2026-09-30',revision:1,
      source:{sourceName:'loan.txt',documentVersion:'document-v1',recordedAt:'2026-10-19T00:00:00Z',technicalSourceFactId:'source',technicalProvenanceId:'provenance'}
    }]}));
  mount(api,['financial-profile.profile.read','evidence.document.read','financial-profile.profile.update']);const user=userEvent.setup();
  await user.click(await screen.findByRole('button',{name:'Liabilities'}));
  expect(await screen.findByRole('heading',{name:'Liability records'})).toBeVisible();expect(screen.getByRole('button',{name:'Add liability record'})).toBeEnabled();
  expect(window.location.search).toBe('?type=LIABILITY');expect(request.mock.calls.every(([,method])=>method==='GET')).toBe(true);
  expect(screen.getByText(/do not establish total debt/)).toBeVisible();
});

it('opens property records under Assets with only explicitly classified property sources',async()=>{
  window.history.replaceState(null,'','/finances?type=ASSET');const api=new AccessApi(session);
  const fact={financialFactId:'ff-property',factType:'ASSET',assetType:'PROPERTY',label:'Property source amount',value:120000,currency:'USD',effectiveDate:'2026-10-01',revision:1,
    source:{sourceName:'property.txt',documentVersion:'document-property',recordedAt:'2026-10-19T00:00:00Z',technicalSourceFactId:'source-property',technicalProvenanceId:'provenance-property'}};
  const request=vi.spyOn(api,'product').mockImplementation(path=>path==='v1/property-records'
    ?Promise.resolve({items:[],hasMore:false,dataDurability:'Temporary data'})
    :Promise.resolve({financialProfileId:'profile',revision:1,updatedAt:'2026-10-19T00:00:00Z',dataDurability:'Temporary data',facts:[fact,{...fact,financialFactId:'ff-vehicle',assetType:'VEHICLE',label:'Vehicle source amount'}]}));
  mount(api,['financial-profile.profile.read','evidence.document.read','financial-profile.profile.update']);
  expect(await screen.findByRole('heading',{name:'Property records'})).toBeVisible();
  await waitFor(()=>expect(screen.getByRole('button',{name:'Add property record'})).toBeEnabled());
  expect(request.mock.calls.some(([path])=>path==='v1/property-records')).toBe(true);
  await userEvent.setup().click(screen.getByRole('button',{name:'Add property record'}));
  const drawer=screen.getByRole('dialog',{name:'Add property record'});
  await userEvent.setup().click(within(drawer).getByRole('combobox',{name:'Reviewed property source'}));
  await waitFor(()=>expect(screen.getByText(/Property source amount ·/,{selector:'.ant-select-item-option-content'})).toBeVisible());
  expect(screen.queryByText(/Vehicle source amount ·/,{selector:'.ant-select-item-option-content'})).not.toBeInTheDocument();
});

it('opens vehicle records under Assets with only explicitly classified vehicle sources',async()=>{
  window.history.replaceState(null,'','/finances?type=ASSET');const api=new AccessApi(session);
  const fact={financialFactId:'ff-vehicle',factType:'ASSET',assetType:'VEHICLE',label:'Vehicle source amount',value:120000,currency:'USD',effectiveDate:'2026-10-01',revision:1,
    source:{sourceName:'vehicle.txt',documentVersion:'document-vehicle',recordedAt:'2026-10-19T00:00:00Z',technicalSourceFactId:'source-vehicle',technicalProvenanceId:'provenance-vehicle'}};
  const request=vi.spyOn(api,'product').mockImplementation(path=>path==='v1/vehicle-records'||path==='v1/property-records'
    ?Promise.resolve({items:[],hasMore:false,dataDurability:'Temporary data'})
    :Promise.resolve({financialProfileId:'profile',revision:1,updatedAt:'2026-10-19T00:00:00Z',dataDurability:'Temporary data',facts:[fact,{...fact,financialFactId:'ff-property',assetType:'PROPERTY',label:'Property source amount'}]}));
  mount(api,['financial-profile.profile.read','evidence.document.read','financial-profile.profile.update']);
  expect(await screen.findByRole('heading',{name:'Vehicle records'})).toBeVisible();
  await waitFor(()=>expect(screen.getByRole('button',{name:'Add vehicle record'})).toBeEnabled());expect(request.mock.calls.some(([path])=>path==='v1/vehicle-records')).toBe(true);
  const user=userEvent.setup();await user.click(screen.getByRole('button',{name:'Add vehicle record'}));const drawer=screen.getByRole('dialog',{name:'Add vehicle record'});
  await user.click(within(drawer).getByRole('combobox',{name:'Reviewed vehicle source'}));
  await waitFor(()=>expect(screen.getByText(/Vehicle source amount ·/,{selector:'.ant-select-item-option-content'})).toBeVisible());
  expect(screen.queryByText(/Property source amount ·/,{selector:'.ant-select-item-option-content'})).not.toBeInTheDocument();
});


it('opens customer-defined goals without documents or source-profile access',async()=>{
  window.history.replaceState(null,'','/finances?type=GOAL');const api=new AccessApi(session);
  const request=vi.spyOn(api,'product').mockImplementation(path=>path==='v1/financial-goals'?Promise.resolve({items:[],hasMore:false,dataDurability:'Temporary data'}):Promise.reject(new Error('Source profile must not be requested')));
  mount(api,['financial-profile.profile.read','financial-profile.profile.update']);
  expect(await screen.findByText(/No financial goals added/)).toBeVisible();expect(screen.getByRole('button',{name:'Add goal'})).toBeEnabled();
  expect(request.mock.calls.every(([path])=>path==='v1/financial-goals')).toBe(true);expect(screen.queryByText(/Review documents/)).not.toBeInTheDocument();
});
it('makes financial goals reachable before any source figures are added',async()=>{
  const api=new AccessApi(session);vi.spyOn(api,'product').mockImplementation(path=>Promise.resolve(path==='v1/financial-goals'?{items:[],hasMore:false,dataDurability:'Temporary data'}:{financialProfileId:'profile',revision:0,updatedAt:null,dataDurability:'Temporary data',facts:[]}));
  mount(api,['financial-profile.profile.read']);const user=userEvent.setup();await user.click(screen.getByRole('button',{name:'Financial goals'}));
  expect(await screen.findByText(/No financial goals added/)).toBeVisible();expect(window.location.search).toBe('?type=GOAL');expect(screen.queryByRole('button',{name:'Add goal'})).not.toBeInTheDocument();
  await user.click(screen.getByRole('button',{name:'Back to financial information'}));expect(window.location.search).toBe('');expect(await screen.findByText('Your financial information will appear here after you review it.')).toBeVisible();
});
