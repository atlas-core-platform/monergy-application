import { FoundationProvider } from '@monergy/ui-foundation';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, it, vi } from 'vitest';
import { AccessApi, AccessApiError } from '../src/access/accessApi';
import InsuranceRecordsExperience from '../src/product/InsuranceRecordsExperience';
import type { ProductFinancialFact, ProductInsuranceDraft, ProductInsuranceRecord } from '../src/product/productApi';
const session = { tenantId: 'T001', actorId: 'A100', authenticationContextId: 'customer-session', subjectVersion: 2, expiresAt: '2027-01-01T00:00:00Z' };
const source: ProductFinancialFact = { financialFactId: 'ff-insurance', factType: 'INSURANCE', label: 'Sum assured stated in source', value: 500000,
  currency: 'USD', effectiveDate: '2026-01-01', revision: 2, source: { sourceName: 'insurance.txt', documentVersion: 'document-v2',
    recordedAt: '2026-10-01T00:00:00Z', technicalSourceFactId: 'source-insurance-2', technicalProvenanceId: 'provenance-2' } };
const insurance: ProductInsuranceDraft = { name:'Family policy', provider:'Example provider', policyType:'Life',
  sumAssured:500000, premiumAmount:400000, policyAsOfDate:'2026-10-01', maturityDate:null, sourceFinancialFactId:'ff-insurance', sourceRevision:2 };
const record: ProductInsuranceRecord = { insuranceRecordId:'insurance-saved',operationId:'a'.repeat(32),customerId:'reference-customer',insurance,
  currency:'USD',sourceProvenanceId:'provenance-2',documentVersionId:'document-v2',evidenceId:'evidence-insurance',recordedBy:'A100',recordedAt:'2026-10-19T00:00:00Z',revision:1 };
