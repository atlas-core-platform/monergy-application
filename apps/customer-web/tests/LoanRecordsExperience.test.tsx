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
  recordedBy: 'A100', recordedAt: '2026-10-19T00:00:00Z', revision: 1, lifecycleStatus: 'UNREVIEWED', lifecycleAsOfDate: null };
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

it('edits the saved terms explicitly using the exact record version and immutable historical source', async () => {
  const api=new AccessApi(session);let current=record;
  const corrected={...record,revision:2,loan:{...loan,outstanding:390000}};
  const request=vi.spyOn(api,'product').mockImplementation((path,method,body)=>{
    if(method==='GET') return Promise.resolve({items:[current],hasMore:false,dataDurability:'Temporary data'});
    expect(path).toBe('v1/loan-records/loan-saved/corrections');
    expect(body).toEqual({operationId:'a'.repeat(32),expectedRevision:1,loan:corrected.loan});current=corrected;return Promise.resolve(corrected);
  });
  vi.spyOn(crypto,'randomUUID').mockReturnValue('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa');mount(api,true,true,[]);
  const user=userEvent.setup();await user.click(await screen.findByRole('button',{name:'Edit loan record: Home loan'}));
  const drawer=screen.getByRole('dialog',{name:'Edit loan record'});
  expect(within(drawer).getByText(/Editing version 1 · USD · Source revision 2/)).toBeVisible();
  expect(within(drawer).queryByRole('combobox',{name:'Reviewed loan source'})).not.toBeInTheDocument();
  expect(within(drawer).getByLabelText('Outstanding balance')).toHaveValue(400000);
  fireEvent.change(within(drawer).getByLabelText('Outstanding balance'),{target:{value:'390000'}});
  expect(request.mock.calls.filter(([,method])=>method==='POST')).toHaveLength(0);
  await user.click(within(drawer).getByRole('button',{name:'Save reviewed corrections'}));
  await screen.findByText('Loan record saved: Home loan');expect(screen.getByText(/Outstanding balance: \$390,000.00/)).toBeVisible();
});

it('blocks a stale correction and requires review of the refreshed version', async () => {
  const api=new AccessApi(session);let current=record;
  const request=vi.spyOn(api,'product').mockImplementation((_path,method)=>method==='GET'
    ? Promise.resolve({items:[current],hasMore:false,dataDurability:'Temporary data'})
    : Promise.reject(new AccessApiError(409,'LOAN_REVISION_CONFLICT')));
  mount(api);const user=userEvent.setup();await user.click(await screen.findByRole('button',{name:'Edit loan record: Home loan'}));
  const drawer=screen.getByRole('dialog',{name:'Edit loan record'});await user.click(within(drawer).getByRole('button',{name:'Save reviewed corrections'}));
  expect(await within(drawer).findByText(/A newer version has been saved/)).toBeVisible();
  expect(within(drawer).getByRole('button',{name:'Save reviewed corrections'})).toBeDisabled();expect(within(drawer).getByLabelText('Outstanding balance')).toBeDisabled();
  await user.click(within(drawer).getByRole('button',{name:'Close'}));current={...record,revision:2,loan:{...loan,outstanding:380000}};
  await user.click(screen.getByRole('button',{name:'Refresh loan records'}));await screen.findByText(/Outstanding balance: \$380,000.00/);
  await user.click(screen.getByRole('button',{name:'Edit loan record: Home loan'}));
  expect(within(screen.getByRole('dialog',{name:'Edit loan record'})).getByText(/Editing version 2/)).toBeVisible();
  expect(request.mock.calls.filter(([,method])=>method==='POST')).toHaveLength(1);
});

it('retains an uncertain correction and its expected version after closing with no current source figures', async () => {
  const api=new AccessApi(session);let attempts=0;
  const request=vi.spyOn(api,'product').mockImplementation((_path,method)=>{
    if(method==='GET')return Promise.resolve({items:[record],hasMore:false,dataDurability:'Temporary data'});
    attempts++;return attempts===1?Promise.reject(new AccessApiError(503,'unavailable')):Promise.resolve({...record,revision:2});
  });
  vi.spyOn(crypto,'randomUUID').mockReturnValue('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa');mount(api,true,true,[]);
  const user=userEvent.setup();await user.click(await screen.findByRole('button',{name:'Edit loan record: Home loan'}));
  const drawer=screen.getByRole('dialog',{name:'Edit loan record'});await user.click(within(drawer).getByRole('button',{name:'Save reviewed corrections'}));
  await within(drawer).findByText(/The save has not been confirmed/);expect(within(drawer).getByLabelText('Lender')).toBeDisabled();
  await user.click(within(drawer).getByRole('button',{name:'Close'}));await user.click(screen.getByRole('button',{name:'Continue save confirmation'}));
  const reopened=screen.getByRole('dialog',{name:'Edit loan record'});await user.click(within(reopened).getByRole('button',{name:'Retry save confirmation'}));
  await screen.findByText('Loan record saved: Home loan');const calls=request.mock.calls.filter(([,method])=>method==='POST');
  expect(calls).toHaveLength(2);expect(calls[0]?.[2]).toEqual(calls[1]?.[2]);expect(calls[0]?.[2]).toMatchObject({expectedRevision:1});
});

