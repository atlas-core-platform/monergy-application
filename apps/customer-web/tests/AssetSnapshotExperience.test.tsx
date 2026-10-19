import { FoundationProvider } from '@monergy/ui-foundation';
import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, it, vi } from 'vitest';
import { AccessApi, AccessApiError } from '../src/access/accessApi';
import AssetSnapshotExperience from '../src/product/AssetSnapshotExperience';
import type { ProductAssetSnapshot, ProductVehicleRecord } from '../src/product/productApi';
const session={tenantId:'T001',actorId:'A100',authenticationContextId:'session',subjectVersion:2,expiresAt:'2027-01-01T00:00:00Z'};
const common={operationId:'a'.repeat(32),customerId:'customer',currency:'USD',revision:2,sourceProvenanceId:'provenance',documentVersionId:'document',evidenceId:'evidence',recordedBy:'A100',recordedAt:'2026-10-19T00:00:00Z'};
const draft={name:'Family car',registrationNumber:null,vehicleType:'Car',purchaseValue:0,currentValue:1234.56,valueAsOfDate:'2026-09-30',sourceFinancialFactId:'ff-vehicle',sourceRevision:1};
const vehicle:ProductVehicleRecord={...common,vehicleRecordId:'vehicle-one',vehicle:draft};
const value:ProductAssetSnapshot={asOfDate:'2026-09-30',currency:'USD',vehicles:{items:[vehicle],hasMore:false},investments:{items:[{...common,investmentRecordId:'investment-one',investment:{...draft,name:'Reviewed holding',institution:'Example institution',investmentType:'Fund',sourceFinancialFactId:'ff-investment'}}],hasMore:false},properties:{items:[{...common,propertyRecordId:'property-one',property:{...draft,name:'Family home',location:'Example location',propertyType:'Apartment',sourceFinancialFactId:'ff-property'}}],hasMore:false}};
function mount(api:AccessApi,refreshKey:unknown=null){return render(<FoundationProvider appearance="midnight"><AssetSnapshotExperience api={api} refreshKey={refreshKey}/></FoundationProvider>);}
async function show(user:ReturnType<typeof userEvent.setup>){fireEvent.change(screen.getByLabelText('Snapshot value date'),{target:{value:'2026-09-30'}});await user.type(screen.getByLabelText('Snapshot currency'),'usd');await user.click(screen.getByRole('button',{name:'View dated assets'}));}
afterEach(()=>{cleanup();vi.restoreAllMocks();});
it('requests explicit date/currency and retains all three kinds,separate values and exact source/version meaning',async()=>{
 const api=new AccessApi(session);const request=vi.spyOn(api,'product').mockResolvedValue(value);mount(api);const user=userEvent.setup();expect(request).not.toHaveBeenCalled();expect(screen.getByRole('button',{name:'View dated assets'})).toBeDisabled();await show(user);
 const result=await screen.findByRole('region',{name:'Dated asset results'});expect(result).toHaveTextContent('Family car');expect(result).toHaveTextContent('Family home');expect(result).toHaveTextContent('Reviewed holding');expect(result).toHaveTextContent('$0.00');expect(result).toHaveTextContent('$1,234.56');expect(result).toHaveTextContent('Registration not supplied');expect(result).toHaveTextContent('No total assets, net worth or complete asset position');expect(result).toHaveTextContent('Each kind is read independently');
 for(const name of ['Family car','Family home','Reviewed holding'])expect(within(result).getByRole('link',{name:'View source history for '+name})).toHaveAttribute('href',expect.stringContaining('/finances?fact=ff-'));
 expect(request.mock.calls[0]?.slice(0,3)).toEqual(['v1/asset-records/snapshot?asOfDate=2026-09-30&currency=USD','GET',undefined]);
});
it('distinguishes no saved matches from zero assets',async()=>{
 const api=new AccessApi(session);vi.spyOn(api,'product').mockResolvedValue({...value,investments:{items:[],hasMore:false},properties:{items:[],hasMore:false},vehicles:{items:[],hasMore:false}});mount(api);await show(userEvent.setup());expect(await screen.findByText(/This does not mean zero assets/)).toBeVisible();
});
it('keeps records sharing a source separate and warns about overlap',async()=>{
 const api=new AccessApi(session);vi.spyOn(api,'product').mockResolvedValue({...value,vehicles:{items:[vehicle,{...vehicle,vehicleRecordId:'vehicle-two',vehicle:{...draft,name:'Other reviewed car',currentValue:7}}],hasMore:false}});mount(api);await show(userEvent.setup());expect(await screen.findByText(/More than one record references/)).toBeVisible();expect(screen.getByRole('region',{name:'Dated asset results'})).toHaveTextContent('$7.00');expect(screen.getByRole('region',{name:'Dated asset results'})).not.toHaveTextContent('$1,241.56');
});
it('shows each kind bound without hiding other kinds',async()=>{
 const api=new AccessApi(session);const items=Array.from({length:200},(_,n)=>({...vehicle,vehicleRecordId:'vehicle-'+String(n)}));vi.spyOn(api,'product').mockResolvedValue({...value,vehicles:{items,hasMore:true}});mount(api);await show(userEvent.setup());expect(await screen.findByText(/Vehicles: 200.*More matching records exist/)).toBeVisible();expect(screen.getByRole('region',{name:'Dated asset results'})).toHaveTextContent('Family home');
});
it.each([400,403,503])('shows an explicit unavailable or invalid selection for %s',async(status)=>{
 const api=new AccessApi(session);vi.spyOn(api,'product').mockRejectedValue(new AccessApiError(status,'FAIL'));mount(api);await show(userEvent.setup());expect(await screen.findByRole('alert')).toBeVisible();expect(screen.queryByRole('region',{name:'Dated asset results'})).not.toBeInTheDocument();
});
it('clears and aborts pending results when filters change,including ignored cancellation',async()=>{
 const api=new AccessApi(session);let finish!:(value:ProductAssetSnapshot)=>void;const request=vi.spyOn(api,'product').mockImplementation(()=>new Promise(resolve=>{finish=resolve;}));mount(api);await show(userEvent.setup());const signal=request.mock.calls[0]?.[3];fireEvent.change(screen.getByLabelText('Snapshot value date'),{target:{value:'2026-09-29'}});expect(signal?.aborted).toBe(true);await act(async()=>{finish(value);await Promise.resolve();});expect(screen.queryByRole('region',{name:'Dated asset results'})).not.toBeInTheDocument();
});
it.each(['api','records'])('hides and aborts previous %s scope even when cancellation is ignored',async(kind)=>{
 const api=new AccessApi(session);let finish!:(value:ProductAssetSnapshot)=>void;const request=vi.spyOn(api,'product').mockImplementation(()=>new Promise(resolve=>{finish=resolve;}));const view=mount(api,0);await show(userEvent.setup());const next=kind==='api'?new AccessApi({...session,actorId:'A200'}):api;view.rerender(<FoundationProvider appearance="midnight"><AssetSnapshotExperience api={next} refreshKey={1}/></FoundationProvider>);expect(request.mock.calls[0]?.[3]?.aborted).toBe(true);await act(async()=>{finish(value);await Promise.resolve();});expect(screen.queryByRole('region',{name:'Dated asset results'})).not.toBeInTheDocument();
});
it('invalidates a displayed snapshot after a saved-record refresh',async()=>{
 const api=new AccessApi(session);vi.spyOn(api,'product').mockResolvedValue(value);const view=mount(api,0);await show(userEvent.setup());await screen.findByRole('region',{name:'Dated asset results'});view.rerender(<FoundationProvider appearance="midnight"><AssetSnapshotExperience api={api} refreshKey={1}/></FoundationProvider>);expect(screen.queryByRole('region',{name:'Dated asset results'})).not.toBeInTheDocument();
});
it.each(['date','currency','group','items','hasMore','large','duplicate','item-date','item-currency','revision','source','amount','missing-flag'])('rejects malformed %s data without showing a partial position',async(variation)=>{
 const changed=structuredClone(value);const row=changed.vehicles.items[0];if(!row)throw new Error('Missing fixture');
 switch(variation){case 'date':changed.asOfDate='2026-09-29';break;case 'currency':changed.currency='INR';break;case 'group':changed.properties=null as unknown as ProductAssetSnapshot['properties'];break;case 'items':changed.investments.items=null as unknown as ProductAssetSnapshot['investments']['items'];break;case 'hasMore':changed.vehicles.hasMore=true;break;case 'large':changed.vehicles.items=Array.from({length:201},(_,n)=>({...row,vehicleRecordId:String(n)}));break;case 'duplicate':changed.vehicles.items.push(row);break;case 'item-date':row.vehicle.valueAsOfDate='2026-09-29';break;case 'item-currency':row.currency='INR';break;case 'revision':row.revision=0;break;case 'source':row.vehicle.sourceRevision=0;break;case 'amount':row.vehicle.currentValue=-1;break;case 'missing-flag':delete (changed.properties as Partial<ProductAssetSnapshot['properties']>).hasMore;}
 const api=new AccessApi(session);vi.spyOn(api,'product').mockResolvedValue(changed);mount(api);await show(userEvent.setup());expect(await screen.findByRole('alert')).toBeVisible();expect(screen.queryByRole('region',{name:'Dated asset results'})).not.toBeInTheDocument();
});