function mount(api: AccessApi, canCreate = true, canRead = true, facts: ProductFinancialFact[] = [source]) {
  return render(<FoundationProvider appearance="midnight"><InsuranceRecordsExperience api={api} canCreate={canCreate} canRead={canRead} facts={facts} /></FoundationProvider>);
}
afterEach(() => { cleanup(); vi.restoreAllMocks(); });
async function fill(user: ReturnType<typeof userEvent.setup>) {
  await user.click(await screen.findByRole('button', { name: 'Add insurance record' }));
  const drawer = screen.getByRole('dialog', { name: 'Add insurance record' });
  await user.click(within(drawer).getByRole('combobox', { name: 'Reviewed insurance source' }));
  await user.click(screen.getByText(/Sum assured stated in source ·/, { selector: '.ant-select-item-option-content' }));
  expect(within(drawer).getByLabelText('Premium amount')).toHaveValue(null);
  await user.type(within(drawer).getByLabelText('Policy name'), 'Family policy');
  await user.type(within(drawer).getByLabelText('Insurance provider'), 'Example provider');
  await user.type(within(drawer).getByLabelText('Policy type'), 'Life');
  for(const [label,value] of [['Sum assured','500000'],['Premium amount','400000']] as const)
    await user.type(within(drawer).getByLabelText(label),value);
  fireEvent.change(within(drawer).getByLabelText('Policy information as of'),{target:{value:'2026-10-01'}});
  return drawer;
}
it('requires explicit reviewed terms, preserves the source revision and shows owner-confirmed readback', async () => {
  const api = new AccessApi(session); let stored = false;
  const request = vi.spyOn(api,'product').mockImplementation((path,method,body) => {
    if (path === 'v1/insurance-records' && method === 'GET') return Promise.resolve({ items: stored ? [record] : [], hasMore: false, dataDurability: 'Temporary data' });
    if (path === 'v1/insurance-records' && method === 'POST') { expect(body).toEqual({ operationId: 'a'.repeat(32), insurance }); stored = true; return Promise.resolve(record); }
    return Promise.reject(new Error('unexpected route'));
  });
  vi.spyOn(crypto,'randomUUID').mockReturnValue('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa'); mount(api);
  const user=userEvent.setup(); const drawer=await fill(user);
  expect(request.mock.calls.filter(([,method]) => method === 'POST')).toHaveLength(0);
  expect(drawer).toHaveTextContent('Enter sum assured and premium separately.');
  await user.click(within(drawer).getByRole('button',{name:'Save reviewed insurance record'}));
  expect(await screen.findByText('Insurance record saved: Family policy')).toBeVisible();
  await user.click(await screen.findByRole('button',{name:'View insurance record: Family policy'}));
  const details=screen.getByRole('dialog',{name:'Insurance record details'});
  expect(details).toHaveTextContent('$400,000.00 as of 2026-10-01'); expect(details).toHaveTextContent('$500,000.00'); expect(details).toHaveTextContent('Life');
  expect(within(details).getByRole('link',{name:'View exact insurance source history'})).toHaveAttribute('href','/finances?fact=ff-insurance');
  expect(details).toHaveTextContent('have not been verified');
});
it('retries an uncertain save with exactly the same operation and reviewed terms', async () => {
  const api=new AccessApi(session); let attempts=0;
  const request=vi.spyOn(api,'product').mockImplementation((_path,method) => {
    if(method==='GET') return Promise.resolve({items:[],hasMore:false,dataDurability:'Temporary data'});
    attempts++; return attempts===1 ? Promise.reject(new AccessApiError(503,'unavailable')) : Promise.resolve(record);
  });
  vi.spyOn(crypto,'randomUUID').mockReturnValue('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa');mount(api);const user=userEvent.setup();const drawer=await fill(user);
  await user.click(within(drawer).getByRole('button',{name:'Save reviewed insurance record'}));
  expect(await within(drawer).findByText(/The save has not been confirmed/)).toBeVisible();
  expect(within(drawer).getByLabelText('Premium amount')).toBeDisabled();
  await user.click(within(drawer).getByRole('button',{name:'Retry save confirmation'}));
  expect(await screen.findByText('Insurance record saved: Family policy')).toBeVisible();
  const calls=request.mock.calls.filter(([,method])=>method==='POST'); expect(calls).toHaveLength(2);expect(calls.at(0)?.at(2)).toEqual(calls.at(1)?.at(2));
});
it('leaves the form editable after a confirmed validation rejection', async () => {
  const api=new AccessApi(session);vi.spyOn(api,'product').mockImplementation((_path,method)=>method==='GET'
    ? Promise.resolve({items:[],hasMore:false,dataDurability:'Temporary data'}) : Promise.reject(new AccessApiError(400,'invalid')));
  mount(api);const user=userEvent.setup();const drawer=await fill(user);await user.click(within(drawer).getByRole('button',{name:'Save reviewed insurance record'}));
  expect(await within(drawer).findByText(/Check the required amounts/)).toBeVisible();expect(within(drawer).getByLabelText('Premium amount')).toBeEnabled();
  expect(screen.queryByText(/Insurance record saved:/)).not.toBeInTheDocument();
});
it('keeps read-only records visible and hides creation without update access', async () => {
  const api=new AccessApi(session);vi.spyOn(api,'product').mockResolvedValue({items:[record],hasMore:false,dataDurability:'Temporary data'});mount(api,false);
  expect(await screen.findByRole('button',{name:'View insurance record: Family policy'})).toBeVisible();expect(screen.queryByRole('button',{name:'Add insurance record'})).not.toBeInTheDocument();
});
it('makes no record request without document access', () => {
  const api=new AccessApi(session);const request=vi.spyOn(api,'product');mount(api,true,false);
  expect(screen.getByText(/Document access is required/)).toBeVisible();expect(request).not.toHaveBeenCalled();
});
it('requires a previously reviewed insurance source and does not accept another source category', async () => {
  const api=new AccessApi(session);vi.spyOn(api,'product').mockResolvedValue({items:[],hasMore:false,dataDurability:'Temporary data'});mount(api,true,true,[{...source,factType:'INCOME'}]);
  expect(screen.getByRole('button',{name:'Add insurance record'})).toBeDisabled();await screen.findByText(/No insurance policies added yet/);
});
it('shows unavailable data explicitly rather than presenting an empty insurance position', async () => {
  const api=new AccessApi(session);vi.spyOn(api,'product').mockRejectedValue(new AccessApiError(503,'unavailable'));mount(api);
  expect(await screen.findByText('Insurance policies are unavailable. Try again.')).toBeVisible();expect(screen.queryByText(/No insurance policies added yet/)).not.toBeInTheDocument();
});
it('hides prior records immediately after an API/session scope change', async () => {
  const api=new AccessApi(session);vi.spyOn(api,'product').mockResolvedValue({items:[record],hasMore:false,dataDurability:'Temporary data'});const view=mount(api,false);
  await screen.findByRole('button',{name:'View insurance record: Family policy'});const nextApi=new AccessApi({...session,actorId:'A200'});
  vi.spyOn(nextApi,'product').mockResolvedValue({items:[],hasMore:false,dataDurability:'Temporary data'});
  view.rerender(<FoundationProvider appearance="midnight"><InsuranceRecordsExperience api={nextApi} canCreate={false} canRead facts={[source]} /></FoundationProvider>);
  expect(screen.queryByRole('button',{name:'View insurance record: Family policy'})).not.toBeInTheDocument();
  await waitFor(()=>expect(screen.getByText(/No insurance policies added yet/)).toBeVisible());
});

