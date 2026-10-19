import { FoundationProvider } from '@monergy/ui-foundation';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, it, vi } from 'vitest';
import { AccessApi, AccessApiError } from '../src/access/accessApi';
import LiabilityRecordsExperience from '../src/product/LiabilityRecordsExperience';
import type { ProductFinancialFact, ProductLiabilityDraft, ProductLiabilityRecord } from '../src/product/productApi';
const session = { tenantId: 'T001', actorId: 'A100', authenticationContextId: 'customer-session', subjectVersion: 2, expiresAt: '2027-01-01T00:00:00Z' };
const source: ProductFinancialFact = { financialFactId: 'ff-liability', factType: 'LOAN', label: 'Loan balance stated in source', value: 500000,
  currency: 'USD', effectiveDate: '2026-01-01', revision: 2, source: { sourceName: 'liability.txt', documentVersion: 'document-v2',
    recordedAt: '2026-10-01T00:00:00Z', technicalSourceFactId: 'source-liability-2', technicalProvenanceId: 'provenance-2' } };
const liability: ProductLiabilityDraft = {name:'September debt',creditor:'Example lender',outstandingAmount:400000,
  balanceAsOfDate:'2026-09-30',sourceFinancialFactId:'ff-liability',sourceRevision:2};
const record: ProductLiabilityRecord = {liabilityRecordId:'liability-saved',operationId:'a'.repeat(32),customerId:'reference-customer',liability,currency:'USD',
  sourceProvenanceId:'provenance-2',documentVersionId:'document-v2',evidenceId:'evidence-liability',recordedBy:'A100',recordedAt:'2026-10-19T00:00:00Z',verificationStatus:'CUSTOMER_REVIEWED'};
