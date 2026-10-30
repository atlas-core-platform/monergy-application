import { FoundationProvider } from '@monergy/ui-foundation';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, it, vi } from 'vitest';
import { AccessApi, AccessApiError } from '../src/access/accessApi';
import VehicleRecordsExperience from '../src/product/VehicleRecordsExperience';
import type { ProductFinancialFact, ProductVehicleDraft, ProductVehicleRecord } from '../src/product/productApi';
const session = { tenantId: 'T001', actorId: 'A100', authenticationContextId: 'customer-session', subjectVersion: 2, expiresAt: '2027-01-01T00:00:00Z' };
const source: ProductFinancialFact = { financialFactId: 'ff-vehicle', factType: 'ASSET', assetType: 'VEHICLE', label: 'Purchase value stated in source', value: 500000,
  currency: 'USD', effectiveDate: '2026-01-01', revision: 2, source: { sourceName: 'vehicle.txt', documentVersion: 'document-v2',
    recordedAt: '2026-10-01T00:00:00Z', technicalSourceFactId: 'source-vehicle-2', technicalProvenanceId: 'provenance-2' } };
const vehicle: ProductVehicleDraft = { name:'Family car', registrationNumber:'TS 09 AB 1234', vehicleType:'Car',
  purchaseValue:500000, currentValue:400000, valueAsOfDate:'2026-10-01', sourceFinancialFactId:'ff-vehicle', sourceRevision:2 };
const record: ProductVehicleRecord = { vehicleRecordId:'vehicle-saved',operationId:'a'.repeat(32),customerId:'reference-customer',vehicle,
  currency:'USD',sourceProvenanceId:'provenance-2',documentVersionId:'document-v2',evidenceId:'evidence-vehicle',recordedBy:'A100',recordedAt:'2026-10-19T00:00:00Z',revision:1,
  ownershipStatus:'UNREVIEWED',ownershipAsOfDate:null };