it('requires a new explicit source choice when its current revision changes during review', async () => {
  const api=new AccessApi(session);
  const request=vi.spyOn(api,'product').mockImplementation((_path,method)=>method==='GET'
    ? Promise.resolve({items:[],hasMore:false,dataDurability:'Temporary data'})
    : Promise.resolve({...record,insurance:{...insurance,sourceRevision:3}}));
  vi.spyOn(crypto,'randomUUID').mockReturnValue('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa');const view=mount(api);const user=userEvent.setup();const drawer=await fill(user);
  const changed={...source,revision:3,value:550000};
  view.rerender(<FoundationProvider appearance="midnight"><InsuranceRecordsExperience api={api} canCreate canRead facts={[changed]} /></FoundationProvider>);
  expect(within(drawer).getByText(/The source revision has changed/)).toBeVisible();
  expect(within(drawer).getByRole('button',{name:'Save reviewed insurance record'})).toBeDisabled();
  expect(request.mock.calls.filter(([,method])=>method==='POST')).toHaveLength(0);
  await user.click(within(drawer).getByRole('combobox',{name:'Reviewed insurance source'}));
  await user.click(screen.getByText(/Sum assured stated in source ·/, {selector:'.ant-select-item-option-content'}));
  await user.click(within(drawer).getByRole('button',{name:'Save reviewed insurance record'}));
  await screen.findByText('Insurance record saved: Family policy');
  expect(request.mock.calls.find(([,method])=>method==='POST')?.at(2)).toEqual({operationId:'a'.repeat(32),insurance:{...insurance,sourceRevision:3}});
});
it('preserves an unconfirmed submission when the review drawer is closed and reopened', async () => {
  const api=new AccessApi(session);vi.spyOn(api,'product').mockImplementation((_path,method)=>method==='GET'
    ? Promise.resolve({items:[],hasMore:false,dataDurability:'Temporary data'}) : Promise.reject(new AccessApiError(503,'unavailable')));
  mount(api);const user=userEvent.setup();const drawer=await fill(user);await user.click(within(drawer).getByRole('button',{name:'Save reviewed insurance record'}));
  await within(drawer).findByText(/The save has not been confirmed/);
  await user.click(within(drawer).getByRole('button',{name:'Close'}));
  await user.click(screen.getByRole('button',{name:'Continue save confirmation'}));
  expect(within(screen.getByRole('dialog',{name:'Add insurance record'})).getByLabelText('Premium amount')).toBeDisabled();
  expect(screen.getByRole('button',{name:'Retry save confirmation'})).toBeVisible();
});


