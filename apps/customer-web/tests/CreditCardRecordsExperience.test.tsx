import { FoundationProvider } from '@monergy/ui-foundation';
import { cleanup, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, it, vi } from 'vitest';
import { AccessApi, AccessApiError } from '../src/access/accessApi';
import { Context } from '../src/access/workspaceSession';
import CreditCardRecordsExperience from '../src/product/CreditCardRecordsExperience';
import type { ProductCreditCardRecord, ProductFinancialFact } from '../src/product/productApi';

const session={tenantId:'T001',actorId:'A100',authenticationContextId:'session',subjectVersion:2,expiresAt:'2027-01-01T00:00:00Z'};
const source:ProductFinancialFact={financialFactId:'fact-card',factType:'LOAN',label:'Card statement closing balance',
  value:2510.25,currency:'INR',effectiveDate:'2026-10-01',revision:2,source:{sourceName:'statement.pdf',documentVersion:'document-v1',
    recordedAt:'2026-10-01T00:00:00Z',technicalSourceFactId:'source-bank',technicalProvenanceId:'provenance-bank'}};
const record:ProductCreditCardRecord={creditCardRecordId:'credit-card-saved',operationId:'a'.repeat(32),customerId:'reference-customer',
  card:{issuerName:'Example Issuer',cardType:'Rewards',cardNumberLast4:'4821',sourceFinancialFactId:'fact-card',sourceRevision:2},
  balance:2510.25,currency:'INR',balanceAsOfDate:'2026-10-01',sourceProvenanceId:'provenance-bank',documentVersionId:'document-v1',
  evidenceId:'evidence-v1',recordedBy:'A100',recordedAt:'2026-10-20T10:00:00Z',revision:1};
const mount=(api:AccessApi,canCreate=true,canRead=true,facts=[source])=>render(<FoundationProvider appearance="midnight"><Context value={{
  status:'authorized',session,api,authorization:{area:'product',context:{tenantId:'T001',actorId:'A100',customerId:'reference-customer',
    customerName:'Reference Customer',customerDetail:'Primary customer',capabilities:['financial-profile.profile.read','evidence.document.read',...(canCreate?['financial-profile.profile.update']:[])],
    environment:'Test environment',dataDurability:'Temporary data',productionAccepted:false}},message:'',recovery:null,
  connect:()=>Promise.resolve(),retry:()=>Promise.resolve(),signOut:()=>Promise.resolve(),cancel:()=>undefined,
}}><CreditCardRecordsExperience api={api} facts={facts} canCreate={canCreate} canRead={canRead}/></Context></FoundationProvider>);
afterEach(()=>{cleanup();vi.restoreAllMocks();});

it('creates metadata only and presents the source-derived balance and safe masked card',async()=>{
  const api=new AccessApi(session);let stored=false;const calls=vi.spyOn(api,'product').mockImplementation((path,method,body)=>{
    if(path==='v1/credit-card-records'&&method==='GET')return Promise.resolve({items:stored?[record]:[],hasMore:false,dataDurability:'Temporary data'});
    if(path==='v1/credit-card-records'&&method==='POST'){expect(body).toEqual({operationId:'a'.repeat(32),card:{issuerName:'Example Issuer',cardType:'Rewards',
      cardNumberLast4:'4821',sourceFinancialFactId:'fact-card',sourceRevision:2}});stored=true;return Promise.resolve(record);}
    throw new Error('unexpected route');
  });
  vi.spyOn(crypto,'randomUUID').mockReturnValue('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa');mount(api);const user=userEvent.setup();
  await user.click(await screen.findByRole('button',{name:'Add credit card'}));const drawer=screen.getByRole('dialog',{name:'Add credit card'});
  await user.click(within(drawer).getByRole('combobox',{name:'Reviewed credit card source'}));
  await user.click(screen.getByText(/Card statement closing balance ·/,{selector:'.ant-select-item-option-content'}));
  await user.type(within(drawer).getByLabelText('Issuer name'),'Example Issuer');await user.type(within(drawer).getByLabelText('Card type'),'Rewards');
  await user.type(within(drawer).getByLabelText('Last four digits'),'4821');expect(drawer).toHaveTextContent(/Source balance:.*2,510\.25.*2026-10-01/);
  await user.click(within(drawer).getByRole('button',{name:'Save reviewed credit card'}));
  await user.click(await screen.findByRole('button',{name:'View card details'}));const details=screen.getByRole('dialog',{name:'Credit card details'});
  expect(details).toHaveTextContent('•••• 4821');expect(details).toHaveTextContent(/2,510\.25 as of 2026-10-01/);
  expect(details).toHaveTextContent('not a live issuer connection');expect(details).toHaveTextContent('does not store a full card number');
  expect(details).not.toHaveTextContent('1234567812344821');
  expect(calls.mock.calls.filter(([,method])=>method==='POST')).toHaveLength(1);
});

