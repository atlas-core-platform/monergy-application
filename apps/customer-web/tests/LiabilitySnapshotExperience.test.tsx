import { FoundationProvider } from '@monergy/ui-foundation';
import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, it, vi } from 'vitest';
import { AccessApi, AccessApiError } from '../src/access/accessApi';
import LiabilitySnapshotExperience from '../src/product/LiabilitySnapshotExperience';
import LiabilityRecordsExperience from '../src/product/LiabilityRecordsExperience';
import type { ProductLiabilityRecord, ProductLiabilityRecordList } from '../src/product/productApi';

const session={tenantId:'T001',actorId:'A100',authenticationContextId:'session',subjectVersion:2,expiresAt:'2027-01-01T00:00:00Z'};
const record:ProductLiabilityRecord={liabilityRecordId:'liability-one',operationId:'a'.repeat(32),customerId:'customer',currency:'USD',revision:2,
  liability:{name:'Reviewed home debt',creditor:'Example lender',outstandingAmount:400000,balanceAsOfDate:'2026-09-30',sourceFinancialFactId:'ff-loan',sourceRevision:1},
  sourceProvenanceId:'provenance',documentVersionId:'document',evidenceId:'evidence',recordedBy:'A100',recordedAt:'2026-10-19T00:00:00Z',verificationStatus:'CUSTOMER_REVIEWED'};
