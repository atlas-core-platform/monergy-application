import { FoundationProvider } from '@monergy/ui-foundation';
import { cleanup, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, it, vi } from 'vitest';
import { AccessApi, AccessApiError } from '../src/access/accessApi';
import { Context } from '../src/access/workspaceSession';
import InvestmentRecordsExperience from '../src/product/InvestmentRecordsExperience';
import type { ProductFinancialFact, ProductInvestmentRecord } from '../src/product/productApi';

const session={tenantId:'T001',actorId:'A100',authenticationContextId:'session',subjectVersion:2,expiresAt:'2027-01-01T00:00:00Z'};
const source:ProductFinancialFact={financialFactId:'fact-investment',factType:'INVESTMENT',label:'Investment statement value',value:400000,
  currency:'INR',effectiveDate:'2026-10-01',revision:2,source:{sourceName:'statement.pdf',documentVersion:'document-v1',
    recordedAt:'2026-10-01T00:00:00Z',technicalSourceFactId:'source-investment',technicalProvenanceId:'provenance-investment'}};
const record:ProductInvestmentRecord={investmentRecordId:'investment-saved',operationId:'a'.repeat(32),customerId:'reference-customer',
  investment:{name:'Retirement holding',institution:'Example institution',investmentType:'Mutual fund',purchaseValue:500000,currentValue:400000,
    valueAsOfDate:'2026-10-01',sourceFinancialFactId:'fact-investment',sourceRevision:2},currency:'INR',sourceProvenanceId:'provenance-investment',
  documentVersionId:'document-v1',evidenceId:'evidence-v1',recordedBy:'A100',recordedAt:'2026-10-20T10:00:00Z',revision:1,
  lifecycleStatus:'UNREVIEWED',lifecycleAsOfDate:null};
const mount=(api:AccessApi,canCreate=true,canRead=true)=>render(<FoundationProvider appearance="midnight"><Context value={{
  status:'authorized',session,api,authorization:{area:'product',context:{tenantId:'T001',actorId:'A100',customerId:'reference-customer',
    customerName:'Reference Customer',customerDetail:'Primary customer',capabilities:['financial-profile.profile.read','evidence.document.read',...(canCreate?['financial-profile.profile.update']:[])],
    environment:'Test environment',dataDurability:'Temporary data',productionAccepted:false}},message:'',recovery:null,
  connect:()=>Promise.resolve(),retry:()=>Promise.resolve(),signOut:()=>Promise.resolve(),cancel:()=>undefined,
}}><InvestmentRecordsExperience api={api} facts={[source]} canCreate={canCreate} canRead={canRead}/></Context></FoundationProvider>);
afterEach(()=>{cleanup();vi.restoreAllMocks();});

it('reviews an investment lifecycle status with one retry-safe operation and shows the governed result',async()=>{
  const api=new AccessApi(session);let current=record;const calls=vi.spyOn(api,'product').mockImplementation((path,method,body)=>{
    if(path==='v1/investment-records'&&method==='GET')return Promise.resolve({items:[current],hasMore:false,dataDurability:'Temporary data'});
    if(path.endsWith('/lifecycle')){expect(body).toEqual({operationId:'b'.repeat(32),expectedRevision:1,status:'CLOSED',asOfDate:'2026-10-02'});
      current={...record,operationId:'b'.repeat(32),revision:2,lifecycleStatus:'CLOSED',lifecycleAsOfDate:'2026-10-02'};return Promise.resolve(current);}
    throw new Error('unexpected route '+path);
  });
  vi.spyOn(crypto,'randomUUID').mockReturnValue('bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb');mount(api);const user=userEvent.setup();
  await user.click(await screen.findByRole('button',{name:'Review investment status: Retirement holding'}));
  const drawer=screen.getByRole('dialog',{name:'Review investment status'});
  await user.click(within(drawer).getByRole('combobox',{name:'Investment status'}));await user.click(screen.getByText('Closed',{selector:'.ant-select-item-option-content'}));
  await user.type(within(drawer).getByLabelText('Status as of'),'2026-10-02');await user.click(within(drawer).getByRole('button',{name:'Save reviewed investment status'}));
  expect(await screen.findByText('Investment status saved: Retirement holding')).toBeVisible();expect(screen.getByText('Closed — customer reported')).toBeVisible();
  expect(calls.mock.calls.filter(([,method])=>method==='POST')).toHaveLength(1);
});

it('keeps lifecycle history explicit and readable without update access',async()=>{
  const api=new AccessApi(session);const closed={...record,operationId:'b'.repeat(32),revision:2,lifecycleStatus:'CLOSED' as const,lifecycleAsOfDate:'2026-10-02'};
  vi.spyOn(api,'product').mockImplementation((path)=>path.endsWith('/history')?Promise.resolve({items:[closed,record],hasMore:false,nextBeforeRevision:null}):
    Promise.resolve({items:[closed],hasMore:false,dataDurability:'Temporary data'}));mount(api,false);const user=userEvent.setup();
  expect(await screen.findByText('Closed — customer reported')).toBeVisible();expect(screen.queryByRole('button',{name:/Review investment status/})).not.toBeInTheDocument();
  await user.click(screen.getByRole('button',{name:'View investment history: Retirement holding'}));const history=await screen.findByRole('region',{name:'Reviewed investment change history'});
  expect(within(history).getByText('Closed — customer reported as of 2026-10-02')).toBeVisible();expect(within(history).getByText('Status not reviewed')).toBeVisible();
});

it('preserves read-only behavior and reports a lifecycle conflict without inventing success',async()=>{
  const api=new AccessApi(session);vi.spyOn(api,'product').mockImplementation((path,method)=>{
    if(path==='v1/investment-records'&&method==='GET')return Promise.resolve({items:[record],hasMore:false,dataDurability:'Temporary data'});
    return Promise.reject(new AccessApiError(409,'INVESTMENT_REVISION_CONFLICT'));
  });
  vi.spyOn(crypto,'randomUUID').mockReturnValue('bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb');mount(api);const user=userEvent.setup();
  await user.click(await screen.findByRole('button',{name:'Review investment status: Retirement holding'}));const drawer=screen.getByRole('dialog',{name:'Review investment status'});
  await user.click(within(drawer).getByRole('combobox',{name:'Investment status'}));await user.click(screen.getByText('Active',{selector:'.ant-select-item-option-content'}));
  await user.type(within(drawer).getByLabelText('Status as of'),'2026-10-02');await user.click(within(drawer).getByRole('button',{name:'Save reviewed investment status'}));
  expect(await within(drawer).findByText(/A newer version has been saved/)).toBeVisible();expect(screen.queryByText(/Investment status saved/)).not.toBeInTheDocument();
});