it('keeps uncertain confirmation available when the reviewed source disappears', async () => {
  const api=new AccessApi(session);let attempts=0;
  const request=vi.spyOn(api,'product').mockImplementation((_path,method)=>method==='GET'
    ?Promise.resolve({items:[],hasMore:false,dataDurability:'Temporary data'})
    :++attempts===1?Promise.reject(new AccessApiError(503,'unavailable')):Promise.resolve(record));
  vi.spyOn(crypto,'randomUUID').mockReturnValue('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa');const view=mount(api);const user=userEvent.setup();const drawer=await fill(user);
  await user.click(within(drawer).getByRole('button',{name:'Save reviewed insurance record'}));await within(drawer).findByText(/The save has not been confirmed/);
  await user.click(within(drawer).getByRole('button',{name:'Close'}));
  view.rerender(<FoundationProvider appearance="midnight"><InsuranceRecordsExperience api={api} canCreate canRead facts={[]} /></FoundationProvider>);
  await user.click(screen.getByRole('button',{name:'Continue save confirmation'}));await user.click(screen.getByRole('button',{name:'Retry save confirmation'}));
  expect(await screen.findByText('Insurance record saved: Family policy')).toBeVisible();
  const writes=request.mock.calls.filter(([,method])=>method==='POST');expect(writes).toHaveLength(2);expect(writes[0]?.[2]).toEqual(writes[1]?.[2]);
});
it('discards a late previous-session list even when transport ignores cancellation', async () => {
  const api=new AccessApi(session);let finish:(value:unknown)=>void=()=>undefined;
  const request=vi.spyOn(api,'product').mockImplementation(()=>new Promise(resolve=>{finish=resolve;}));const view=mount(api,false);
  await waitFor(()=>{expect(request).toHaveBeenCalled();});
  const nextApi=new AccessApi({...session,actorId:'A200'});vi.spyOn(nextApi,'product').mockResolvedValue({items:[],hasMore:false,dataDurability:'Temporary data'});
  view.rerender(<FoundationProvider appearance="midnight"><InsuranceRecordsExperience api={nextApi} canCreate={false} canRead facts={[]} /></FoundationProvider>);
  await screen.findByText(/No insurance policies added yet/);finish({items:[record],hasMore:false,dataDurability:'Temporary data'});
  await waitFor(()=>expect(screen.queryByText('Family policy')).not.toBeInTheDocument());
});
it('does not restore sensitive records after document access is removed', async () => {
  const api=new AccessApi(session);vi.spyOn(api,'product').mockResolvedValue({items:[record],hasMore:false,dataDurability:'Temporary data'});const view=mount(api,false);
  await screen.findByRole('button',{name:'View insurance record: Family policy'});
  view.rerender(<FoundationProvider appearance="midnight"><InsuranceRecordsExperience api={api} canCreate={false} canRead={false} facts={[source]} /></FoundationProvider>);
  expect(screen.queryByText('Family policy')).not.toBeInTheDocument();expect(screen.getByText(/Document access is required/)).toBeVisible();
});

it.each(['ASSET','INVESTMENT','TAX'] as const)('rejects %s as an insurance-policy source',async(kind)=>{
 const api=new AccessApi(session);vi.spyOn(api,'product').mockResolvedValue({items:[],hasMore:false,dataDurability:'Temporary data'});mount(api,true,true,[{...source,factType:kind}]);expect(await screen.findByRole('button',{name:'Add insurance record'})).toBeDisabled();
});

it('edits the saved terms explicitly using the exact record version and immutable historical source', async () => {
  const api=new AccessApi(session);let current=record;
  const corrected={...record,revision:2,insurance:{...insurance,premiumAmount:390000}};
  const request=vi.spyOn(api,'product').mockImplementation((path,method,body)=>{
    if(method==='GET') return Promise.resolve({items:[current],hasMore:false,dataDurability:'Temporary data'});
    expect(path).toBe('v1/insurance-records/insurance-saved/corrections');
    expect(body).toEqual({operationId:'a'.repeat(32),expectedRevision:1,insurance:corrected.insurance});current=corrected;return Promise.resolve(corrected);
  });
  vi.spyOn(crypto,'randomUUID').mockReturnValue('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa');mount(api,true,true,[]);
  const user=userEvent.setup();await user.click(await screen.findByRole('button',{name:'Edit insurance record: Family policy'}));
  const drawer=screen.getByRole('dialog',{name:'Edit insurance record'});
  expect(within(drawer).getByText(/Editing version 1 · USD · Source revision 2/)).toBeVisible();
  expect(within(drawer).queryByRole('combobox',{name:'Reviewed insurance source'})).not.toBeInTheDocument();
  expect(within(drawer).getByLabelText('Premium amount')).toHaveValue(400000);
  fireEvent.change(within(drawer).getByLabelText('Premium amount'),{target:{value:'390000'}});
  expect(request.mock.calls.filter(([,method])=>method==='POST')).toHaveLength(0);
  await user.click(within(drawer).getByRole('button',{name:'Save reviewed corrections'}));
  await screen.findByText('Insurance record saved: Family policy');expect(screen.getByText(/Premium amount: \$390,000.00/)).toBeVisible();
});