it('allows correction after confirmed validation rejection and keeps uncertain conflicts frozen', async () => {
  const api=new AccessApi(session);let conflict=false;
  vi.spyOn(api,'product').mockImplementation((_path,method)=>method==='GET'
    ?Promise.resolve({items:[record],hasMore:false,dataDurability:'Temporary data'})
    :Promise.reject(new AccessApiError(conflict?409:400,conflict?'LOAN_OPERATION_CONFLICT':'invalid')));
  mount(api);const user=userEvent.setup();await user.click(await screen.findByRole('button',{name:'Edit loan record: Home loan'}));
  const drawer=screen.getByRole('dialog',{name:'Edit loan record'});await user.click(within(drawer).getByRole('button',{name:'Save reviewed corrections'}));
  await within(drawer).findByText(/Check the required amounts/);expect(within(drawer).getByLabelText('Lender')).toBeEnabled();
  conflict=true;await user.click(within(drawer).getByRole('button',{name:'Save reviewed corrections'}));
  await within(drawer).findByText(/The save has not been confirmed/);expect(within(drawer).getByLabelText('Lender')).toBeDisabled();
});

it('reads original and corrected terms in bounded history pages without enabling edits for read-only access', async () => {
  const api=new AccessApi(session);const latest={...record,revision:51,loan:{...loan,outstanding:300000}};
  const request=vi.spyOn(api,'product').mockImplementation((path)=>{
    if(path==='v1/loan-records')return Promise.resolve({items:[latest],hasMore:false,dataDurability:'Temporary data'});
    if(path==='v1/loan-records/loan-saved/history')return Promise.resolve({items:[latest],hasMore:true,nextBeforeRevision:2});
    if(path==='v1/loan-records/loan-saved/history?beforeRevision=2')return Promise.resolve({items:[record],hasMore:false,nextBeforeRevision:null});
    return Promise.reject(new Error('unexpected route'));
  });mount(api,false);const user=userEvent.setup();await user.click(await screen.findByRole('button',{name:'View change history: Home loan'}));
  expect(screen.queryByRole('button',{name:'Edit loan record: Home loan'})).not.toBeInTheDocument();
  const drawer=screen.getByRole('dialog',{name:'Loan record history'});await user.click(await within(drawer).findByRole('button',{name:'Earlier versions'}));
  await user.click(await within(drawer).findByRole('button',{name:'View version 1'}));
  const version=within(drawer).getByRole('region',{name:'Saved loan version 1'});expect(version).toHaveTextContent('$400,000.00 as of 2026-10-01');
  expect(version).toHaveTextContent('8.25%');expect(within(version).getByRole('link',{name:'View exact loan source history'})).toHaveAttribute('href','/finances?fact=ff-loan');
  await user.click(within(drawer).getByRole('button',{name:'Latest versions'}));await within(drawer).findByRole('button',{name:'View version 51'});
  expect(request.mock.calls.every(([,method])=>method==='GET')).toBe(true);
});

it('shows history failure and hides prior history immediately after a session scope change', async () => {
  const api=new AccessApi(session);vi.spyOn(api,'product').mockImplementation(path=>path==='v1/loan-records'
    ?Promise.resolve({items:[record],hasMore:false,dataDurability:'Temporary data'}):Promise.reject(new AccessApiError(403,'denied')));
  const view=mount(api);const user=userEvent.setup();await user.click(await screen.findByRole('button',{name:'View change history: Home loan'}));
  await screen.findByText('Loan history is unavailable.');expect(screen.queryByRole('button',{name:'View version 1'})).not.toBeInTheDocument();
  const other=new AccessApi({...session,actorId:'A200'});vi.spyOn(other,'product').mockResolvedValue({items:[],hasMore:false,dataDurability:'Temporary data'});
  view.rerender(<FoundationProvider appearance="midnight"><LoanRecordsExperience api={other} facts={[]} canRead canCreate={false} /></FoundationProvider>);
  expect(screen.queryByRole('dialog',{name:'Loan record history'})).not.toBeInTheDocument();await screen.findByText(/No loan records added yet/);
});

