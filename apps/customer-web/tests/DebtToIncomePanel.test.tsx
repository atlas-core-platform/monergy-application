import { FoundationProvider } from '@monergy/ui-foundation';
import { cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, it, vi } from 'vitest';
import { AccessApi, AccessApiError } from '../src/access/accessApi';
import DebtToIncomePanel from '../src/product/DebtToIncomePanel';
import type { DebtServiceRatioResult, DebtToIncomeResult, MonthlyCashflowLine, MonthlyCashflowPosition, MonthlyCashflowResult } from '../src/product/monthlyCashflowApi';

const session={tenantId:'T001',actorId:'actor-001',authenticationContextId:'session-001',subjectVersion:2,expiresAt:'2027-01-01T00:00:00Z'};
const income:MonthlyCashflowLine={lineId:'a'.repeat(32),side:'INCOME',kind:'SALARY',name:'Salary',amount:'1000.00',currency:'INR',valueDate:'2026-09-30',sourceNote:'Reviewed salary',record:null,member:'Self'};
const debt:MonthlyCashflowLine={lineId:'b'.repeat(32),side:'EXPENSE',kind:'LOAN',name:'Home loan EMI',amount:'250.00',currency:'INR',valueDate:'2026-09-30',sourceNote:'Reviewed loan payment',record:null,member:'Self'};
const position:MonthlyCashflowPosition={positionId:'position-1',operationId:'c'.repeat(32),customerId:'customer-1',position:{asOfDate:'2026-09-30',currency:'INR',lines:[income,debt],incomeComplete:true,expensesComplete:true,replacesPositionId:null,household:'Self'},lines:[{line:income,source:null},{line:debt,source:null}],recordedBy:'actor-001',recordedAt:'2026-10-01T00:00:00Z'};
const cashflow:MonthlyCashflowResult={calculationId:'cashflow-1',operationId:'d'.repeat(32),customerId:'customer-1',position,ruleId:'monergy.monthly-cashflow',ruleVersion:'1.0.0',definitionHash:'e'.repeat(64),numericSemantics:'Exact saved monthly review',state:'CALCULATED',totalIncome:'1000.00',totalExpenses:'250.00',balance:'750.00',issues:[],recordedBy:'actor-001',recordedAt:'2026-10-01T00:00:00Z'};
const source:DebtServiceRatioResult={calculationId:'debt-ratio-1',operationId:'f'.repeat(32),customerId:'customer-1',sourceCalculation:cashflow,debtPaymentLineIds:[debt.lineId],debtPaymentsComplete:true,ruleId:'monergy.monthly-debt-service-ratio',ruleVersion:'1.0.0',definitionHash:'1'.repeat(64),numericSemantics:'Exact saved debt review',state:'CALCULATED',monthlyIncome:'1000.00',monthlyDebtPayments:'250.00',ratioPercent:'25.00',issues:[],recordedBy:'actor-001',recordedAt:'2026-10-01T00:00:00Z'};
const receipt=(operationId:string):DebtToIncomeResult=>({calculationId:'dti-1',operationId,customerId:'customer-1',sourceDebtServiceRatio:source,grossIncomeConfirmed:true,timeBasis:'MONTHLY',incomeBasis:'GROSS_INCOME',ruleId:'monergy.customer-reviewed-monthly-debt-to-income',ruleVersion:'1.0.0',definitionHash:'2'.repeat(64),numericSemantics:'Exact customer-reviewed monthly DTI.',state:'CALCULATED',monthlyDebtObligations:'250.00',grossMonthlyIncome:'1000.00',ratioPercent:'25.00',issues:[],recordedBy:'actor-001',recordedAt:'2026-10-02T00:00:00Z'});
const view=(api:AccessApi)=><FoundationProvider appearance="midnight"><DebtToIncomePanel api={api} customerId="customer-1" source={source}/></FoundationProvider>;
afterEach(()=>{cleanup();vi.restoreAllMocks();});

it('posts only the exact source receipt and explicit gross-income confirmation',async()=>{
  const api=new AccessApi(session);const request=vi.spyOn(api,'product').mockImplementation((path,_method,body)=>path.endsWith('debt-to-income-ratios')&&body?Promise.resolve(receipt((body as {operationId:string}).operationId)):Promise.resolve({items:[],hasMore:false,nextBeforeId:null}));
  render(view(api));const user=userEvent.setup();const button=screen.getByRole('button',{name:'Calculate and save monthly DTI'});expect(button).toBeDisabled();await user.click(screen.getByRole('checkbox',{name:/gross monthly income before deductions/}));expect(button).toBeEnabled();await user.click(button);
  expect(await screen.findByText('25.00%')).toBeVisible();expect(screen.getByText(/not independent verification/)).toBeVisible();const write=request.mock.calls.find(([,method])=>method==='POST');expect(write?.[2]).toEqual({operationId:expect.stringMatching(/^[a-f0-9]{32}$/u),debtServiceRatioCalculationId:'debt-ratio-1',grossIncomeConfirmed:true});
});

it('retries the identical bounded request after an uncertain response',async()=>{
  const api=new AccessApi(session);let attempts=0;const request=vi.spyOn(api,'product').mockImplementation((_path,_method,body)=>{if(!body)return Promise.resolve({items:[],hasMore:false,nextBeforeId:null});const input=body as {operationId:string};return ++attempts===1?Promise.reject(new AccessApiError(503,'unavailable')):Promise.resolve(receipt(input.operationId));});
  render(view(api));const user=userEvent.setup();await user.click(screen.getByRole('checkbox',{name:/gross monthly income before deductions/}));await user.click(screen.getByRole('button',{name:'Calculate and save monthly DTI'}));await screen.findByText(/not confirmed/);await user.click(screen.getByRole('button',{name:'Retry DTI confirmation'}));await screen.findByText('25.00%');const writes=request.mock.calls.filter(([,method])=>method==='POST');expect(writes).toHaveLength(2);expect(writes[0]?.[2]).toEqual(writes[1]?.[2]);
});
