import { FoundationProvider } from '@monergy/ui-foundation';
import { cleanup, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, it, vi } from 'vitest';
import { AccessApi, AccessApiError } from '../src/access/accessApi';
import PropertyRecordsExperience from '../src/product/PropertyRecordsExperience';
import type { ProductFinancialFact, ProductPropertyRecord } from '../src/product/productApi';

const session={tenantId:'T001',actorId:'A100',authenticationContextId:'session',subjectVersion:2,expiresAt:'2027-01-01T00:00:00Z'};
const source:ProductFinancialFact={financialFactId:'fact-property',factType:'ASSET',assetType:'PROPERTY',label:'Property statement amount',value:400000,
  currency:'INR',effectiveDate:'2026-10-01',revision:2,source:{sourceName:'property.pdf',documentVersion:'document-v1',
    recordedAt:'2026-10-01T00:00:00Z',technicalSourceFactId:'source-property',technicalProvenanceId:'provenance-property'}};
const record:ProductPropertyRecord={propertyRecordId:'property-saved',operationId:'a'.repeat(32),customerId:'reference-customer',
  property:{name:'Family home',location:'Example location',propertyType:'Apartment',purchaseValue:500000,currentValue:400000,
    valueAsOfDate:'2026-10-01',sourceFinancialFactId:'fact-property',sourceRevision:2},currency:'INR',sourceProvenanceId:'provenance-property',
  documentVersionId:'document-v1',evidenceId:'evidence-v1',recordedBy:'A100',recordedAt:'2026-10-20T10:00:00Z',revision:1,
  ownershipStatus:'UNREVIEWED',ownershipAsOfDate:null};
const mount=(api:AccessApi,canCreate=true,canRead=true)=>render(<FoundationProvider appearance="midnight">
  <PropertyRecordsExperience api={api} facts={[source]} canCreate={canCreate} canRead={canRead}/></FoundationProvider>);
afterEach(()=>{cleanup();vi.restoreAllMocks();});

it('reviews property ownership with one retry-safe operation and shows the governed result',async()=>{
  const api=new AccessApi(session);let current=record;const calls=vi.spyOn(api,'product').mockImplementation((path,method,body)=>{
    if(path==='v1/property-records'&&method==='GET')return Promise.resolve({items:[current],hasMore:false,dataDurability:'Temporary data'});
    if(path.endsWith('/ownership')){expect(body).toEqual({operationId:'b'.repeat(32),expectedRevision:1,status:'DISPOSED',asOfDate:'2026-10-02'});
      current={...record,operationId:'b'.repeat(32),revision:2,ownershipStatus:'DISPOSED',ownershipAsOfDate:'2026-10-02'};return Promise.resolve(current);}
    throw new Error('unexpected route '+path);
  });
  vi.spyOn(crypto,'randomUUID').mockReturnValue('bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb');mount(api);const user=userEvent.setup();
  await user.click(await screen.findByRole('button',{name:'Review property ownership: Family home'}));
  const drawer=screen.getByRole('dialog',{name:'Review property ownership'});
  await user.click(within(drawer).getByRole('combobox',{name:'Ownership status'}));await user.click(screen.getByText('Disposed',{selector:'.ant-select-item-option-content'}));
  await user.type(within(drawer).getByLabelText('Ownership as of'),'2026-10-02');await user.click(within(drawer).getByRole('button',{name:'Save reviewed ownership'}));
  expect(await screen.findByText('Property ownership saved: Family home')).toBeVisible();expect(screen.getByText('Disposed — customer reported')).toBeVisible();
  expect(calls.mock.calls.filter(([,method])=>method==='POST')).toHaveLength(1);
});

it('keeps ownership history explicit and readable without update access',async()=>{
  const api=new AccessApi(session);const disposed={...record,operationId:'b'.repeat(32),revision:2,ownershipStatus:'DISPOSED' as const,ownershipAsOfDate:'2026-10-02'};
  vi.spyOn(api,'product').mockImplementation((path)=>path.endsWith('/history')?Promise.resolve({items:[disposed,record],hasMore:false,nextBeforeRevision:null}):
    Promise.resolve({items:[disposed],hasMore:false,dataDurability:'Temporary data'}));mount(api,false);const user=userEvent.setup();
  expect(await screen.findByText('Disposed — customer reported')).toBeVisible();expect(screen.queryByRole('button',{name:/Review property ownership/})).not.toBeInTheDocument();
  await user.click(screen.getByRole('button',{name:'View property history: Family home'}));const history=await screen.findByRole('region',{name:'Reviewed property change history'});
  expect(within(history).getByText('Disposed — customer reported as of 2026-10-02')).toBeVisible();expect(within(history).getByText('Ownership not reviewed')).toBeVisible();
});

it('reports an ownership conflict without inventing success',async()=>{
  const api=new AccessApi(session);vi.spyOn(api,'product').mockImplementation((path,method)=>{
    if(path==='v1/property-records'&&method==='GET')return Promise.resolve({items:[record],hasMore:false,dataDurability:'Temporary data'});
    return Promise.reject(new AccessApiError(409,'PROPERTY_REVISION_CONFLICT'));
  });
  vi.spyOn(crypto,'randomUUID').mockReturnValue('bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb');mount(api);const user=userEvent.setup();
  await user.click(await screen.findByRole('button',{name:'Review property ownership: Family home'}));const drawer=screen.getByRole('dialog',{name:'Review property ownership'});
  await user.click(within(drawer).getByRole('combobox',{name:'Ownership status'}));await user.click(screen.getByText('Owned',{selector:'.ant-select-item-option-content'}));
  await user.type(within(drawer).getByLabelText('Ownership as of'),'2026-10-02');await user.click(within(drawer).getByRole('button',{name:'Save reviewed ownership'}));
  expect(await within(drawer).findByText(/A newer version has been saved/)).toBeVisible();expect(screen.queryByText(/Property ownership saved/)).not.toBeInTheDocument();
});