function mount(api: AccessApi, canCreate = true, canRead = true, facts: ProductFinancialFact[] = [source]) {
  return render(<FoundationProvider appearance="midnight"><VehicleRecordsExperience api={api} canCreate={canCreate} canRead={canRead} facts={facts} /></FoundationProvider>);
}
afterEach(() => { cleanup(); vi.restoreAllMocks(); });
async function fill(user: ReturnType<typeof userEvent.setup>) {
  await user.click(await screen.findByRole('button', { name: 'Add vehicle record' }));
  const drawer = screen.getByRole('dialog', { name: 'Add vehicle record' });
  await user.click(within(drawer).getByRole('combobox', { name: 'Reviewed vehicle source' }));
  await user.click(screen.getByText(/Purchase value stated in source ·/, { selector: '.ant-select-item-option-content' }));
  expect(within(drawer).getByLabelText('Current value')).toHaveValue(null);
  await user.type(within(drawer).getByLabelText('Vehicle name'), 'Family car');
  await user.type(within(drawer).getByLabelText('Registration reference (optional)'), 'TS 09 AB 1234');
  await user.type(within(drawer).getByLabelText('Vehicle type'), 'Car');
  for(const [label,value] of [['Purchase value','500000'],['Current value','400000']] as const)
    await user.type(within(drawer).getByLabelText(label),value);
  fireEvent.change(within(drawer).getByLabelText('Value as of'),{target:{value:'2026-10-01'}});
  return drawer;
}
it('requires explicit reviewed terms, preserves the source revision and shows owner-confirmed readback', async () => {
  const api = new AccessApi(session); let stored = false;
  const request = vi.spyOn(api,'product').mockImplementation((path,method,body) => {
    if (path === 'v1/vehicle-records' && method === 'GET') return Promise.resolve({ items: stored ? [record] : [], hasMore: false, dataDurability: 'Temporary data' });
    if (path === 'v1/vehicle-records' && method === 'POST') { expect(body).toEqual({ operationId: 'a'.repeat(32), vehicle }); stored = true; return Promise.resolve(record); }
    return Promise.reject(new Error('unexpected route'));
  });
  vi.spyOn(crypto,'randomUUID').mockReturnValue('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa'); mount(api);
  const user=userEvent.setup(); const drawer=await fill(user);
  expect(request.mock.calls.filter(([,method]) => method === 'POST')).toHaveLength(0);
  expect(drawer).toHaveTextContent('Enter purchase and current values separately.');
  await user.click(within(drawer).getByRole('button',{name:'Save reviewed vehicle record'}));
  expect(await screen.findByText('Vehicle record saved: Family car')).toBeVisible();
  await user.click(await screen.findByRole('button',{name:'View vehicle record: Family car'}));
  const details=screen.getByRole('dialog',{name:'Vehicle record details'});
  expect(details).toHaveTextContent('$400,000.00 as of 2026-10-01'); expect(details).toHaveTextContent('$500,000.00'); expect(details).toHaveTextContent('Car');
  expect(within(details).getByRole('link',{name:'View exact vehicle source history'})).toHaveAttribute('href','/finances?fact=ff-vehicle');
  expect(details).toHaveTextContent('have not been independently verified');
});
it('retries an uncertain save with exactly the same operation and reviewed terms', async () => {
  const api=new AccessApi(session); let attempts=0;
  const request=vi.spyOn(api,'product').mockImplementation((_path,method) => {
    if(method==='GET') return Promise.resolve({items:[],hasMore:false,dataDurability:'Temporary data'});
    attempts++; return attempts===1 ? Promise.reject(new AccessApiError(503,'unavailable')) : Promise.resolve(record);
  });
  vi.spyOn(crypto,'randomUUID').mockReturnValue('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa');mount(api);const user=userEvent.setup();const drawer=await fill(user);
  await user.click(within(drawer).getByRole('button',{name:'Save reviewed vehicle record'}));
  expect(await within(drawer).findByText(/The save has not been confirmed/)).toBeVisible();
  expect(within(drawer).getByLabelText('Current value')).toBeDisabled();
  await user.click(within(drawer).getByRole('button',{name:'Retry save confirmation'}));
  expect(await screen.findByText('Vehicle record saved: Family car')).toBeVisible();
  const calls=request.mock.calls.filter(([,method])=>method==='POST'); expect(calls).toHaveLength(2);expect(calls.at(0)?.at(2)).toEqual(calls.at(1)?.at(2));
});
it('leaves the form editable after a confirmed validation rejection', async () => {
  const api=new AccessApi(session);vi.spyOn(api,'product').mockImplementation((_path,method)=>method==='GET'
    ? Promise.resolve({items:[],hasMore:false,dataDurability:'Temporary data'}) : Promise.reject(new AccessApiError(400,'invalid')));
  mount(api);const user=userEvent.setup();const drawer=await fill(user);await user.click(within(drawer).getByRole('button',{name:'Save reviewed vehicle record'}));
  expect(await within(drawer).findByText(/Check the required amounts/)).toBeVisible();expect(within(drawer).getByLabelText('Current value')).toBeEnabled();
  expect(screen.queryByText(/Vehicle record saved:/)).not.toBeInTheDocument();
});
it('keeps read-only records visible and hides creation without update access', async () => {
  const api=new AccessApi(session);vi.spyOn(api,'product').mockResolvedValue({items:[record],hasMore:false,dataDurability:'Temporary data'});mount(api,false);
  expect(await screen.findByRole('button',{name:'View vehicle record: Family car'})).toBeVisible();expect(screen.queryByRole('button',{name:'Add vehicle record'})).not.toBeInTheDocument();
});
it('makes no record request without document access', () => {
  const api=new AccessApi(session);const request=vi.spyOn(api,'product');mount(api,true,false);
  expect(screen.getByText(/Document access is required/)).toBeVisible();expect(request).not.toHaveBeenCalled();
});
it('requires a previously reviewed vehicle source and does not accept another source category', async () => {
  const api=new AccessApi(session);vi.spyOn(api,'product').mockResolvedValue({items:[],hasMore:false,dataDurability:'Temporary data'});mount(api,true,true,[{...source,factType:'INCOME'}]);
  expect(screen.getByRole('button',{name:'Add vehicle record'})).toBeDisabled();await screen.findByText(/No vehicle records added yet/);
});
it('shows unavailable data explicitly rather than presenting an empty vehicle position', async () => {
  const api=new AccessApi(session);vi.spyOn(api,'product').mockRejectedValue(new AccessApiError(503,'unavailable'));mount(api);
  expect(await screen.findByText('Vehicle records are unavailable. Try again.')).toBeVisible();expect(screen.queryByText(/No vehicle records added yet/)).not.toBeInTheDocument();
});
it('hides prior records immediately after an API/session scope change', async () => {
  const api=new AccessApi(session);vi.spyOn(api,'product').mockResolvedValue({items:[record],hasMore:false,dataDurability:'Temporary data'});const view=mount(api,false);
  await screen.findByRole('button',{name:'View vehicle record: Family car'});const nextApi=new AccessApi({...session,actorId:'A200'});
  vi.spyOn(nextApi,'product').mockResolvedValue({items:[],hasMore:false,dataDurability:'Temporary data'});
  view.rerender(<FoundationProvider appearance="midnight"><VehicleRecordsExperience api={nextApi} canCreate={false} canRead facts={[source]} /></FoundationProvider>);
  expect(screen.queryByRole('button',{name:'View vehicle record: Family car'})).not.toBeInTheDocument();
  await waitFor(()=>expect(screen.getByText(/No vehicle records added yet/)).toBeVisible());
});

