import { FoundationProvider } from '@monergy/ui-foundation';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, it, vi } from 'vitest';
import { AccessApi, AccessApiError } from '../src/access/accessApi';
import LoanRecordsExperience from '../src/product/LoanRecordsExperience';
import type { ProductFinancialFact, ProductLoanDraft, ProductLoanRecord } from '../src/product/productApi';
const session = { tenantId: 'T001', actorId: 'A100', authenticationContextId: 'customer-session', subjectVersion: 2, expiresAt: '2027-01-01T00:00:00Z' };
const source: ProductFinancialFact = { financialFactId: 'ff-loan', factType: 'LOAN', label: 'Sanctioned amount stated in source', value: 500000,
  currency: 'USD', effectiveDate: '2026-01-01', revision: 2, source: { sourceName: 'loan.txt', documentVersion: 'document-v2',
    recordedAt: '2026-10-01T00:00:00Z', technicalSourceFactId: 'source-loan-2', technicalProvenanceId: 'provenance-2' } };
const loan: ProductLoanDraft = { name: 'Home loan', lender: 'Example lender', loanType: 'HOME', sanctionAmount: 500000, outstanding: 400000,
  interestRatePercent: 8.25, emi: 10000, tenureMonths: 60, startDate: '2026-01-01', endDate: '2030-12-31', balanceAsOfDate: '2026-10-01',
  sourceFinancialFactId: 'ff-loan', sourceRevision: 2 };
const record: ProductLoanRecord = { loanRecordId: 'loan-saved', operationId: 'a'.repeat(32), customerId: 'reference-customer', loan,
  currency: 'USD', sourceProvenanceId: 'provenance-2', documentVersionId: 'document-v2', evidenceId: 'evidence-loan',
  recordedBy: 'A100', recordedAt: '2026-10-19T00:00:00Z' };