async function chooseLoanStatus(user: ReturnType<typeof userEvent.setup>, label='Closed') {
  await user.click(await screen.findByRole('button',{name:'Review loan status: Home loan'}));
  const drawer=screen.getByRole('dialog',{name:'Review loan status'});
  await user.click(within(drawer).getByRole('combobox',{name:'Loan status'}));
  await user.click(screen.getByText(label,{selector:'.ant-select-item-option-content'}));
  fireEvent.change(within(drawer).getByLabelText('Status as of'),{target:{value:'2026-10-19'}});
  return drawer;
}

it('requires explicit dated status review and keeps the original balance and terms when closed', async () => {
  const api=new AccessApi(session);let current=record;
  const closed: ProductLoanRecord={...record,revision:2,lifecycleStatus:'CLOSED',lifecycleAsOfDate:'2026-10-19'};
  const request=vi.spyOn(api,'product').mockImplementation((path,method,body)=>{
    if(method==='GET')return Promise.resolve({items:[current],hasMore:false,dataDurability:'Temporary data'});
    expect(path).toBe('v1/loan-records/loan-saved/lifecycle');expect(body).toEqual({operationId:'a'.repeat(32),expectedRevision:1,status:'CLOSED',asOfDate:'2026-10-19'});
    current=closed;return Promise.resolve(closed);
  });vi.spyOn(crypto,'randomUUID').mockReturnValue('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa');mount(api);
  expect(await screen.findByText('Status not reviewed')).toBeVisible();const user=userEvent.setup();
  await user.click(screen.getByRole('button',{name:'Review loan status: Home loan'}));const drawer=screen.getByRole('dialog',{name:'Review loan status'});
  expect(within(drawer).getByRole('button',{name:'Save reviewed loan status'})).toBeDisabled();expect(within(drawer).getByLabelText('Status as of')).toHaveValue('');
  await user.click(within(drawer).getByRole('combobox',{name:'Loan status'}));await user.click(screen.getByText('Closed',{selector:'.ant-select-item-option-content'}));
  expect(within(drawer).getByRole('button',{name:'Save reviewed loan status'})).toBeDisabled();
  fireEvent.change(within(drawer).getByLabelText('Status as of'),{target:{value:'2026-10-19'}});
  expect(request.mock.calls.filter(([,method])=>method==='POST')).toHaveLength(0);expect(drawer).toHaveTextContent('does not verify repayment');
  await user.click(within(drawer).getByRole('button',{name:'Save reviewed loan status'}));await screen.findByText('Loan status saved: Home loan');
  expect(screen.getByText('Closed — customer reported')).toBeVisible();expect(screen.getByText('Recorded balance: $400,000.00 · as of 2026-10-01')).toBeVisible();
  await user.click(screen.getByRole('button',{name:'View loan record: Home loan'}));const details=screen.getByRole('dialog',{name:'Loan record details'});
  expect(details).toHaveTextContent('Closed — customer reported as of 2026-10-19');expect(details).toHaveTextContent('8.25%');expect(details).toHaveTextContent('$10,000.00');
});

it('explicitly reopens a closed historical-source record without requiring a current extracted figure', async () => {
  const api=new AccessApi(session);const closed: ProductLoanRecord={...record,revision:2,lifecycleStatus:'CLOSED',lifecycleAsOfDate:'2026-10-10'};
  const active: ProductLoanRecord={...closed,revision:3,lifecycleStatus:'ACTIVE',lifecycleAsOfDate:'2026-10-19'};let current=closed;
  vi.spyOn(api,'product').mockImplementation((_path,method,body)=>{
    if(method==='GET')return Promise.resolve({items:[current],hasMore:false,dataDurability:'Temporary data'});
    expect(body).toMatchObject({expectedRevision:2,status:'ACTIVE',asOfDate:'2026-10-19'});current=active;return Promise.resolve(active);
  });vi.spyOn(crypto,'randomUUID').mockReturnValue('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa');mount(api,true,true,[]);
  const user=userEvent.setup();const drawer=await chooseLoanStatus(user,'Active — reopen record');
  expect(within(drawer).getByText(/This will reopen the record as customer reported/)).toBeVisible();
  await user.click(within(drawer).getByRole('button',{name:'Save reviewed loan status'}));await screen.findByText('Loan status saved: Home loan');
  expect(screen.getByText('Active — customer reported')).toBeVisible();expect(screen.getByText(/Outstanding balance: \$400,000.00/)).toBeVisible();
});