it('requires a new explicit source choice when its current revision changes during review', async () => {
  const api=new AccessApi(session);
  const request=vi.spyOn(api,'product').mockImplementation((_path,method)=>method==='GET'
    ? Promise.resolve({items:[],hasMore:false,dataDurability:'Temporary data'})
    : Promise.resolve({...record,vehicle:{...vehicle,sourceRevision:3}}));
  vi.spyOn(crypto,'randomUUID').mockReturnValue('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa');const view=mount(api);const user=userEvent.setup();const drawer=await fill(user);
  const changed={...source,revision:3,value:550000};
  view.rerender(<FoundationProvider appearance="midnight"><VehicleRecordsExperience api={api} canCreate canRead facts={[changed]} /></FoundationProvider>);
  expect(within(drawer).getByText(/The source revision has changed/)).toBeVisible();
  expect(within(drawer).getByRole('button',{name:'Save reviewed vehicle record'})).toBeDisabled();
  expect(request.mock.calls.filter(([,method])=>method==='POST')).toHaveLength(0);
  await user.click(within(drawer).getByRole('combobox',{name:'Reviewed vehicle source'}));
  await user.click(screen.getByText(/Purchase value stated in source ·/, {selector:'.ant-select-item-option-content'}));
  await user.click(within(drawer).getByRole('button',{name:'Save reviewed vehicle record'}));
  await screen.findByText('Vehicle record saved: Family car');
  expect(request.mock.calls.find(([,method])=>method==='POST')?.at(2)).toEqual({operationId:'a'.repeat(32),vehicle:{...vehicle,sourceRevision:3}});
});
it('preserves an unconfirmed submission when the review drawer is closed and reopened', async () => {
  const api=new AccessApi(session);vi.spyOn(api,'product').mockImplementation((_path,method)=>method==='GET'
    ? Promise.resolve({items:[],hasMore:false,dataDurability:'Temporary data'}) : Promise.reject(new AccessApiError(503,'unavailable')));
  mount(api);const user=userEvent.setup();const drawer=await fill(user);await user.click(within(drawer).getByRole('button',{name:'Save reviewed vehicle record'}));
  await within(drawer).findByText(/The save has not been confirmed/);
  await user.click(within(drawer).getByRole('button',{name:'Close'}));
  await user.click(screen.getByRole('button',{name:'Continue save confirmation'}));
  expect(within(screen.getByRole('dialog',{name:'Add vehicle record'})).getByLabelText('Current value')).toBeDisabled();
  expect(screen.getByRole('button',{name:'Retry save confirmation'})).toBeVisible();
});