function mount(api: AccessApi, canCreate = true, canRead = true, facts: ProductFinancialFact[] = [source]) {
  return render(<FoundationProvider appearance="midnight"><LiabilityRecordsExperience api={api} canCreate={canCreate} canRead={canRead} facts={facts} /></FoundationProvider>);
}
afterEach(() => { cleanup(); vi.restoreAllMocks(); });
async function fill(user: ReturnType<typeof userEvent.setup>) {
  await user.click(await screen.findByRole('button', { name: 'Add liability record' }));
  const drawer = screen.getByRole('dialog', { name: 'Add liability record' });
  await user.click(within(drawer).getByRole('combobox', { name: 'Reviewed debt source' }));
  await user.click(screen.getByText(/Loan balance stated in source ·/, { selector: '.ant-select-item-option-content' }));
  expect(within(drawer).getByLabelText('Outstanding balance')).toHaveValue(null);
  for(const [label,value] of [['Liability name','September debt'],['Creditor','Example lender'],['Outstanding balance','400000']] as const)
    await user.type(within(drawer).getByLabelText(label),value);
  for(const [label,value] of [['Balance as of','2026-09-30']] as const) fireEvent.change(within(drawer).getByLabelText(label),{target:{value}});
  return drawer;
}
it('requires explicit reviewed terms, preserves the source revision and shows owner-confirmed readback', async () => {
  const api = new AccessApi(session); let stored = false;
  const request = vi.spyOn(api,'product').mockImplementation((path,method,body) => {
    if (path === 'v1/liability-records' && method === 'GET') return Promise.resolve({ items: stored ? [record] : [], hasMore: false, dataDurability: 'Temporary data' });
    if (path === 'v1/liability-records' && method === 'POST') { expect(body).toEqual({ operationId: 'a'.repeat(32), liability }); stored = true; return Promise.resolve(record); }
    return Promise.reject(new Error('unexpected route'));
  });
  vi.spyOn(crypto,'randomUUID').mockReturnValue('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa'); mount(api);
  const user=userEvent.setup(); const drawer=await fill(user);
  expect(request.mock.calls.filter(([,method]) => method === 'POST')).toHaveLength(0);
  expect(drawer).toHaveTextContent('Enter the outstanding balance separately.');
  await user.click(within(drawer).getByRole('button',{name:'Save reviewed liability record'}));
  expect(await screen.findByText('Liability record saved: September debt')).toBeVisible();
  await user.click(await screen.findByRole('button',{name:'View liability record: September debt'}));
  const details=screen.getByRole('dialog',{name:'Liability record details'});
  expect(details).toHaveTextContent('$400,000.00'); expect(details).toHaveTextContent('2026-09-30');
  expect(within(details).getByRole('link',{name:'View exact liability source history'})).toHaveAttribute('href','/finances?fact=ff-liability');
  expect(details).toHaveTextContent('have not been independently verified');
});
it('retries an uncertain save with exactly the same operation and reviewed terms', async () => {
  const api=new AccessApi(session); let attempts=0;
  const request=vi.spyOn(api,'product').mockImplementation((_path,method) => {
    if(method==='GET') return Promise.resolve({items:[],hasMore:false,dataDurability:'Temporary data'});
    attempts++; return attempts===1 ? Promise.reject(new AccessApiError(503,'unavailable')) : Promise.resolve(record);
  });
  vi.spyOn(crypto,'randomUUID').mockReturnValue('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa');mount(api);const user=userEvent.setup();const drawer=await fill(user);
  await user.click(within(drawer).getByRole('button',{name:'Save reviewed liability record'}));
  expect(await within(drawer).findByText(/The save has not been confirmed/)).toBeVisible();
  expect(within(drawer).getByLabelText('Outstanding balance')).toBeDisabled();
  await user.click(within(drawer).getByRole('button',{name:'Retry save confirmation'}));
  expect(await screen.findByText('Liability record saved: September debt')).toBeVisible();
  const calls=request.mock.calls.filter(([,method])=>method==='POST'); expect(calls).toHaveLength(2);expect(calls.at(0)?.at(2)).toEqual(calls.at(1)?.at(2));
});
it('leaves the form editable after a confirmed validation rejection', async () => {
  const api=new AccessApi(session);vi.spyOn(api,'product').mockImplementation((_path,method)=>method==='GET'
    ? Promise.resolve({items:[],hasMore:false,dataDurability:'Temporary data'}) : Promise.reject(new AccessApiError(400,'invalid')));
  mount(api);const user=userEvent.setup();const drawer=await fill(user);await user.click(within(drawer).getByRole('button',{name:'Save reviewed liability record'}));
  expect(await within(drawer).findByText(/Check the balance, as-of date/)).toBeVisible();expect(within(drawer).getByLabelText('Outstanding balance')).toBeEnabled();
  expect(screen.queryByText(/Liability record saved:/)).not.toBeInTheDocument();
});
it('keeps read-only records visible and hides creation without update access', async () => {
  const api=new AccessApi(session);vi.spyOn(api,'product').mockResolvedValue({items:[record],hasMore:false,dataDurability:'Temporary data'});mount(api,false);
  expect(await screen.findByRole('button',{name:'View liability record: September debt'})).toBeVisible();expect(screen.queryByRole('button',{name:'Add liability record'})).not.toBeInTheDocument();
});
it('makes no record request without document access', () => {
  const api=new AccessApi(session);const request=vi.spyOn(api,'product');mount(api,true,false);
  expect(screen.getByText(/Document access is required/)).toBeVisible();expect(request).not.toHaveBeenCalled();
});
it('requires a previously reviewed liability source and does not accept another source creditor', async () => {
  const api=new AccessApi(session);vi.spyOn(api,'product').mockResolvedValue({items:[],hasMore:false,dataDurability:'Temporary data'});mount(api,true,true,[{...source,factType:'INCOME'}]);
  expect(screen.getByRole('button',{name:'Add liability record'})).toBeDisabled();await screen.findByText(/No liability records added yet/);
});
it('shows unavailable data explicitly rather than presenting an empty liability position', async () => {
  const api=new AccessApi(session);vi.spyOn(api,'product').mockRejectedValue(new AccessApiError(503,'unavailable'));mount(api);
  expect(await screen.findByText('Liability records are unavailable. Try again.')).toBeVisible();expect(screen.queryByText(/No liability records added yet/)).not.toBeInTheDocument();
});
it('hides prior records immediately after an API/session scope change', async () => {
  const api=new AccessApi(session);vi.spyOn(api,'product').mockResolvedValue({items:[record],hasMore:false,dataDurability:'Temporary data'});const view=mount(api,false);
  await screen.findByRole('button',{name:'View liability record: September debt'});const nextApi=new AccessApi({...session,actorId:'A200'});
  vi.spyOn(nextApi,'product').mockResolvedValue({items:[],hasMore:false,dataDurability:'Temporary data'});
  view.rerender(<FoundationProvider appearance="midnight"><LiabilityRecordsExperience api={nextApi} canCreate={false} canRead facts={[source]} /></FoundationProvider>);
  expect(screen.queryByRole('button',{name:'View liability record: September debt'})).not.toBeInTheDocument();
  await waitFor(()=>expect(screen.getByText(/No liability records added yet/)).toBeVisible());
});