it('blocks a stale correction and requires review of the refreshed version', async () => {
  const api=new AccessApi(session);let current=record;
  const request=vi.spyOn(api,'product').mockImplementation((_path,method)=>method==='GET'
    ? Promise.resolve({items:[current],hasMore:false,dataDurability:'Temporary data'})
    : Promise.reject(new AccessApiError(409,'INSURANCE_POLICY_REVISION_CONFLICT')));
  mount(api);const user=userEvent.setup();await user.click(await screen.findByRole('button',{name:'Edit insurance record: Family policy'}));
  const drawer=screen.getByRole('dialog',{name:'Edit insurance record'});await user.click(within(drawer).getByRole('button',{name:'Save reviewed corrections'}));
  expect(await within(drawer).findByText(/A newer version has been saved/)).toBeVisible();
  expect(within(drawer).getByRole('button',{name:'Save reviewed corrections'})).toBeDisabled();expect(within(drawer).getByLabelText('Premium amount')).toBeDisabled();
  await user.click(within(drawer).getByRole('button',{name:'Close'}));current={...record,revision:2,insurance:{...insurance,premiumAmount:380000}};
  await user.click(screen.getByRole('button',{name:'Refresh insurance policies'}));await screen.findByText(/Premium amount: \$380,000.00/);
  await user.click(screen.getByRole('button',{name:'Edit insurance record: Family policy'}));
  expect(within(screen.getByRole('dialog',{name:'Edit insurance record'})).getByText(/Editing version 2/)).toBeVisible();
  expect(request.mock.calls.filter(([,method])=>method==='POST')).toHaveLength(1);
});

it('retains an uncertain correction and its expected version after closing with no current source figures', async () => {
  const api=new AccessApi(session);let attempts=0;
  const request=vi.spyOn(api,'product').mockImplementation((_path,method)=>{
    if(method==='GET')return Promise.resolve({items:[record],hasMore:false,dataDurability:'Temporary data'});
    attempts++;return attempts===1?Promise.reject(new AccessApiError(503,'unavailable')):Promise.resolve({...record,revision:2});
  });
  vi.spyOn(crypto,'randomUUID').mockReturnValue('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa');mount(api,true,true,[]);
  const user=userEvent.setup();await user.click(await screen.findByRole('button',{name:'Edit insurance record: Family policy'}));
  const drawer=screen.getByRole('dialog',{name:'Edit insurance record'});await user.click(within(drawer).getByRole('button',{name:'Save reviewed corrections'}));
  await within(drawer).findByText(/The save has not been confirmed/);expect(within(drawer).getByLabelText('Insurance provider')).toBeDisabled();
  await user.click(within(drawer).getByRole('button',{name:'Close'}));await user.click(screen.getByRole('button',{name:'Continue save confirmation'}));
  const reopened=screen.getByRole('dialog',{name:'Edit insurance record'});await user.click(within(reopened).getByRole('button',{name:'Retry save confirmation'}));
  await screen.findByText('Insurance record saved: Family policy');const calls=request.mock.calls.filter(([,method])=>method==='POST');
  expect(calls).toHaveLength(2);expect(calls[0]?.[2]).toEqual(calls[1]?.[2]);expect(calls[0]?.[2]).toMatchObject({expectedRevision:1});
});

it('allows correction after confirmed validation rejection and keeps uncertain conflicts frozen', async () => {
  const api=new AccessApi(session);let conflict=false;
  vi.spyOn(api,'product').mockImplementation((_path,method)=>method==='GET'
    ?Promise.resolve({items:[record],hasMore:false,dataDurability:'Temporary data'})
    :Promise.reject(new AccessApiError(conflict?409:400,conflict?'INSURANCE_POLICY_OPERATION_CONFLICT':'invalid')));
  mount(api);const user=userEvent.setup();await user.click(await screen.findByRole('button',{name:'Edit insurance record: Family policy'}));
  const drawer=screen.getByRole('dialog',{name:'Edit insurance record'});await user.click(within(drawer).getByRole('button',{name:'Save reviewed corrections'}));
  await within(drawer).findByText(/Check the required amounts/);expect(within(drawer).getByLabelText('Insurance provider')).toBeEnabled();
  conflict=true;await user.click(within(drawer).getByRole('button',{name:'Save reviewed corrections'}));
  await within(drawer).findByText(/The save has not been confirmed/);expect(within(drawer).getByLabelText('Insurance provider')).toBeDisabled();
});