it('keeps uncertain confirmation available when the reviewed source disappears', async () => {
  const api=new AccessApi(session);let attempts=0;
  const request=vi.spyOn(api,'product').mockImplementation((_path,method)=>method==='GET'
    ?Promise.resolve({items:[],hasMore:false,dataDurability:'Temporary data'})
    :++attempts===1?Promise.reject(new AccessApiError(503,'unavailable')):Promise.resolve(record));
  vi.spyOn(crypto,'randomUUID').mockReturnValue('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa');const view=mount(api);const user=userEvent.setup();const drawer=await fill(user);
  await user.click(within(drawer).getByRole('button',{name:'Save reviewed vehicle record'}));await within(drawer).findByText(/The save has not been confirmed/);
  await user.click(within(drawer).getByRole('button',{name:'Close'}));
  view.rerender(<FoundationProvider appearance="midnight"><VehicleRecordsExperience api={api} canCreate canRead facts={[]} /></FoundationProvider>);
  await user.click(screen.getByRole('button',{name:'Continue save confirmation'}));await user.click(screen.getByRole('button',{name:'Retry save confirmation'}));
  expect(await screen.findByText('Vehicle record saved: Family car')).toBeVisible();
  const writes=request.mock.calls.filter(([,method])=>method==='POST');expect(writes).toHaveLength(2);expect(writes[0]?.[2]).toEqual(writes[1]?.[2]);
});
it('discards a late previous-session list even when transport ignores cancellation', async () => {
  const api=new AccessApi(session);let finish:(value:unknown)=>void=()=>undefined;
  const request=vi.spyOn(api,'product').mockImplementation(()=>new Promise(resolve=>{finish=resolve;}));const view=mount(api,false);
  await waitFor(()=>{expect(request).toHaveBeenCalled();});
  const nextApi=new AccessApi({...session,actorId:'A200'});vi.spyOn(nextApi,'product').mockResolvedValue({items:[],hasMore:false,dataDurability:'Temporary data'});
  view.rerender(<FoundationProvider appearance="midnight"><VehicleRecordsExperience api={nextApi} canCreate={false} canRead facts={[]} /></FoundationProvider>);
  await screen.findByText(/No vehicle records added yet/);finish({items:[record],hasMore:false,dataDurability:'Temporary data'});
  await waitFor(()=>expect(screen.queryByText('Family car')).not.toBeInTheDocument());
});
it('does not restore sensitive records after document access is removed', async () => {
  const api=new AccessApi(session);vi.spyOn(api,'product').mockResolvedValue({items:[record],hasMore:false,dataDurability:'Temporary data'});const view=mount(api,false);
  await screen.findByRole('button',{name:'View vehicle record: Family car'});
  view.rerender(<FoundationProvider appearance="midnight"><VehicleRecordsExperience api={api} canCreate={false} canRead={false} facts={[source]} /></FoundationProvider>);
  expect(screen.queryByText('Family car')).not.toBeInTheDocument();expect(screen.getByText(/Document access is required/)).toBeVisible();
});

it.each(['unclassified','PROPERTY','INVESTMENT'])('requires an explicitly classified vehicle source rather than %s',async(kind)=>{
  const api=new AccessApi(session);vi.spyOn(api,'product').mockResolvedValue({items:[],hasMore:false,dataDurability:'Temporary data'});
  const fact={...source,...(kind==='INVESTMENT'?{factType:'INVESTMENT',assetType:undefined}:{assetType:kind==='PROPERTY'?'PROPERTY' as const:undefined})};
  mount(api,true,true,[fact]);
  expect(await screen.findByRole('button',{name:'Add vehicle record'})).toBeDisabled();
});