function mount(api: AccessApi, canCreate = true, canRead = true, facts: ProductFinancialFact[] = [source]) {
  return render(<FoundationProvider appearance="midnight"><LoanRecordsExperience api={api} canCreate={canCreate} canRead={canRead} facts={facts} /></FoundationProvider>);
}
afterEach(() => { cleanup(); vi.restoreAllMocks(); });
async function fill(user: ReturnType<typeof userEvent.setup>) {
  await user.click(await screen.findByRole('button', { name: 'Add loan record' }));
  const drawer = screen.getByRole('dialog', { name: 'Add loan record' });
  await user.click(within(drawer).getByRole('combobox', { name: 'Reviewed loan source' }));
  await user.click(screen.getByText(/Sanctioned amount stated in source ·/, { selector: '.ant-select-item-option-content' }));
  expect(within(drawer).getByLabelText('Outstanding balance')).toHaveValue(null);
  await user.type(within(drawer).getByLabelText('Loan name'), 'Home loan');
  await user.type(within(drawer).getByLabelText('Lender'), 'Example lender');
  await user.click(within(drawer).getByRole('combobox', { name: 'Loan type' }));
  await user.click(screen.getByText('Home loan', { selector: '.ant-select-item-option-content' }));
  for (const [label,value] of [['Sanction amount','500000'],['Outstanding balance','400000'],['Annual interest rate (%)','8.25'],['Monthly EMI','10000'],['Tenure (months)','60']] as const)
    await user.type(within(drawer).getByLabelText(label), value);
  for (const [label,value] of [['Start date','2026-01-01'],['End date','2030-12-31'],['Balance as of','2026-10-01']] as const)
    fireEvent.change(within(drawer).getByLabelText(label), { target: { value } });
  return drawer;
}
it('requires explicit reviewed terms, preserves the source revision and shows owner-confirmed readback', async () => {
  const api = new AccessApi(session); let stored = false;
  const request = vi.spyOn(api,'product').mockImplementation((path,method,body) => {
    if (path === 'v1/loan-records' && method === 'GET') return Promise.resolve({ items: stored ? [record] : [], hasMore: false, dataDurability: 'Temporary data' });
    if (path === 'v1/loan-records' && method === 'POST') { expect(body).toEqual({ operationId: 'a'.repeat(32), loan }); stored = true; return Promise.resolve(record); }
    return Promise.reject(new Error('unexpected route'));
  });
  vi.spyOn(crypto,'randomUUID').mockReturnValue('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa'); mount(api);
  const user=userEvent.setup(); const drawer=await fill(user);
  expect(request.mock.calls.filter(([,method]) => method === 'POST')).toHaveLength(0);
  expect(drawer).toHaveTextContent('Enter the outstanding balance separately.');
  await user.click(within(drawer).getByRole('button',{name:'Save reviewed loan record'}));
  expect(await screen.findByText('Loan record saved: Home loan')).toBeVisible();
  await user.click(await screen.findByRole('button',{name:'View loan record: Home loan'}));
  const details=screen.getByRole('dialog',{name:'Loan record details'});
  expect(details).toHaveTextContent('$400,000.00 as of 2026-10-01'); expect(details).toHaveTextContent('8.25%'); expect(details).toHaveTextContent('60 months');
  expect(within(details).getByRole('link',{name:'View exact loan source history'})).toHaveAttribute('href','/finances?fact=ff-loan');
  expect(details).toHaveTextContent('have not been independently verified');
});
it('retries an uncertain save with exactly the same operation and reviewed terms', async () => {
  const api=new AccessApi(session); let attempts=0;
  const request=vi.spyOn(api,'product').mockImplementation((_path,method) => {
    if(method==='GET') return Promise.resolve({items:[],hasMore:false,dataDurability:'Temporary data'});
    attempts++; return attempts===1 ? Promise.reject(new AccessApiError(503,'unavailable')) : Promise.resolve(record);
  });
  vi.spyOn(crypto,'randomUUID').mockReturnValue('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa');mount(api);const user=userEvent.setup();const drawer=await fill(user);
  await user.click(within(drawer).getByRole('button',{name:'Save reviewed loan record'}));
  expect(await within(drawer).findByText(/The save has not been confirmed/)).toBeVisible();
  expect(within(drawer).getByLabelText('Outstanding balance')).toBeDisabled();
  await user.click(within(drawer).getByRole('button',{name:'Retry save confirmation'}));
  expect(await screen.findByText('Loan record saved: Home loan')).toBeVisible();
  const calls=request.mock.calls.filter(([,method])=>method==='POST'); expect(calls).toHaveLength(2);expect(calls.at(0)?.at(2)).toEqual(calls.at(1)?.at(2));
});
it('leaves the form editable after a confirmed validation rejection', async () => {
  const api=new AccessApi(session);vi.spyOn(api,'product').mockImplementation((_path,method)=>method==='GET'
    ? Promise.resolve({items:[],hasMore:false,dataDurability:'Temporary data'}) : Promise.reject(new AccessApiError(400,'invalid')));
  mount(api);const user=userEvent.setup();const drawer=await fill(user);await user.click(within(drawer).getByRole('button',{name:'Save reviewed loan record'}));
  expect(await within(drawer).findByText(/Check the required amounts/)).toBeVisible();expect(within(drawer).getByLabelText('Outstanding balance')).toBeEnabled();
  expect(screen.queryByText(/Loan record saved:/)).not.toBeInTheDocument();
});
it('keeps read-only records visible and hides creation without update access', async () => {
  const api=new AccessApi(session);vi.spyOn(api,'product').mockResolvedValue({items:[record],hasMore:false,dataDurability:'Temporary data'});mount(api,false);
  expect(await screen.findByRole('button',{name:'View loan record: Home loan'})).toBeVisible();expect(screen.queryByRole('button',{name:'Add loan record'})).not.toBeInTheDocument();
});
it('makes no record request without document access', () => {
  const api=new AccessApi(session);const request=vi.spyOn(api,'product');mount(api,true,false);
  expect(screen.getByText(/Document access is required/)).toBeVisible();expect(request).not.toHaveBeenCalled();
});
it('requires a previously reviewed loan source and does not accept another source category', async () => {
  const api=new AccessApi(session);vi.spyOn(api,'product').mockResolvedValue({items:[],hasMore:false,dataDurability:'Temporary data'});mount(api,true,true,[{...source,factType:'INCOME'}]);
  expect(screen.getByRole('button',{name:'Add loan record'})).toBeDisabled();await screen.findByText(/No loan records added yet/);
});
it('shows unavailable data explicitly rather than presenting an empty loan position', async () => {
  const api=new AccessApi(session);vi.spyOn(api,'product').mockRejectedValue(new AccessApiError(503,'unavailable'));mount(api);
  expect(await screen.findByText('Loan records are unavailable. Try again.')).toBeVisible();expect(screen.queryByText(/No loan records added yet/)).not.toBeInTheDocument();
});
it('hides prior records immediately after an API/session scope change', async () => {
  const api=new AccessApi(session);vi.spyOn(api,'product').mockResolvedValue({items:[record],hasMore:false,dataDurability:'Temporary data'});const view=mount(api,false);
  await screen.findByRole('button',{name:'View loan record: Home loan'});const nextApi=new AccessApi({...session,actorId:'A200'});
  vi.spyOn(nextApi,'product').mockResolvedValue({items:[],hasMore:false,dataDurability:'Temporary data'});
  view.rerender(<FoundationProvider appearance="midnight"><LoanRecordsExperience api={nextApi} canCreate={false} canRead facts={[source]} /></FoundationProvider>);
  expect(screen.queryByRole('button',{name:'View loan record: Home loan'})).not.toBeInTheDocument();
  await waitFor(()=>expect(screen.getByText(/No loan records added yet/)).toBeVisible());
});

