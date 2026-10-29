import { FoundationProvider } from '@monergy/ui-foundation';
import { cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, it, vi } from 'vitest';
import { AccessApi, AccessApiError } from '../src/access/accessApi';
import DebtServiceRatioPanel from '../src/product/DebtServiceRatioPanel';
import type { DebtServiceRatioResult, MonthlyCashflowLine, MonthlyCashflowPosition, MonthlyCashflowResult } from '../src/product/monthlyCashflowApi';

const session={tenantId:'T001',actorId:'actor-001',authenticationContextId:'session-001',subjectVersion:2,expiresAt:'2027-01-01T00:00:00Z'};
const income:MonthlyCashflowLine={lineId:'a'.repeat(32),side:'INCOME',kind:'SALARY',name:'Salary',amount:'1000.00',currency:'INR',valueDate:'2026-09-30',sourceNote:'Reviewed salary',record:null,member:'Self'};
const debt:MonthlyCashflowLine={lineId:'b'.repeat(32),side:'EXPENSE',kind:'LOAN',name:'Home loan EMI',amount:'250.00',currency:'INR',valueDate:'2026-09-30',sourceNote:'Reviewed loan payment',record:null,member:'Self'};
const position:MonthlyCashflowPosition={positionId:'position-1',operationId:'c'.repeat(32),customerId:'customer-1',position:{asOfDate:'2026-09-30',currency:'INR',lines:[income,debt],incomeComplete:true,expensesComplete:true,replacesPositionId:null,household:'Self'},lines:[{line:income,source:null},{line:debt,source:null}],recordedBy:'actor-001',recordedAt:'2026-10-01T00:00:00Z'};
const source:MonthlyCashflowResult={calculationId:'cashflow-1',operationId:'d'.repeat(32),customerId:'customer-1',position,ruleId:'monergy.monthly-cashflow',ruleVersion:'1.0.0',definitionHash:'e'.repeat(64),numericSemantics:'Exact saved monthly review',state:'CALCULATED',totalIncome:'1000.00',totalExpenses:'250.00',balance:'750.00',issues:[],recordedBy:'actor-001',recordedAt:'2026-10-01T00:00:00Z'};
const receipt=(operationId:string):DebtServiceRatioResult=>({calculationId:'debt-ratio-1',operationId,customerId:'customer-1',sourceCalculation:source,debtPaymentLineIds:[debt.lineId],debtPaymentsComplete:true,ruleId:'monergy.monthly-debt-service-ratio',ruleVersion:'1.0.0',definitionHash:'f'.repeat(64),numericSemantics:'Selected customer-reviewed monthly debt payments divided by reviewed monthly income.',state:'CALCULATED',monthlyIncome:'1000.00',monthlyDebtPayments:'250.00',ratioPercent:'25.00',issues:[],recordedBy:'actor-001',recordedAt:'2026-10-01T00:00:00Z'});
const view=(api:AccessApi)=><FoundationProvider appearance="midnight"><DebtServiceRatioPanel api={api} customerId="customer-1" source={source}/></FoundationProvider>;
afterEach(()=>{cleanup();vi.restoreAllMocks();});

it('requires explicit debt selection and completeness before saving an exact ratio',async()=>{
  const api=new AccessApi(session);const request=vi.spyOn(api,'product').mockImplementation((path,_method,body)=>path.endsWith('debt-service-ratios')&&body?Promise.resolve(receipt((body as {operationId:string}).operationId)):Promise.resolve({items:[],hasMore:false,nextBeforeId:null}));
  render(view(api));const user=userEvent.setup();const button=screen.getByRole('button',{name:'Calculate and save ratio'});expect(button).toBeDisabled();await user.click(screen.getByRole('checkbox',{name:/Home loan EMI/}));expect(button).toBeDisabled();await user.click(screen.getByRole('checkbox',{name:/selected every monthly debt payment/}));expect(button).toBeEnabled();await user.click(button);
  expect(await screen.findByText('25.00%')).toBeVisible();expect(screen.getByText(/not lender DTI\/FOIR/)).toBeVisible();const write=request.mock.calls.find(([,method])=>method==='POST');expect(write?.[2]).toMatchObject({monthlyCashflowCalculationId:'cashflow-1',debtPaymentLineIds:[debt.lineId],debtPaymentsComplete:true});
});
it('retries the identical ratio request after an uncertain response',async()=>{
  const api=new AccessApi(session);let attempts=0;const request=vi.spyOn(api,'product').mockImplementation((_path,_method,body)=>{if(!body)return Promise.resolve({items:[],hasMore:false,nextBeforeId:null});const input=body as {operationId:string};return ++attempts===1?Promise.reject(new AccessApiError(503,'unavailable')):Promise.resolve(receipt(input.operationId));});
  render(view(api));const user=userEvent.setup();await user.click(screen.getByRole('checkbox',{name:/Home loan EMI/}));await user.click(screen.getByRole('checkbox',{name:/selected every monthly debt payment/}));await user.click(screen.getByRole('button',{name:'Calculate and save ratio'}));await screen.findByText(/not confirmed/);await user.click(screen.getByRole('button',{name:'Retry ratio confirmation'}));await screen.findByText('25.00%');const writes=request.mock.calls.filter(([,method])=>method==='POST');expect(writes).toHaveLength(2);expect(writes[0]?.[2]).toEqual(writes[1]?.[2]);
});