it('requires a new explicit source choice when its current revision changes during review', async () => {
  const api=new AccessApi(session);
  const request=vi.spyOn(api,'product').mockImplementation((_path,method)=>method==='GET'
    ? Promise.resolve({items:[],hasMore:false,dataDurability:'Temporary data'})
    : Promise.resolve({...record,liability:{...liability,sourceRevision:3}}));
  vi.spyOn(crypto,'randomUUID').mockReturnValue('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa');const view=mount(api);const user=userEvent.setup();const drawer=await fill(user);
  const changed={...source,revision:3,value:550000};
  view.rerender(<FoundationProvider appearance="midnight"><LiabilityRecordsExperience api={api} canCreate canRead facts={[changed]} /></FoundationProvider>);
  expect(within(drawer).getByText(/The source revision has changed/)).toBeVisible();
  expect(within(drawer).getByRole('button',{name:'Save reviewed liability record'})).toBeDisabled();
  expect(request.mock.calls.filter(([,method])=>method==='POST')).toHaveLength(0);
  await user.click(within(drawer).getByRole('combobox',{name:'Reviewed debt source'}));
  await user.click(screen.getByText(/Loan balance stated in source ·/, {selector:'.ant-select-item-option-content'}));
  await user.click(within(drawer).getByRole('button',{name:'Save reviewed liability record'}));
  await screen.findByText('Liability record saved: September debt');
  expect(request.mock.calls.find(([,method])=>method==='POST')?.at(2)).toEqual({operationId:'a'.repeat(32),liability:{...liability,sourceRevision:3}});
});
it('preserves an unconfirmed submission when the review drawer is closed and reopened', async () => {
  const api=new AccessApi(session);vi.spyOn(api,'product').mockImplementation((_path,method)=>method==='GET'
    ? Promise.resolve({items:[],hasMore:false,dataDurability:'Temporary data'}) : Promise.reject(new AccessApiError(503,'unavailable')));
  mount(api);const user=userEvent.setup();const drawer=await fill(user);await user.click(within(drawer).getByRole('button',{name:'Save reviewed liability record'}));
  await within(drawer).findByText(/The save has not been confirmed/);
  await user.click(within(drawer).getByRole('button',{name:'Close'}));
  await user.click(screen.getByRole('button',{name:'Continue save confirmation'}));
  expect(within(screen.getByRole('dialog',{name:'Add liability record'})).getByLabelText('Outstanding balance')).toBeDisabled();
  expect(screen.getByRole('button',{name:'Retry save confirmation'})).toBeVisible();
});


