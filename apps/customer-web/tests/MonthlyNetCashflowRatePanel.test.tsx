import { FoundationProvider } from '@monergy/ui-foundation';
import { cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, it, vi } from 'vitest';
import { AccessApi, AccessApiError } from '../src/access/accessApi';
import MonthlyNetCashflowRatePanel from '../src/product/MonthlyNetCashflowRatePanel';
import type { MonthlyCashflowLine, MonthlyCashflowPosition, MonthlyCashflowResult, MonthlyNetCashflowRateResult } from '../src/product/monthlyCashflowApi';

const session={tenantId:'T001',actorId:'actor-001',authenticationContextId:'session-001',subjectVersion:2,expiresAt:'2027-01-01T00:00:00Z'};
const income:MonthlyCashflowLine={lineId:'a'.repeat(32),side:'INCOME',kind:'SALARY',name:'Salary',amount:'10000.00',currency:'INR',valueDate:'2026-09-30',sourceNote:'Customer reviewed',record:null,member:'Customer'};
const expense:MonthlyCashflowLine={lineId:'b'.repeat(32),side:'EXPENSE',kind:'HOUSEHOLD',name:'Household costs',amount:'7000.00',currency:'INR',valueDate:'2026-09-30',sourceNote:'Customer reviewed',record:null,member:'Household'};
const position:MonthlyCashflowPosition={positionId:'cashflow-position',operationId:'c'.repeat(32),customerId:'customer-1',position:{asOfDate:'2026-09-30',currency:'INR',lines:[income,expense],incomeComplete:true,expensesComplete:true,replacesPositionId:null,household:'Customer household'},lines:[income,expense].map(line=>({line,source:null})),recordedBy:'actor-001',recordedAt:'2026-10-01T00:00:00Z'};
const cashflow:MonthlyCashflowResult={calculationId:'cashflow-1',operationId:'d'.repeat(32),customerId:'customer-1',position,ruleId:'monergy.monthly-cashflow',ruleVersion:'1.0.0',definitionHash:'e'.repeat(64),numericSemantics:'Exact customer-reviewed monthly cash flow.',state:'CALCULATED',totalIncome:'10000.00',totalExpenses:'7000.00',balance:'3000.00',issues:[],recordedBy:'actor-001',recordedAt:'2026-10-01T00:00:00Z'};
const receipt=(operationId:string):MonthlyNetCashflowRateResult=>({calculationId:'cashflow-rate-1',operationId,customerId:'customer-1',sourceMonthlyCashflow:cashflow,ruleId:'monergy.monthly-net-cashflow-rate',ruleVersion:'1.0.0',definitionHash:'f'.repeat(64),numericSemantics:'Exact signed rate from one saved receipt.',state:'CALCULATED',monthlyIncome:'10000.00',monthlyExpenses:'7000.00',netCashflow:'3000.00',ratePercent:'30.00',issues:[],recordedBy:'actor-001',recordedAt:'2026-10-02T00:00:00Z'});
const page=(items:MonthlyNetCashflowRateResult[]=[])=>({items,hasMore:false,nextBeforeId:null});
const view=(api:AccessApi)=><FoundationProvider appearance="midnight"><MonthlyNetCashflowRatePanel api={api} customerId="customer-1" source={cashflow}/></FoundationProvider>;
afterEach(()=>{cleanup();vi.restoreAllMocks();});

it('posts only the exact source identifier and displays the server-calculated signed rate',async()=>{
  const api=new AccessApi(session);const request=vi.spyOn(api,'product').mockImplementation((_path,method,body)=>method==='GET'?Promise.resolve(page()):Promise.resolve(receipt((body as {operationId:string}).operationId)));
  render(view(api));const user=userEvent.setup();await user.click(screen.getByRole('button',{name:'Calculate and save net cash-flow rate'}));
  expect(await screen.findByText('30.00%')).toBeVisible();expect(screen.getByText(/not verified income or spending/)).toBeVisible();
  const write=request.mock.calls.find(([,method])=>method==='POST');expect(write?.[2]).toEqual(expect.objectContaining({monthlyCashflowCalculationId:'cashflow-1'}));expect(Object.keys(write?.[2] as object).sort()).toEqual(['monthlyCashflowCalculationId','operationId']);expect(JSON.stringify(write?.[2])).not.toContain('10000.00');
});
it('retries one identical request after an uncertain response',async()=>{
  const api=new AccessApi(session);let attempts=0;const request=vi.spyOn(api,'product').mockImplementation((_path,method,body)=>method==='GET'?Promise.resolve(page()):++attempts===1?Promise.reject(new AccessApiError(503,'unavailable')):Promise.resolve(receipt((body as {operationId:string}).operationId)));
  render(view(api));const user=userEvent.setup();await user.click(screen.getByRole('button',{name:'Calculate and save net cash-flow rate'}));await screen.findByText(/not confirmed/);await user.click(screen.getByRole('button',{name:'Retry net cash-flow rate confirmation'}));await screen.findByText('30.00%');
  const writes=request.mock.calls.filter(([,method])=>method==='POST');expect(writes).toHaveLength(2);expect(writes[0]?.[2]).toEqual(writes[1]?.[2]);
});
it('reopens the immutable saved rate for the exact cash-flow source without a duplicate write',async()=>{
  const api=new AccessApi(session);const request=vi.spyOn(api,'product').mockResolvedValue(page([receipt('1'.repeat(32))]));render(view(api));
  expect(await screen.findByText('30.00%')).toBeVisible();expect(request.mock.calls.every(([,method])=>method==='GET')).toBe(true);
});