const value={asOfDate:'2026-09-30',currency:'USD',items:[record],hasMore:false};
function mount(api:AccessApi,refreshKey:ProductLiabilityRecordList|null=null){return render(<FoundationProvider appearance="midnight"><LiabilitySnapshotExperience api={api} refreshKey={refreshKey}/></FoundationProvider>);}
async function choose(user:ReturnType<typeof userEvent.setup>){fireEvent.change(screen.getByLabelText('Snapshot balance date'),{target:{value:'2026-09-30'}});await user.type(screen.getByLabelText('Snapshot currency'),'usd');}
async function show(user:ReturnType<typeof userEvent.setup>){await choose(user);await user.click(screen.getByRole('button',{name:'View dated liabilities'}));}
afterEach(()=>{cleanup();vi.restoreAllMocks();});
it('requires an explicit exact date/currency and displays original balances with source/version meaning',async()=>{
  const api=new AccessApi(session);const request=vi.spyOn(api,'product').mockResolvedValue(value);mount(api);const user=userEvent.setup();
  expect(request).not.toHaveBeenCalled();expect(screen.getByRole('button',{name:'View dated liabilities'})).toBeDisabled();await show(user);
  const result=await screen.findByRole('region',{name:'Dated liability results'});expect(result).toHaveTextContent('$400,000.00');expect(result).toHaveTextContent('Example lender');expect(result).toHaveTextContent('Customer reviewed');
  expect(result).toHaveTextContent('No total debt or complete liability position is established');expect(within(result).getByRole('link',{name:'View source history for Reviewed home debt'})).toHaveAttribute('href','/finances?fact=ff-loan');
  expect(request.mock.calls[0]?.slice(0,3)).toEqual(['v1/liability-records/snapshot?asOfDate=2026-09-30&currency=USD','GET',undefined]);
});
it('distinguishes no exact matches from zero debt',async()=>{
  const api=new AccessApi(session);vi.spyOn(api,'product').mockResolvedValue({...value,items:[]});mount(api);const user=userEvent.setup();await show(user);
  expect(await screen.findByText(/This does not mean zero debt/)).toBeVisible();expect(screen.getByText(/Missing debts, other dates and currencies/)).toBeVisible();
});
it('flags repeated source identities while retaining separate reviewed balances',async()=>{
  const api=new AccessApi(session);vi.spyOn(api,'product').mockResolvedValue({...value,items:[record,{...record,liabilityRecordId:'liability-two',liability:{...record.liability,name:'Other reviewed debt',outstandingAmount:7}}]});mount(api);const user=userEvent.setup();await show(user);
  expect(await screen.findByText(/More than one record references the same source/)).toBeVisible();expect(screen.getByRole('region',{name:'Dated liability results'})).toHaveTextContent('$7.00');expect(screen.getByRole('region',{name:'Dated liability results'})).not.toHaveTextContent('$400,007.00');
});
it('keeps the matching-record bound explicit without using the ordinary list bound as its source',async()=>{
  const api=new AccessApi(session);const items=Array.from({length:200},(_,i)=>({...record,liabilityRecordId:'liability-'+String(i),liability:{...record.liability,name:'Debt '+String(i),sourceFinancialFactId:'source-'+String(i)}}));
  const request=vi.spyOn(api,'product').mockResolvedValue({...value,items,hasMore:true});mount(api);const user=userEvent.setup();await show(user);
  expect(await screen.findByText(/More matching records exist/)).toBeVisible();expect(request.mock.calls).toHaveLength(1);expect(request.mock.calls[0]?.[0]).toContain('/snapshot?');
});
it.each([400,403,503])('hides unavailable or invalid snapshots (%s)',async status=>{
  const api=new AccessApi(session);vi.spyOn(api,'product').mockRejectedValue(new AccessApiError(status,'unavailable'));mount(api);const user=userEvent.setup();await show(user);
  await screen.findByText(status===400?/Choose a balance date from 1900/:/Dated liabilities are unavailable/);expect(screen.queryByRole('region',{name:'Dated liability results'})).not.toBeInTheDocument();
});
it('aborts and discards an old selection even if cancellation is ignored',async()=>{
  const api=new AccessApi(session);let finish:(result:unknown)=>void=()=>undefined;const request=vi.spyOn(api,'product').mockImplementation(()=>new Promise(resolve=>{finish=resolve;}));mount(api);const user=userEvent.setup();await show(user);
  fireEvent.change(screen.getByLabelText('Snapshot balance date'),{target:{value:'2026-10-01'}});expect(request.mock.calls[0]?.[3]?.aborted).toBe(true);await act(async()=>{finish(value);await Promise.resolve();});expect(screen.queryByRole('region',{name:'Dated liability results'})).not.toBeInTheDocument();
});
it('hides and aborts a response from a previous API/session',async()=>{
  const api=new AccessApi(session);let finish:(result:unknown)=>void=()=>undefined;const request=vi.spyOn(api,'product').mockImplementation(()=>new Promise(resolve=>{finish=resolve;}));const view=mount(api);const user=userEvent.setup();await show(user);
  view.rerender(<FoundationProvider appearance="midnight"><LiabilitySnapshotExperience api={new AccessApi({...session,actorId:'A200'})} refreshKey={null}/></FoundationProvider>);
  expect(request.mock.calls[0]?.[3]?.aborted).toBe(true);await act(async()=>{finish(value);await Promise.resolve();});expect(screen.queryByRole('region',{name:'Dated liability results'})).not.toBeInTheDocument();
});
it('requires a new snapshot after the parent refreshes its saved record versions',async()=>{
  const api=new AccessApi(session);const request=vi.spyOn(api,'product').mockResolvedValue(value);const first:ProductLiabilityRecordList={items:[record],hasMore:false,dataDurability:'Temporary data'};const view=mount(api,first);const user=userEvent.setup();await show(user);await screen.findByRole('region',{name:'Dated liability results'});
  view.rerender(<FoundationProvider appearance="midnight"><LiabilitySnapshotExperience api={api} refreshKey={{...first,items:[{...record,revision:3}]}}/></FoundationProvider>);
  expect(screen.queryByRole('region',{name:'Dated liability results'})).not.toBeInTheDocument();expect(request.mock.calls).toHaveLength(1);
});
it.each(['date','currency','status','revision','duplicate','hasMore'])('rejects an unsupported response (%s)',async variation=>{
  const api=new AccessApi(session);const response=variation==='date'?{...value,asOfDate:'2026-09-29'}:variation==='currency'?{...value,currency:'INR'}:variation==='status'?{...value,items:[{...record,verificationStatus:'VERIFIED'}]}:variation==='revision'?{...value,items:[{...record,revision:0}]}:variation==='duplicate'?{...value,items:[record,record]}:{...value,hasMore:true};
  vi.spyOn(api,'product').mockResolvedValue(response);mount(api);const user=userEvent.setup();await show(user);await screen.findByText(/Dated liabilities are unavailable/);expect(screen.queryByRole('region',{name:'Dated liability results'})).not.toBeInTheDocument();
});
it.each([true,false])('requires document access and allows read-only snapshot use (%s)',async canRead=>{
  const api=new AccessApi(session);const request=vi.spyOn(api,'product').mockImplementation(path=>path==='v1/liability-records'?Promise.resolve({items:[],hasMore:false,dataDurability:'Temporary data'}):Promise.resolve(value));
  render(<FoundationProvider appearance="midnight"><LiabilityRecordsExperience api={api} facts={[]} canRead={canRead} canCreate={false}/></FoundationProvider>);
  if(canRead){await screen.findByText('No liability records added yet. Review a loan/debt source figure in Documents, then enter its creditor, balance and as-of date here.');const user=userEvent.setup();await show(user);await screen.findByRole('region',{name:'Dated liability results'});expect(screen.queryByRole('button',{name:'Add liability record'})).not.toBeInTheDocument();}
  else {expect(screen.queryByRole('region',{name:'Dated liability snapshot'})).not.toBeInTheDocument();expect(request).not.toHaveBeenCalled();}
});