it('reads original and corrected terms in bounded history pages without enabling edits for read-only access', async () => {
  const api=new AccessApi(session);const latest={...record,revision:51,insurance:{...insurance,premiumAmount:300000}};
  const request=vi.spyOn(api,'product').mockImplementation((path)=>{
    if(path==='v1/insurance-records')return Promise.resolve({items:[latest],hasMore:false,dataDurability:'Temporary data'});
    if(path==='v1/insurance-records/insurance-saved/history')return Promise.resolve({items:[latest],hasMore:true,nextBeforeRevision:2});
    if(path==='v1/insurance-records/insurance-saved/history?beforeRevision=2')return Promise.resolve({items:[record],hasMore:false,nextBeforeRevision:null});
    return Promise.reject(new Error('unexpected route'));
  });mount(api,false);const user=userEvent.setup();await user.click(await screen.findByRole('button',{name:'View insurance history: Family policy'}));
  expect(screen.queryByRole('button',{name:'Edit insurance record: Family policy'})).not.toBeInTheDocument();
  const drawer=screen.getByRole('dialog',{name:'Insurance record history'});await user.click(await within(drawer).findByRole('button',{name:'Earlier versions'}));
  await user.click(await within(drawer).findByRole('button',{name:'View version 1'}));
  const version=within(drawer).getByRole('region',{name:'Saved insurance version 1'});expect(version).toHaveTextContent('$400,000.00 as of 2026-10-01');
  expect(version).toHaveTextContent('$500,000.00');expect(within(version).getByRole('link',{name:'View exact insurance source history'})).toHaveAttribute('href','/finances?fact=ff-insurance');
  await user.click(within(drawer).getByRole('button',{name:'Latest versions'}));await within(drawer).findByRole('button',{name:'View version 51'});
  expect(request.mock.calls.every(([,method])=>method==='GET')).toBe(true);
});

it('shows history failure and hides prior history immediately after a session scope change', async () => {
  const api=new AccessApi(session);vi.spyOn(api,'product').mockImplementation(path=>path==='v1/insurance-records'
    ?Promise.resolve({items:[record],hasMore:false,dataDurability:'Temporary data'}):Promise.reject(new AccessApiError(403,'denied')));
  const view=mount(api);const user=userEvent.setup();await user.click(await screen.findByRole('button',{name:'View insurance history: Family policy'}));
  await screen.findByText('Insurance history is unavailable.');expect(screen.queryByRole('button',{name:'View version 1'})).not.toBeInTheDocument();
  const other=new AccessApi({...session,actorId:'A200'});vi.spyOn(other,'product').mockResolvedValue({items:[],hasMore:false,dataDurability:'Temporary data'});
  view.rerender(<FoundationProvider appearance="midnight"><InsuranceRecordsExperience api={other} facts={[]} canRead canCreate={false} /></FoundationProvider>);
  expect(screen.queryByRole('dialog',{name:'Insurance record history'})).not.toBeInTheDocument();await screen.findByText(/No insurance policies added yet/);
});

it('aborts pending history and rejects a late previous-session response',async()=>{
  const api=new AccessApi(session);let complete:(value:unknown)=>void=()=>undefined;
  const request=vi.spyOn(api,'product').mockImplementation(path=>path==='v1/insurance-records'
    ?Promise.resolve({items:[record],hasMore:false,dataDurability:'Temporary data'})
    :new Promise(resolve=>{complete=resolve;}));
  const view=mount(api);const user=userEvent.setup();await user.click(await screen.findByRole('button',{name:'View insurance history: Family policy'}));
  await screen.findByText('Loading insurance history…');
  const signal=request.mock.calls.find(([path])=>path.endsWith('/history'))?.[3];expect(signal).toBeDefined();
  const next=new AccessApi({...session,actorId:'A200'});vi.spyOn(next,'product').mockResolvedValue({items:[],hasMore:false,dataDurability:'Temporary data'});
  view.rerender(<FoundationProvider appearance="midnight"><InsuranceRecordsExperience api={next} facts={[]} canRead canCreate={false} /></FoundationProvider>);
  expect(signal?.aborted).toBe(true);complete({items:[record],hasMore:false,nextBeforeRevision:null});
  await screen.findByText(/No insurance policies added yet/);expect(screen.queryByRole('dialog',{name:'Insurance record history'})).not.toBeInTheDocument();
});

