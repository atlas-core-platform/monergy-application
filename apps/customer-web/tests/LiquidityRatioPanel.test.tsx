import { FoundationProvider } from '@monergy/ui-foundation';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, it, vi } from 'vitest';
import { AccessApi, AccessApiError } from '../src/access/accessApi';
import LiquidityRatioPanel from '../src/product/LiquidityRatioPanel';
import type { MonthlyCashflowLine, MonthlyCashflowPosition, MonthlyCashflowResult } from '../src/product/monthlyCashflowApi';
import type { LiquidityRatioResult, NetWorthLine, NetWorthPosition, NetWorthResult } from '../src/product/netWorthApi';

const session={tenantId:'T001',actorId:'actor-001',authenticationContextId:'session-001',subjectVersion:2,expiresAt:'2027-01-01T00:00:00Z'};
const cash:NetWorthLine={lineId:'a'.repeat(32),side:'ASSET',kind:'CASH',name:'Cash reserve',amount:'1200.00',currency:'INR',valueDate:'2026-10-01',sourceNote:'Reviewed cash',record:null};
const bank:NetWorthLine={lineId:'b'.repeat(32),side:'ASSET',kind:'BANK_ACCOUNT',name:'Savings account',amount:'600.00',currency:'INR',valueDate:'2026-10-01',sourceNote:'Reviewed bank account',record:null};
const property:NetWorthLine={lineId:'c'.repeat(32),side:'ASSET',kind:'PROPERTY',name:'Home',amount:'5000.00',currency:'INR',valueDate:'2026-10-01',sourceNote:'Reviewed property',record:null};
const netPosition:NetWorthPosition={positionId:'net-position',operationId:'d'.repeat(32),customerId:'customer-1',position:{asOfDate:'2026-10-01',currency:'INR',lines:[cash,bank,property],assetsComplete:true,liabilitiesComplete:true,replacesPositionId:null},lines:[cash,bank,property].map(line=>({line,source:null})),recordedBy:'actor-001',recordedAt:'2026-10-02T00:00:00Z'};
const net:NetWorthResult={calculationId:'networth-1',operationId:'e'.repeat(32),customerId:'customer-1',position:netPosition,ruleId:'monergy.net-worth',ruleVersion:'1.0.0',definitionHash:'f'.repeat(64),numericSemantics:'Exact reviewed assets minus liabilities.',state:'CALCULATED',totalAssets:'6800.00',totalLiabilities:'0.00',netWorth:'6800.00',issues:[],recordedBy:'actor-001',recordedAt:'2026-10-02T00:00:00Z'};
const income:MonthlyCashflowLine={lineId:'1'.repeat(32),side:'INCOME',kind:'SALARY',name:'Salary',amount:'5000.00',currency:'INR',valueDate:'2026-09-30',sourceNote:'Reviewed salary',record:null,member:'Self'};
const expense:MonthlyCashflowLine={lineId:'2'.repeat(32),side:'EXPENSE',kind:'HOUSEHOLD',name:'Monthly expenses',amount:'600.00',currency:'INR',valueDate:'2026-09-30',sourceNote:'Reviewed expenses',record:null,member:'Self'};
const cashflowPosition:MonthlyCashflowPosition={positionId:'cashflow-position',operationId:'3'.repeat(32),customerId:'customer-1',position:{asOfDate:'2026-09-30',currency:'INR',lines:[income,expense],incomeComplete:true,expensesComplete:true,replacesPositionId:null,household:'Self'},lines:[income,expense].map(line=>({line,source:null})),recordedBy:'actor-001',recordedAt:'2026-10-01T00:00:00Z'};
const cashflow:MonthlyCashflowResult={calculationId:'cashflow-1',operationId:'4'.repeat(32),customerId:'customer-1',position:cashflowPosition,ruleId:'monergy.monthly-cashflow',ruleVersion:'1.0.0',definitionHash:'5'.repeat(64),numericSemantics:'Exact complete month.',state:'CALCULATED',totalIncome:'5000.00',totalExpenses:'600.00',balance:'4400.00',issues:[],recordedBy:'actor-001',recordedAt:'2026-10-01T00:00:00Z'};
const receipt=(operationId:string):LiquidityRatioResult=>({calculationId:'liquidity-1',operationId,customerId:'customer-1',sourceNetWorth:net,sourceMonthlyCashflow:cashflow,liquidAssetLineIds:[cash.lineId,bank.lineId],liquidAssetsComplete:true,ruleId:'monergy.liquidity-ratio',ruleVersion:'1.0.0',definitionHash:'6'.repeat(64),numericSemantics:'Reviewed cash and bank accounts divided by reviewed monthly expenses.',state:'CALCULATED',liquidAssets:'1800.00',monthlyExpenses:'600.00',ratioMonths:'3.00',issues:[],recordedBy:'actor-001',recordedAt:'2026-10-03T00:00:00Z'});
const page=<T,>(items:T[]=[])=>({items,hasMore:false,nextBeforeId:null});
const view=(api:AccessApi)=><FoundationProvider appearance="midnight"><LiquidityRatioPanel api={api} customerId="customer-1" source={net}/></FoundationProvider>;
afterEach(()=>{cleanup();vi.restoreAllMocks();});
async function chooseExpenseMonth(){
  fireEvent.mouseDown(screen.getByRole('combobox',{name:'Reviewed expense month'}));
  const matches=await screen.findAllByText('2026-09-30 · INR · expenses INR 600.00');
  const option=matches[matches.length-1];if(!option)throw new Error('Missing expense month option');fireEvent.click(option);
}