it('freezes an uncertain status request across drawer close and blocks overlapping term edits', async () => {
  const api=new AccessApi(session);let attempts=0;
  const request=vi.spyOn(api,'product').mockImplementation((_path,method)=>{
    if(method==='GET')return Promise.resolve({items:[record],hasMore:false,dataDurability:'Temporary data'});
    return ++attempts===1?Promise.reject(new AccessApiError(503,'unavailable')):Promise.resolve({...record,revision:2,lifecycleStatus:'CLOSED',lifecycleAsOfDate:'2026-10-19'});
  });vi.spyOn(crypto,'randomUUID').mockReturnValue('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa');mount(api,true,true,[]);
  const user=userEvent.setup();const drawer=await chooseLoanStatus(user);await user.click(within(drawer).getByRole('button',{name:'Save reviewed loan status'}));
  await within(drawer).findByText(/The status save has not been confirmed/);expect(within(drawer).getByLabelText('Status as of')).toBeDisabled();
  await user.click(within(drawer).getByRole('button',{name:'Close'}));expect(screen.getByRole('button',{name:'Edit loan record: Home loan'})).toBeDisabled();
  await user.click(screen.getByRole('button',{name:'Continue loan status confirmation'}));const reopened=screen.getByRole('dialog',{name:'Review loan status'});
  expect(within(reopened).getByLabelText('Status as of')).toBeDisabled();await user.click(within(reopened).getByRole('button',{name:'Retry status confirmation'}));
  await screen.findByText('Loan status saved: Home loan');const posts=request.mock.calls.filter(([,method])=>method==='POST');expect(posts).toHaveLength(2);expect(posts[0]?.[2]).toEqual(posts[1]?.[2]);
});

it('allows confirmed date correction but stops a stale status edit until the newer version is reviewed', async () => {
  const api=new AccessApi(session);let failure=400;
  vi.spyOn(api,'product').mockImplementation((_path,method)=>method==='GET'?Promise.resolve({items:[record],hasMore:false,dataDurability:'Temporary data'}):Promise.reject(new AccessApiError(failure,failure===400?'invalid':'LOAN_REVISION_CONFLICT')));
  mount(api);const user=userEvent.setup();const drawer=await chooseLoanStatus(user);await user.click(within(drawer).getByRole('button',{name:'Save reviewed loan status'}));
  await within(drawer).findByText(/Check the status and date/);expect(within(drawer).getByLabelText('Status as of')).toBeEnabled();
  failure=409;await user.click(within(drawer).getByRole('button',{name:'Save reviewed loan status'}));await within(drawer).findByText(/A newer version has been saved/);
  expect(within(drawer).getByRole('button',{name:'Save reviewed loan status'})).toBeDisabled();expect(within(drawer).getByLabelText('Status as of')).toBeDisabled();
});

it('shows reviewed status history to read-only customers without offering status changes', async () => {
  const api=new AccessApi(session);const closed: ProductLoanRecord={...record,revision:2,lifecycleStatus:'CLOSED',lifecycleAsOfDate:'2026-10-19'};
  vi.spyOn(api,'product').mockImplementation(path=>path==='v1/loan-records'?Promise.resolve({items:[closed],hasMore:false,dataDurability:'Temporary data'}):Promise.resolve({items:[closed,record],hasMore:false,nextBeforeRevision:null}));
  mount(api,false);const user=userEvent.setup();await screen.findByText('Closed — customer reported');expect(screen.queryByRole('button',{name:'Review loan status: Home loan'})).not.toBeInTheDocument();
  await user.click(screen.getByRole('button',{name:'View change history: Home loan'}));const drawer=screen.getByRole('dialog',{name:'Loan record history'});
  await within(drawer).findByText('Closed — customer reported as of 2026-10-19');expect(within(drawer).getByText('Status not reviewed')).toBeVisible();
  await user.click(within(drawer).getByRole('button',{name:'View version 2'}));const saved=within(drawer).getByRole('region',{name:'Saved loan version 2'});
  expect(saved).toHaveTextContent('Closed — customer reported as of 2026-10-19');expect(saved).toHaveTextContent('$400,000.00');
});

it('hides an unconfirmed prior-session status form and prevents a request after scope changes', async () => {
  const api=new AccessApi(session);vi.spyOn(api,'product').mockImplementation((_path,method)=>method==='GET'?Promise.resolve({items:[record],hasMore:false,dataDurability:'Temporary data'}):Promise.reject(new AccessApiError(503,'unavailable')));
  const view=mount(api);const user=userEvent.setup();const drawer=await chooseLoanStatus(user);await user.click(within(drawer).getByRole('button',{name:'Save reviewed loan status'}));await within(drawer).findByText(/The status save has not been confirmed/);
  const other=new AccessApi({...session,actorId:'A200'});const request=vi.spyOn(other,'product').mockResolvedValue({items:[],hasMore:false,dataDurability:'Temporary data'});
  view.rerender(<FoundationProvider appearance="midnight"><LoanRecordsExperience api={other} facts={[]} canRead canCreate={false} /></FoundationProvider>);
  expect(screen.queryByRole('dialog',{name:'Review loan status'})).not.toBeInTheDocument();expect(screen.queryByRole('button',{name:'Continue loan status confirmation'})).not.toBeInTheDocument();
  await screen.findByText(/No loan records added yet/);expect(request.mock.calls.every(([,method])=>method==='GET')).toBe(true);
});