it('keeps records readable without update access and exposes governed Request Access actions',async()=>{
  const api=new AccessApi(session);vi.spyOn(api,'product').mockResolvedValue({items:[record],hasMore:false,dataDurability:'Temporary data'});
  vi.spyOn(api,'request').mockResolvedValue({tenantId:'T001',actorId:'A100',policyVersion:1,subjectVersion:2,items:[]});mount(api,false);
  expect(await screen.findByText('Example Issuer · •••• 4821')).toBeVisible();expect(screen.queryByRole('button',{name:'Add credit card'})).not.toBeInTheDocument();
  expect(screen.getByText('Adding or correcting a credit card needs access')).toBeVisible();
  expect(screen.getByText('Correction access required')).toBeVisible();expect(screen.getByRole('button',{name:'View card details'})).toBeVisible();
});

it('corrects metadata with immutable source identifiers and reads append-only history',async()=>{
  const api=new AccessApi(session);const corrected={...record,revision:2,card:{...record.card,issuerName:'Renamed Issuer',cardNumberLast4:'7314'}};
  const request=vi.spyOn(api,'product').mockImplementation((path,method,body)=>{
    if(path==='v1/credit-card-records'&&method==='GET')return Promise.resolve({items:[record],hasMore:false,dataDurability:'Temporary data'});
    if(path.endsWith('/corrections')){expect(body).toMatchObject({expectedRevision:1,card:{sourceFinancialFactId:'fact-card',sourceRevision:2}});return Promise.resolve(corrected);}
    if(path.endsWith('/history'))return Promise.resolve({items:[corrected,record],hasMore:false,nextBeforeRevision:null});
    throw new Error('unexpected route');
  });
  vi.spyOn(crypto,'randomUUID').mockReturnValue('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa');mount(api);const user=userEvent.setup();
  await user.click(await screen.findByRole('button',{name:'Correct card metadata'}));const edit=screen.getByRole('dialog',{name:'Correct credit card metadata'});
  expect(within(edit).queryByRole('combobox',{name:'Reviewed credit card source'})).not.toBeInTheDocument();
  await user.clear(within(edit).getByLabelText('Issuer name'));await user.type(within(edit).getByLabelText('Issuer name'),'Renamed Issuer');
  await user.click(within(edit).getByRole('button',{name:'Save correction'}));
  await user.click(await screen.findByRole('button',{name:'View change history'}));const history=await screen.findByRole('region',{name:'Reviewed credit card change history'});
  expect(await within(history).findByText('Version 2')).toBeVisible();expect(within(history).getByText('Version 1')).toBeVisible();
  expect(request.mock.calls.filter(([,method])=>method==='POST')).toHaveLength(1);
});

it('does not query records without source-document access and reports owner failures distinctly',async()=>{
  const api=new AccessApi(session);const request=vi.spyOn(api,'product');mount(api,true,false);
  expect(screen.getByText(/Document access is required/)).toBeVisible();expect(request).not.toHaveBeenCalled();cleanup();
  const failing=new AccessApi(session);vi.spyOn(failing,'product').mockRejectedValue(new AccessApiError(503,'unavailable'));mount(failing);
  expect(await screen.findByText('Credit card records are unavailable. Try again.')).toBeVisible();
});