it('uses exact source identifiers and every eligible liquid asset without posting money',async()=>{
  const api=new AccessApi(session);const request=vi.spyOn(api,'product').mockImplementation((path,method,body)=>method==='GET'?Promise.resolve(path.startsWith('v1/monthly-cashflow')?page([cashflow]):page()):Promise.resolve(receipt((body as {operationId:string}).operationId)));
  render(view(api));const user=userEvent.setup();const button=screen.getByRole('button',{name:'Calculate and save liquidity ratio'});expect(button).toBeDisabled();await chooseExpenseMonth();await user.click(screen.getByRole('checkbox',{name:/Cash reserve/}));await user.click(screen.getByRole('checkbox',{name:/Savings account/}));await user.click(screen.getByRole('checkbox',{name:/selected every cash and bank-account asset/}));expect(button).toBeEnabled();await user.click(button);
  expect(await screen.findByText('3.00 months')).toBeVisible();expect(screen.getByText(/not independent verification/)).toBeVisible();const write=request.mock.calls.find(([,method])=>method==='POST');expect(write?.[2]).toMatchObject({netWorthCalculationId:'networth-1',monthlyCashflowCalculationId:'cashflow-1',liquidAssetLineIds:[cash.lineId,bank.lineId],liquidAssetsComplete:true});expect(JSON.stringify(write?.[2])).not.toContain('1800.00');expect(JSON.stringify(write?.[2])).not.toContain('600.00');
});
it('retries one identical liquidity request after an uncertain response',async()=>{
  const api=new AccessApi(session);let attempts=0;const request=vi.spyOn(api,'product').mockImplementation((path,method,body)=>method==='GET'?Promise.resolve(path.startsWith('v1/monthly-cashflow')?page([cashflow]):page()):++attempts===1?Promise.reject(new AccessApiError(503,'unavailable')):Promise.resolve(receipt((body as {operationId:string}).operationId)));
  render(view(api));const user=userEvent.setup();await chooseExpenseMonth();await user.click(screen.getByRole('checkbox',{name:/Cash reserve/}));await user.click(screen.getByRole('checkbox',{name:/Savings account/}));await user.click(screen.getByRole('checkbox',{name:/selected every cash and bank-account asset/}));await user.click(screen.getByRole('button',{name:'Calculate and save liquidity ratio'}));await screen.findByText(/not confirmed/);await user.click(screen.getByRole('button',{name:'Retry liquidity confirmation'}));await screen.findByText('3.00 months');const writes=request.mock.calls.filter(([,method])=>method==='POST');expect(writes).toHaveLength(2);expect(writes[0]?.[2]).toEqual(writes[1]?.[2]);
});