it('saves an unknown registration reference as null and shows it as not supplied',async()=>{
  const api=new AccessApi(session);let stored=false;const noRegistration={...record,vehicle:{...vehicle,registrationNumber:null}};
  vi.spyOn(api,'product').mockImplementation((_path,method,body)=>{
    if(method==='GET')return Promise.resolve({items:stored?[noRegistration]:[],hasMore:false,dataDurability:'Temporary data'});
    expect(body).toEqual({operationId:'a'.repeat(32),vehicle:{...vehicle,registrationNumber:null}});stored=true;return Promise.resolve(noRegistration);
  });
  vi.spyOn(crypto,'randomUUID').mockReturnValue('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa');mount(api);const user=userEvent.setup();const drawer=await fill(user);
  await user.clear(within(drawer).getByLabelText('Registration reference (optional)'));
  await user.click(within(drawer).getByRole('button',{name:'Save reviewed vehicle record'}));
  await screen.findByText('Vehicle record saved: Family car');await user.click(await screen.findByRole('button',{name:'View vehicle record: Family car'}));
  expect(screen.getByRole('dialog',{name:'Vehicle record details'})).toHaveTextContent('Not supplied');
});

it('edits the saved terms explicitly using the exact record version and immutable historical source', async () => {
  const api=new AccessApi(session);let current=record;
  const corrected={...record,revision:2,vehicle:{...vehicle,currentValue:390000}};
  const request=vi.spyOn(api,'product').mockImplementation((path,method,body)=>{
    if(method==='GET') return Promise.resolve({items:[current],hasMore:false,dataDurability:'Temporary data'});
    expect(path).toBe('v1/vehicle-records/vehicle-saved/corrections');
    expect(body).toEqual({operationId:'a'.repeat(32),expectedRevision:1,vehicle:corrected.vehicle});current=corrected;return Promise.resolve(corrected);
  });
  vi.spyOn(crypto,'randomUUID').mockReturnValue('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa');mount(api,true,true,[]);
  const user=userEvent.setup();await user.click(await screen.findByRole('button',{name:'Edit vehicle record: Family car'}));
  const drawer=screen.getByRole('dialog',{name:'Edit vehicle record'});
  expect(within(drawer).getByText(/Editing version 1 · USD · Source revision 2/)).toBeVisible();
  expect(within(drawer).queryByRole('combobox',{name:'Reviewed vehicle source'})).not.toBeInTheDocument();
  expect(within(drawer).getByLabelText('Current value')).toHaveValue(400000);
  fireEvent.change(within(drawer).getByLabelText('Current value'),{target:{value:'390000'}});
  expect(request.mock.calls.filter(([,method])=>method==='POST')).toHaveLength(0);
  await user.click(within(drawer).getByRole('button',{name:'Save reviewed corrections'}));
  await screen.findByText('Vehicle record saved: Family car');expect(screen.getByText(/Current value: \$390,000.00/)).toBeVisible();
});

it('blocks a stale correction and requires review of the refreshed version', async () => {
  const api=new AccessApi(session);let current=record;
  const request=vi.spyOn(api,'product').mockImplementation((_path,method)=>method==='GET'
    ? Promise.resolve({items:[current],hasMore:false,dataDurability:'Temporary data'})
    : Promise.reject(new AccessApiError(409,'VEHICLE_REVISION_CONFLICT')));
  mount(api);const user=userEvent.setup();await user.click(await screen.findByRole('button',{name:'Edit vehicle record: Family car'}));
  const drawer=screen.getByRole('dialog',{name:'Edit vehicle record'});await user.click(within(drawer).getByRole('button',{name:'Save reviewed corrections'}));
  expect(await within(drawer).findByText(/A newer version has been saved/)).toBeVisible();
  expect(within(drawer).getByRole('button',{name:'Save reviewed corrections'})).toBeDisabled();expect(within(drawer).getByLabelText('Current value')).toBeDisabled();
  await user.click(within(drawer).getByRole('button',{name:'Close'}));current={...record,revision:2,vehicle:{...vehicle,currentValue:380000}};
  await user.click(screen.getByRole('button',{name:'Refresh vehicle records'}));await screen.findByText(/Current value: \$380,000.00/);
  await user.click(screen.getByRole('button',{name:'Edit vehicle record: Family car'}));
  expect(within(screen.getByRole('dialog',{name:'Edit vehicle record'})).getByText(/Editing version 2/)).toBeVisible();
  expect(request.mock.calls.filter(([,method])=>method==='POST')).toHaveLength(1);
});