it('keeps uncertain confirmation available when the reviewed source disappears', async () => {
  const api=new AccessApi(session);let attempts=0;
  const request=vi.spyOn(api,'product').mockImplementation((_path,method)=>method==='GET'
    ?Promise.resolve({items:[],hasMore:false,dataDurability:'Temporary data'})
    :++attempts===1?Promise.reject(new AccessApiError(503,'unavailable')):Promise.resolve(record));
  vi.spyOn(crypto,'randomUUID').mockReturnValue('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa');const view=mount(api);const user=userEvent.setup();const drawer=await fill(user);
  await user.click(within(drawer).getByRole('button',{name:'Save reviewed liability record'}));await within(drawer).findByText(/The save has not been confirmed/);
  await user.click(within(drawer).getByRole('button',{name:'Close'}));
  view.rerender(<FoundationProvider appearance="midnight"><LiabilityRecordsExperience api={api} canCreate canRead facts={[]} /></FoundationProvider>);
  await user.click(screen.getByRole('button',{name:'Continue save confirmation'}));await user.click(screen.getByRole('button',{name:'Retry save confirmation'}));
  expect(await screen.findByText('Liability record saved: September debt')).toBeVisible();
  const writes=request.mock.calls.filter(([,method])=>method==='POST');expect(writes).toHaveLength(2);expect(writes[0]?.[2]).toEqual(writes[1]?.[2]);
});
it('discards a late previous-session list even when transport ignores cancellation', async () => {
  const api=new AccessApi(session);let finish:(value:unknown)=>void=()=>undefined;
  const request=vi.spyOn(api,'product').mockImplementation(()=>new Promise(resolve=>{finish=resolve;}));const view=mount(api,false);
  await waitFor(()=>{expect(request).toHaveBeenCalled();});
  const nextApi=new AccessApi({...session,actorId:'A200'});vi.spyOn(nextApi,'product').mockResolvedValue({items:[],hasMore:false,dataDurability:'Temporary data'});
  view.rerender(<FoundationProvider appearance="midnight"><LiabilityRecordsExperience api={nextApi} canCreate={false} canRead facts={[]} /></FoundationProvider>);
  await screen.findByText(/No liability records added yet/);finish({items:[record],hasMore:false,dataDurability:'Temporary data'});
  await waitFor(()=>expect(screen.queryByText('September debt')).not.toBeInTheDocument());
});
it('does not restore sensitive records after document access is removed', async () => {
  const api=new AccessApi(session);vi.spyOn(api,'product').mockResolvedValue({items:[record],hasMore:false,dataDurability:'Temporary data'});const view=mount(api,false);
  await screen.findByRole('button',{name:'View liability record: September debt'});
  view.rerender(<FoundationProvider appearance="midnight"><LiabilityRecordsExperience api={api} canCreate={false} canRead={false} facts={[source]} /></FoundationProvider>);
  expect(screen.queryByText('September debt')).not.toBeInTheDocument();expect(screen.getByText(/Document access is required/)).toBeVisible();
});

it('keeps a non-customer-reviewed confirmation unconfirmed',async()=>{
  const api=new AccessApi(session);vi.spyOn(api,'product').mockImplementation((_path,method)=>method==='GET'
    ?Promise.resolve({items:[],hasMore:false,dataDurability:'Temporary data'}):Promise.resolve({...record,verificationStatus:'VERIFIED'}));
  vi.spyOn(crypto,'randomUUID').mockReturnValue('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa');mount(api);const user=userEvent.setup();const drawer=await fill(user);
  await user.click(within(drawer).getByRole('button',{name:'Save reviewed liability record'}));
  expect(await within(drawer).findByText(/The save has not been confirmed/)).toBeVisible();expect(screen.queryByText('Liability record saved: September debt')).not.toBeInTheDocument();
});
it('rejects unsupported review status in a returned list',async()=>{
  const api=new AccessApi(session);vi.spyOn(api,'product').mockResolvedValue({items:[{...record,verificationStatus:'VERIFIED'}],hasMore:false,dataDurability:'Temporary data'});mount(api,false);
  expect(await screen.findByText('Liability records are unavailable. Try again.')).toBeVisible();expect(screen.queryByText('September debt')).not.toBeInTheDocument();
});
