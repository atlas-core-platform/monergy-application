import { FoundationProvider } from '@monergy/ui-foundation';
import { cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, it, vi } from 'vitest';
import { AccessApi, AccessApiError } from '../src/access/accessApi';
import MonthlyEmiSharePanel from '../src/product/MonthlyEmiSharePanel';
import type { ProductLoanRecord } from '../src/product/productApi';
import type { MonthlyCashflowLine, MonthlyCashflowPosition, MonthlyCashflowResult, MonthlyEmiShareResult } from '../src/product/monthlyCashflowApi';

const session={tenantId:'T001',actorId:'actor-001',authenticationContextId:'session-001',subjectVersion:2,expiresAt:'2027-01-01T00:00:00Z'};
const income:MonthlyCashflowLine={lineId:'a'.repeat(32),side:'INCOME',kind:'SALARY',name:'Salary',amount:'10000.00',currency:'INR',valueDate:'2026-09-30',sourceNote:'Customer reviewed',record:null,member:'Customer'};
const position:MonthlyCashflowPosition={positionId:'cashflow-position',operationId:'c'.repeat(32),customerId:'customer-1',position:{asOfDate:'2026-09-30',currency:'INR',lines:[income],incomeComplete:true,expensesComplete:true,replacesPositionId:null,household:'Customer household'},lines:[{line:income,source:null}],recordedBy:'actor-001',recordedAt:'2026-10-01T00:00:00Z'};
const cashflow:MonthlyCashflowResult={calculationId:'cashflow-1',operationId:'d'.repeat(32),customerId:'customer-1',position,ruleId:'monergy.monthly-cashflow',ruleVersion:'1.0.0',definitionHash:'e'.repeat(64),numericSemantics:'Exact customer-reviewed monthly cash flow.',state:'CALCULATED',totalIncome:'10000.00',totalExpenses:'0.00',balance:'10000.00',issues:[],recordedBy:'actor-001',recordedAt:'2026-10-01T00:00:00Z'};
const loan:ProductLoanRecord={loanRecordId:'loan-1',operationId:'1'.repeat(32),customerId:'customer-1',loan:{name:'Home loan',lender:'Bank',loanType:'HOME',sanctionAmount:100000,outstanding:80000,interestRatePercent:8,emi:2500,tenureMonths:60,startDate:'2026-01-01',endDate:'2030-12-31',balanceAsOfDate:'2026-09-30',sourceFinancialFactId:'fact-1',sourceRevision:1},currency:'INR',sourceProvenanceId:'provenance-1',documentVersionId:'document-1',evidenceId:'evidence-1',recordedBy:'actor-001',recordedAt:'2026-09-30T00:00:00Z',revision:2,lifecycleStatus:'ACTIVE',lifecycleAsOfDate:'2026-09-30'};
const receipt=(operationId:string):MonthlyEmiShareResult=>({calculationId:'emi-share-1',operationId,customerId:'customer-1',sourceMonthlyCashflow:cashflow,sourceLoans:[loan],activeLoansComplete:true,ruleId:'monergy.customer-reviewed-monthly-emi-share',ruleVersion:'1.0.0',definitionHash:'f'.repeat(64),numericSemantics:'Exact monthly EMI share from saved records.',state:'CALCULATED',monthlyIncome:'10000.00',monthlyEmi:'2500.00',ratePercent:'25.00',issues:[],recordedBy:'actor-001',recordedAt:'2026-10-02T00:00:00Z'});
const page=(items:MonthlyEmiShareResult[]=[])=>({items,hasMore:false,nextBeforeId:null});
const view=(api:AccessApi)=><FoundationProvider appearance="midnight"><MonthlyEmiSharePanel api={api} customerId="customer-1" source={cashflow}/></FoundationProvider>;
afterEach(()=>{cleanup();vi.restoreAllMocks();});
const select=async(user:ReturnType<typeof userEvent.setup>)=>{await screen.findByText(/Home loan/);await user.click(screen.getByRole('checkbox',{name:/Home loan/}));await user.click(screen.getByRole('checkbox',{name:/selected every active loan EMI/}));};

it('posts only exact source references and displays the server-calculated rate',async()=>{
  const api=new AccessApi(session);const request=vi.spyOn(api,'product').mockImplementation((path,method,body)=>method==='POST'?Promise.resolve(receipt((body as {operationId:string}).operationId)):path==='v1/loan-records'?Promise.resolve({items:[loan],hasMore:false,dataDurability:'Temporary data'}):Promise.resolve(page()));
  render(view(api));const user=userEvent.setup();await select(user);await user.click(screen.getByRole('button',{name:'Calculate and save monthly EMI share'}));expect(await screen.findByText('25.00%')).toBeVisible();expect(screen.getByText(/This descriptive rate is not verified debt/)).toBeVisible();
  const write=request.mock.calls.find(([,method])=>method==='POST');expect(write?.[2]).toEqual(expect.objectContaining({monthlyCashflowCalculationId:'cashflow-1',loans:[{loanRecordId:'loan-1',revision:2}],activeLoansComplete:true}));expect(JSON.stringify(write?.[2])).not.toContain('2500');expect(Object.keys(write?.[2] as object).sort()).toEqual(['activeLoansComplete','loans','monthlyCashflowCalculationId','operationId']);
});
it('retries one identical frozen request after an uncertain response',async()=>{
  const api=new AccessApi(session);let attempts=0;const request=vi.spyOn(api,'product').mockImplementation((path,method,body)=>method==='POST'?(++attempts===1?Promise.reject(new AccessApiError(503,'unavailable')):Promise.resolve(receipt((body as {operationId:string}).operationId))):path==='v1/loan-records'?Promise.resolve({items:[loan],hasMore:false,dataDurability:'Temporary data'}):Promise.resolve(page()));
  render(view(api));const user=userEvent.setup();await select(user);await user.click(screen.getByRole('button',{name:'Calculate and save monthly EMI share'}));await screen.findByText(/not confirmed/);await user.click(screen.getByRole('button',{name:'Retry EMI-share confirmation'}));await screen.findByText('25.00%');const writes=request.mock.calls.filter(([,method])=>method==='POST');expect(writes).toHaveLength(2);expect(writes[0]?.[2]).toEqual(writes[1]?.[2]);
});
it('reopens the immutable saved EMI share without a duplicate write',async()=>{
  const api=new AccessApi(session);const request=vi.spyOn(api,'product').mockImplementation(path=>path==='v1/loan-records'?Promise.resolve({items:[loan],hasMore:false,dataDurability:'Temporary data'}):Promise.resolve(page([receipt('2'.repeat(32))])));render(view(api));expect(await screen.findByText('25.00%')).toBeVisible();expect(request.mock.calls.every(([,method])=>method==='GET')).toBe(true);
});