it('retains an uncertain correction and its expected version after closing with no current source figures', async () => {
  const api=new AccessApi(session);let attempts=0;
  const request=vi.spyOn(api,'product').mockImplementation((_path,method)=>{
    if(method==='GET')return Promise.resolve({items:[record],hasMore:false,dataDurability:'Temporary data'});
    attempts++;return attempts===1?Promise.reject(new AccessApiError(503,'unavailable')):Promise.resolve({...record,revision:2});
  });
  vi.spyOn(crypto,'randomUUID').mockReturnValue('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa');mount(api,true,true,[]);
  const user=userEvent.setup();await user.click(await screen.findByRole('button',{name:'Edit vehicle record: Family car'}));
  const drawer=screen.getByRole('dialog',{name:'Edit vehicle record'});await user.click(within(drawer).getByRole('button',{name:'Save reviewed corrections'}));
  await within(drawer).findByText(/The save has not been confirmed/);expect(within(drawer).getByLabelText('Registration reference (optional)')).toBeDisabled();
  await user.click(within(drawer).getByRole('button',{name:'Close'}));await user.click(screen.getByRole('button',{name:'Continue save confirmation'}));
  const reopened=screen.getByRole('dialog',{name:'Edit vehicle record'});await user.click(within(reopened).getByRole('button',{name:'Retry save confirmation'}));
  await screen.findByText('Vehicle record saved: Family car');const calls=request.mock.calls.filter(([,method])=>method==='POST');
  expect(calls).toHaveLength(2);expect(calls[0]?.[2]).toEqual(calls[1]?.[2]);expect(calls[0]?.[2]).toMatchObject({expectedRevision:1});
});

it('allows correction after confirmed validation rejection and keeps uncertain conflicts frozen', async () => {
  const api=new AccessApi(session);let conflict=false;
  vi.spyOn(api,'product').mockImplementation((_path,method)=>method==='GET'
    ?Promise.resolve({items:[record],hasMore:false,dataDurability:'Temporary data'})
    :Promise.reject(new AccessApiError(conflict?409:400,conflict?'VEHICLE_OPERATION_CONFLICT':'invalid')));
  mount(api);const user=userEvent.setup();await user.click(await screen.findByRole('button',{name:'Edit vehicle record: Family car'}));
  const drawer=screen.getByRole('dialog',{name:'Edit vehicle record'});await user.click(within(drawer).getByRole('button',{name:'Save reviewed corrections'}));
  await within(drawer).findByText(/Check the required amounts/);expect(within(drawer).getByLabelText('Registration reference (optional)')).toBeEnabled();
  conflict=true;await user.click(within(drawer).getByRole('button',{name:'Save reviewed corrections'}));
  await within(drawer).findByText(/The save has not been confirmed/);expect(within(drawer).getByLabelText('Registration reference (optional)')).toBeDisabled();
});