it('requires a new explicit source choice when its current revision changes during review', async () => {
  const api=new AccessApi(session);
  const request=vi.spyOn(api,'product').mockImplementation((_path,method)=>method==='GET'
    ? Promise.resolve({items:[],hasMore:false,dataDurability:'Temporary data'})
    : Promise.resolve({...record,loan:{...loan,sourceRevision:3}}));
  vi.spyOn(crypto,'randomUUID').mockReturnValue('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa');const view=mount(api);const user=userEvent.setup();const drawer=await fill(user);
  const changed={...source,revision:3,value:550000};
  view.rerender(<FoundationProvider appearance="midnight"><LoanRecordsExperience api={api} canCreate canRead facts={[changed]} /></FoundationProvider>);
  expect(within(drawer).getByText(/The source revision has changed/)).toBeVisible();
  expect(within(drawer).getByRole('button',{name:'Save reviewed loan record'})).toBeDisabled();
  expect(request.mock.calls.filter(([,method])=>method==='POST')).toHaveLength(0);
  await user.click(within(drawer).getByRole('combobox',{name:'Reviewed loan source'}));
  await user.click(screen.getByText(/Sanctioned amount stated in source ·/, {selector:'.ant-select-item-option-content'}));
  await user.click(within(drawer).getByRole('button',{name:'Save reviewed loan record'}));
  await screen.findByText('Loan record saved: Home loan');
  expect(request.mock.calls.find(([,method])=>method==='POST')?.at(2)).toEqual({operationId:'a'.repeat(32),loan:{...loan,sourceRevision:3}});
});
it('preserves an unconfirmed submission when the review drawer is closed and reopened', async () => {
  const api=new AccessApi(session);vi.spyOn(api,'product').mockImplementation((_path,method)=>method==='GET'
    ? Promise.resolve({items:[],hasMore:false,dataDurability:'Temporary data'}) : Promise.reject(new AccessApiError(503,'unavailable')));
  mount(api);const user=userEvent.setup();const drawer=await fill(user);await user.click(within(drawer).getByRole('button',{name:'Save reviewed loan record'}));
  await within(drawer).findByText(/The save has not been confirmed/);
  await user.click(within(drawer).getByRole('button',{name:'Close'}));
  await user.click(screen.getByRole('button',{name:'Continue save confirmation'}));
  expect(within(screen.getByRole('dialog',{name:'Add loan record'})).getByLabelText('Outstanding balance')).toBeDisabled();
  expect(screen.getByRole('button',{name:'Retry save confirmation'})).toBeVisible();
});