it.each(['source','customer'])('rejects insurance history with changed %s identity',async(variation)=>{
  const api=new AccessApi(session);const changed=variation==='source'?{...record,insurance:{...insurance,sourceRevision:99}}:{...record,customerId:'foreign'};
  vi.spyOn(api,'product').mockImplementation(path=>path==='v1/insurance-records'
    ?Promise.resolve({items:[record],hasMore:false,dataDurability:'Temporary data'})
    :Promise.resolve({items:[changed],hasMore:false,nextBeforeRevision:null}));
  mount(api,false);const user=userEvent.setup();await user.click(await screen.findByRole('button',{name:'View insurance history: Family policy'}));
  expect(await screen.findByText('Insurance history is unavailable.')).toBeVisible();expect(screen.queryByRole('button',{name:'View version 1'})).not.toBeInTheDocument();
});

it.each(['2050-12-31','2020-01-01'])('saves explicit maturity %s without treating it as active coverage',async(maturity)=>{
 const api=new AccessApi(session);const request=vi.spyOn(api,'product').mockImplementation((_path,method,body)=>method==='POST'?Promise.resolve({...record,operationId:(body as {operationId:string}).operationId,insurance:(body as {insurance:ProductInsuranceDraft}).insurance}):Promise.resolve({items:[],hasMore:false,dataDurability:'Temporary data'}));
 mount(api);const user=userEvent.setup();await fill(user);const drawer=screen.getByRole('dialog',{name:'Add insurance record'});fireEvent.change(within(drawer).getByLabelText('Maturity or expiry date (optional)'),{target:{value:maturity}});await user.click(within(drawer).getByRole('button',{name:'Save reviewed insurance record'}));
 await screen.findByText('Insurance record saved: Family policy');const call=request.mock.calls.find(([,method])=>method==='POST');expect((call?.[2] as {insurance:ProductInsuranceDraft}).insurance.maturityDate).toBe(maturity);
});
it('edits unknown maturity as blank,can add it and clear it without source figures',async()=>{
 const api=new AccessApi(session);let latest={...record};vi.spyOn(api,'product').mockImplementation((_path,method,body)=>{if(method==='POST'){const input=body as {operationId:string;expectedRevision:number;insurance:ProductInsuranceDraft};latest={...latest,...input,revision:input.expectedRevision+1};return Promise.resolve(latest);}return Promise.resolve({items:[latest],hasMore:false,dataDurability:'Temporary data'});});
 mount(api,true,true,[]);const user=userEvent.setup();await user.click(await screen.findByRole('button',{name:'Edit insurance record: Family policy'}));let drawer=screen.getByRole('dialog',{name:'Edit insurance record'});expect(within(drawer).getByLabelText('Maturity or expiry date (optional)')).toHaveValue('');fireEvent.change(within(drawer).getByLabelText('Maturity or expiry date (optional)'),{target:{value:'2050-01-01'}});await user.click(within(drawer).getByRole('button',{name:'Save reviewed corrections'}));await screen.findByText('Insurance record saved: Family policy');expect(latest.insurance.maturityDate).toBe('2050-01-01');
 await user.click(await screen.findByRole('button',{name:'Edit insurance record: Family policy'}));drawer=screen.getByRole('dialog',{name:'Edit insurance record'});fireEvent.change(within(drawer).getByLabelText('Maturity or expiry date (optional)'),{target:{value:''}});await user.click(within(drawer).getByRole('button',{name:'Save reviewed corrections'}));await screen.findByText('Insurance record saved: Family policy');expect(latest.insurance.maturityDate).toBeNull();
});