it('reads original and corrected terms in bounded history pages without enabling edits for read-only access', async () => {
  const api=new AccessApi(session);const latest={...record,revision:51,vehicle:{...vehicle,currentValue:300000}};
  const request=vi.spyOn(api,'product').mockImplementation((path)=>{
    if(path==='v1/vehicle-records')return Promise.resolve({items:[latest],hasMore:false,dataDurability:'Temporary data'});
    if(path==='v1/vehicle-records/vehicle-saved/history')return Promise.resolve({items:[latest],hasMore:true,nextBeforeRevision:2});
    if(path==='v1/vehicle-records/vehicle-saved/history?beforeRevision=2')return Promise.resolve({items:[record],hasMore:false,nextBeforeRevision:null});
    return Promise.reject(new Error('unexpected route'));
  });mount(api,false);const user=userEvent.setup();await user.click(await screen.findByRole('button',{name:'View vehicle history: Family car'}));
  expect(screen.queryByRole('button',{name:'Edit vehicle record: Family car'})).not.toBeInTheDocument();
  const drawer=screen.getByRole('dialog',{name:'Vehicle record history'});await user.click(await within(drawer).findByRole('button',{name:'Earlier versions'}));
  await user.click(await within(drawer).findByRole('button',{name:'View version 1'}));
  const version=within(drawer).getByRole('region',{name:'Saved vehicle version 1'});expect(version).toHaveTextContent('$400,000.00 as of 2026-10-01');
  expect(version).toHaveTextContent('$500,000.00');expect(within(version).getByRole('link',{name:'View exact vehicle source history'})).toHaveAttribute('href','/finances?fact=ff-vehicle');
  await user.click(within(drawer).getByRole('button',{name:'Latest versions'}));await within(drawer).findByRole('button',{name:'View version 51'});
  expect(request.mock.calls.every(([,method])=>method==='GET')).toBe(true);
});

it('shows history failure and hides prior history immediately after a session scope change', async () => {
  const api=new AccessApi(session);vi.spyOn(api,'product').mockImplementation(path=>path==='v1/vehicle-records'
    ?Promise.resolve({items:[record],hasMore:false,dataDurability:'Temporary data'}):Promise.reject(new AccessApiError(403,'denied')));
  const view=mount(api);const user=userEvent.setup();await user.click(await screen.findByRole('button',{name:'View vehicle history: Family car'}));
  await screen.findByText('Vehicle history is unavailable.');expect(screen.queryByRole('button',{name:'View version 1'})).not.toBeInTheDocument();
  const other=new AccessApi({...session,actorId:'A200'});vi.spyOn(other,'product').mockResolvedValue({items:[],hasMore:false,dataDurability:'Temporary data'});
  view.rerender(<FoundationProvider appearance="midnight"><VehicleRecordsExperience api={other} facts={[]} canRead canCreate={false} /></FoundationProvider>);
  expect(screen.queryByRole('dialog',{name:'Vehicle record history'})).not.toBeInTheDocument();await screen.findByText(/No vehicle records added yet/);
});

it('aborts pending history and rejects a late previous-session response',async()=>{
  const api=new AccessApi(session);let complete:(value:unknown)=>void=()=>undefined;
  const request=vi.spyOn(api,'product').mockImplementation(path=>path==='v1/vehicle-records'
    ?Promise.resolve({items:[record],hasMore:false,dataDurability:'Temporary data'})
    :new Promise(resolve=>{complete=resolve;}));
  const view=mount(api);const user=userEvent.setup();await user.click(await screen.findByRole('button',{name:'View vehicle history: Family car'}));
  await screen.findByText('Loading vehicle history…');
  const signal=request.mock.calls.find(([path])=>path.endsWith('/history'))?.[3];expect(signal).toBeDefined();
  const next=new AccessApi({...session,actorId:'A200'});vi.spyOn(next,'product').mockResolvedValue({items:[],hasMore:false,dataDurability:'Temporary data'});
  view.rerender(<FoundationProvider appearance="midnight"><VehicleRecordsExperience api={next} facts={[]} canRead canCreate={false} /></FoundationProvider>);
  expect(signal?.aborted).toBe(true);complete({items:[record],hasMore:false,nextBeforeRevision:null});
  await screen.findByText(/No vehicle records added yet/);expect(screen.queryByRole('dialog',{name:'Vehicle record history'})).not.toBeInTheDocument();
});

