import { FoundationProvider } from '@monergy/ui-foundation';
import { cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, it, vi } from 'vitest';
import { AccessApi, AccessApiError } from '../src/access/accessApi';
import AssetAllocationPanel from '../src/product/AssetAllocationPanel';
import type { AssetAllocationResult, NetWorthLine, NetWorthPosition, NetWorthResult } from '../src/product/netWorthApi';

const session={tenantId:'T001',actorId:'actor-001',authenticationContextId:'session-001',subjectVersion:2,expiresAt:'2027-01-01T00:00:00Z'};
const cash:NetWorthLine={lineId:'a'.repeat(32),side:'ASSET',kind:'CASH',name:'Cash reserve',amount:'1000.00',currency:'INR',valueDate:'2026-10-01',sourceNote:'Reviewed cash',record:null};
const property:NetWorthLine={lineId:'b'.repeat(32),side:'ASSET',kind:'PROPERTY',name:'Home',amount:'3000.00',currency:'INR',valueDate:'2026-10-01',sourceNote:'Reviewed property',record:null};
const position:NetWorthPosition={positionId:'net-position',operationId:'c'.repeat(32),customerId:'customer-1',position:{asOfDate:'2026-10-01',currency:'INR',lines:[cash,property],assetsComplete:true,liabilitiesComplete:true,replacesPositionId:null},lines:[cash,property].map(line=>({line,source:null})),recordedBy:'actor-001',recordedAt:'2026-10-02T00:00:00Z'};
const net:NetWorthResult={calculationId:'networth-1',operationId:'d'.repeat(32),customerId:'customer-1',position,ruleId:'monergy.net-worth',ruleVersion:'1.0.0',definitionHash:'e'.repeat(64),numericSemantics:'Exact reviewed assets minus liabilities.',state:'CALCULATED',totalAssets:'4000.00',totalLiabilities:'0.00',netWorth:'4000.00',issues:[],recordedBy:'actor-001',recordedAt:'2026-10-02T00:00:00Z'};
const receipt=(operationId:string):AssetAllocationResult=>({calculationId:'allocation-1',operationId,customerId:'customer-1',sourceNetWorth:net,categories:[{category:'CASH',amount:'1000.00',percentage:'25.00'},{category:'PROPERTY',amount:'3000.00',percentage:'75.00'}],ruleId:'monergy.asset-allocation',ruleVersion:'1.0.0',definitionHash:'f'.repeat(64),numericSemantics:'Canonical saved source composition.',state:'CALCULATED',totalAssets:'4000.00',issues:[],recordedBy:'actor-001',recordedAt:'2026-10-03T00:00:00Z'});
const page=(items:AssetAllocationResult[]=[])=>({items,hasMore:false,nextBeforeId:null});
const view=(api:AccessApi)=><FoundationProvider appearance="midnight"><AssetAllocationPanel api={api} customerId="customer-1" source={net}/></FoundationProvider>;
afterEach(()=>{cleanup();vi.restoreAllMocks();});

it('posts only the exact source identifier and displays server-resolved categories',async()=>{
  const api=new AccessApi(session);const request=vi.spyOn(api,'product').mockImplementation((_path,method,body)=>method==='GET'?Promise.resolve(page()):Promise.resolve(receipt((body as {operationId:string}).operationId)));
  render(view(api));const user=userEvent.setup();await user.click(screen.getByRole('button',{name:'Calculate and save asset allocation'}));
  expect(await screen.findByText('INR 1,000.00 · 25.00%')).toBeVisible();expect(screen.getByText(/not verified ownership/)).toBeVisible();
  const write=request.mock.calls.find(([,method])=>method==='POST');expect(write?.[2]).toEqual(expect.objectContaining({netWorthCalculationId:'networth-1'}));expect(Object.keys(write?.[2] as object).sort()).toEqual(['netWorthCalculationId','operationId']);expect(JSON.stringify(write?.[2])).not.toContain('4000.00');
});
it('retries one identical request after an uncertain response',async()=>{
  const api=new AccessApi(session);let attempts=0;const request=vi.spyOn(api,'product').mockImplementation((_path,method,body)=>method==='GET'?Promise.resolve(page()):++attempts===1?Promise.reject(new AccessApiError(503,'unavailable')):Promise.resolve(receipt((body as {operationId:string}).operationId)));
  render(view(api));const user=userEvent.setup();await user.click(screen.getByRole('button',{name:'Calculate and save asset allocation'}));await screen.findByText(/not confirmed/);await user.click(screen.getByRole('button',{name:'Retry asset allocation confirmation'}));await screen.findByText('INR 3,000.00 · 75.00%');
  const writes=request.mock.calls.filter(([,method])=>method==='POST');expect(writes).toHaveLength(2);expect(writes[0]?.[2]).toEqual(writes[1]?.[2]);
});
it('reopens the immutable saved result for the exact net worth source without a duplicate write',async()=>{
  const api=new AccessApi(session);const request=vi.spyOn(api,'product').mockResolvedValue(page([receipt('1'.repeat(32))]));render(view(api));
  expect(await screen.findByText('INR 1,000.00 · 25.00%')).toBeVisible();expect(request.mock.calls.every(([,method])=>method==='GET')).toBe(true);
});