it.each(['source','customer'])('rejects vehicle history with changed %s identity',async(variation)=>{
  const api=new AccessApi(session);const changed=variation==='source'?{...record,vehicle:{...vehicle,sourceRevision:99}}:{...record,customerId:'foreign'};
  vi.spyOn(api,'product').mockImplementation(path=>path==='v1/vehicle-records'
    ?Promise.resolve({items:[record],hasMore:false,dataDurability:'Temporary data'})
    :Promise.resolve({items:[changed],hasMore:false,nextBeforeRevision:null}));
  mount(api,false);const user=userEvent.setup();await user.click(await screen.findByRole('button',{name:'View vehicle history: Family car'}));
  expect(await screen.findByText('Vehicle history is unavailable.')).toBeVisible();expect(screen.queryByRole('button',{name:'View version 1'})).not.toBeInTheDocument();
});

it('edits an unknown registration as blank and can add then clear the optional reference',async()=>{
  const api=new AccessApi(session);let latest={...record,vehicle:{...vehicle,registrationNumber:null as string|null}};
  const request=vi.spyOn(api,'product').mockImplementation((path,_method,body)=>{
    if(path.endsWith('/corrections')) {const input=body as {operationId:string;expectedRevision:number;vehicle:ProductVehicleDraft};latest={...latest,...input,revision:input.expectedRevision+1};return Promise.resolve(latest);}
    return Promise.resolve({items:[latest],hasMore:false,dataDurability:'Temporary data'});
  });
  mount(api);const user=userEvent.setup();await user.click(await screen.findByRole('button',{name:'Edit vehicle record: Family car'}));
  let drawer=screen.getByRole('dialog',{name:'Edit vehicle record'});expect(within(drawer).getByLabelText('Registration reference (optional)')).toHaveValue('');
  await user.type(within(drawer).getByLabelText('Registration reference (optional)'),'  Ref 42  ');await user.click(within(drawer).getByRole('button',{name:'Save reviewed corrections'}));
  await screen.findByText('Vehicle record saved: Family car');expect(latest.vehicle.registrationNumber).toBe('Ref 42');
  await user.click(await screen.findByRole('button',{name:'Edit vehicle record: Family car'}));drawer=screen.getByRole('dialog',{name:'Edit vehicle record'});
  await user.clear(within(drawer).getByLabelText('Registration reference (optional)'));await user.click(within(drawer).getByRole('button',{name:'Save reviewed corrections'}));
  await screen.findByText('Vehicle record saved: Family car');expect(latest.vehicle.registrationNumber).toBeNull();
  expect(request.mock.calls.filter(([path])=>path.endsWith('/corrections')).map(([, ,body])=>(body as {expectedRevision:number}).expectedRevision)).toEqual([1,2]);
});

it('pages beyond 200 vehicle records and refreshes from the newest page', async () => {
 const api = new AccessApi(session); const first = Array.from({ length: 200 }, (_, i) => ({ ...record, vehicleRecordId: 'vehicle-page-' + String(i), vehicle: { ...record.vehicle, name: 'Paged record ' + String(i) } }));
 const request = vi.spyOn(api, 'product').mockImplementation(async path => {
  await Promise.resolve();
  if (path === 'v1/vehicle-records') return { items: first, hasMore: true, dataDurability: 'Temporary data' };
  if (path === 'v1/vehicle-records?afterId=vehicle-page-199') return { items: [record], hasMore: false, dataDurability: 'Temporary data' };
  throw new Error('Unexpected page');
 });
 mount(api, false); fireEvent.click(await screen.findByRole('button', { name: 'Earlier vehicle records' }));
 expect(await screen.findByRole('button', { name: 'View vehicle record: ' + record.vehicle.name })).toBeVisible();
 expect(screen.queryByRole('button', { name: 'Earlier vehicle records' })).not.toBeInTheDocument();
 expect(request.mock.calls.some(([path]) => path === 'v1/vehicle-records?afterId=vehicle-page-199')).toBe(true);
 fireEvent.click(screen.getByRole('button', { name: 'Refresh vehicle records' })); expect(await screen.findByRole('button', { name: 'Earlier vehicle records' })).toBeVisible();
});
