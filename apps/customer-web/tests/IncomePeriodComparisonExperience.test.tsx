import { FoundationProvider } from '@monergy/ui-foundation';
import { act, cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, it, vi } from 'vitest';
import { AccessApi, AccessApiError } from '../src/access/accessApi';
import IncomePeriodComparisonExperience from '../src/product/IncomePeriodComparisonExperience';
import IncomeRecordsExperience from '../src/product/IncomeRecordsExperience';
import type { ProductIncomeRecord } from '../src/product/productApi';

const session={tenantId:'T001',actorId:'A100',authenticationContextId:'session',subjectVersion:2,expiresAt:'2027-01-01T00:00:00Z'};
const earlier:ProductIncomeRecord={incomeRecordId:'income-aug',operationId:'a'.repeat(32),customerId:'customer',currency:'USD',revision:1,
  income:{name:'August pay',incomeSource:'Example employer',incomeType:'Employment',amount:1000,frequency:'MONTHLY',financialYear:'2026-27',periodStart:'2026-08-01',periodEnd:'2026-08-30',sourceFinancialFactId:'ff-aug',sourceRevision:1},
  sourceProvenanceId:'provenance-aug',documentVersionId:'document-aug',evidenceId:'evidence-aug',recordedBy:'A100',recordedAt:'2026-10-01T00:00:00Z',verificationStatus:'CUSTOMER_REVIEWED'};
const later:ProductIncomeRecord={...earlier,incomeRecordId:'income-sep',operationId:'b'.repeat(32),income:{...earlier.income,name:'September pay',amount:1200,periodStart:'2026-09-01',periodEnd:'2026-09-30',sourceFinancialFactId:'ff-sep'}};
const value={earlier,later,periodDays:30};
const input={earlierIncomeRecordId:'income-aug',earlierRevision:1,laterIncomeRecordId:'income-sep',laterRevision:1};
function mount(api:AccessApi,records:ProductIncomeRecord[]=[earlier,later],hasMore=false){
  return render(<FoundationProvider appearance="midnight"><IncomePeriodComparisonExperience api={api} records={records} hasMore={hasMore}/></FoundationProvider>);
}
async function choose(user:ReturnType<typeof userEvent.setup>,name:string,label:RegExp){
  await user.click(screen.getByRole('combobox',{name}));
  const option=screen.getAllByText(label,{selector:'.ant-select-item-option-content'}).find(el=>!el.closest('.ant-select-dropdown-hidden'));
  if(!option)throw new Error('Missing income option');await user.click(option);
}
async function selectPair(user:ReturnType<typeof userEvent.setup>){
  await choose(user,'Earlier saved income',/August pay/);await choose(user,'Later saved income',/September pay/);
}
afterEach(()=>{cleanup();vi.restoreAllMocks();});
it('requires two explicit choices and shows saved period values with their exact source links',async()=>{
  const api=new AccessApi(session);const request=vi.spyOn(api,'product').mockResolvedValue(value);mount(api);const user=userEvent.setup();
  expect(request).not.toHaveBeenCalled();expect(screen.getByRole('button',{name:'Compare selected income periods'})).toBeDisabled();
  await selectPair(user);expect(request).not.toHaveBeenCalled();await user.click(screen.getByRole('button',{name:'Compare selected income periods'}));
  const result=await screen.findByRole('region',{name:'Income period comparison'});expect(result).toHaveTextContent('$1,000.00');expect(result).toHaveTextContent('$1,200.00');expect(result).toHaveTextContent('30 days in each reporting period');
  expect(result).toHaveTextContent('Monthly');expect(result).toHaveTextContent('2026-27');expect(result).toHaveTextContent('Customer reviewed');
  expect(within(result).getByRole('link',{name:'View earlier period source history'})).toHaveAttribute('href','/finances?fact=ff-aug');
  expect(within(result).getByRole('link',{name:'View later period source history'})).toHaveAttribute('href','/finances?fact=ff-sep');
  expect(request.mock.calls[0]?.slice(0,3)).toEqual(['v1/income-records/comparison','POST',input]);expect(result).not.toHaveTextContent('$2,200.00');
});
it('does not compare the same record and explains missing source-backed choices',async()=>{
  const api=new AccessApi(session);const request=vi.spyOn(api,'product');mount(api,[earlier]);const user=userEvent.setup();
  expect(screen.getByText(/Add two income records from different reviewed sources/)).toBeVisible();
  await choose(user,'Earlier saved income',/August pay/);await choose(user,'Later saved income',/August pay/);
  expect(screen.getByRole('button',{name:'Compare selected income periods'})).toBeDisabled();expect(request).not.toHaveBeenCalled();
});
it.each([400,403,404,503])('keeps unavailable or ineligible comparisons out of the result (%s)',async status=>{
  const api=new AccessApi(session);vi.spyOn(api,'product').mockRejectedValue(new AccessApiError(status,'unavailable'));mount(api);const user=userEvent.setup();await selectPair(user);
  await user.click(screen.getByRole('button',{name:'Compare selected income periods'}));
  await screen.findByText(status===400?/Choose records from different sources/:status===404?/A selected saved version is unavailable/:/Income comparison is unavailable/);
  expect(screen.queryByRole('region',{name:'Income period comparison'})).not.toBeInTheDocument();expect(screen.getByRole('button',{name:'Compare selected income periods'})).toBeEnabled();
});
it('clears a displayed comparison when either selected version changes',async()=>{
  const api=new AccessApi(session);vi.spyOn(api,'product').mockResolvedValue(value);const view=mount(api);const user=userEvent.setup();await selectPair(user);await user.click(screen.getByRole('button',{name:'Compare selected income periods'}));
  await screen.findByRole('region',{name:'Income period comparison'});
  view.rerender(<FoundationProvider appearance="midnight"><IncomePeriodComparisonExperience api={api} records={[earlier,{...later,revision:2}]} hasMore={false}/></FoundationProvider>);
  expect(screen.queryByRole('region',{name:'Income period comparison'})).not.toBeInTheDocument();expect(screen.getByText(/A selected record version is no longer in this list/)).toBeVisible();expect(screen.getByRole('button',{name:'Compare selected income periods'})).toBeDisabled();
});
it('aborts and hides a late comparison after the API/session changes',async()=>{
  const api=new AccessApi(session);let finish:(result:unknown)=>void=()=>undefined;
  const request=vi.spyOn(api,'product').mockImplementation(()=>new Promise(resolve=>{finish=resolve;}));const view=mount(api);const user=userEvent.setup();await selectPair(user);await user.click(screen.getByRole('button',{name:'Compare selected income periods'}));
  await waitFor(()=>{expect(request).toHaveBeenCalled();});const signal=request.mock.calls[0]?.[3];const next=new AccessApi({...session,actorId:'A200'});
  view.rerender(<FoundationProvider appearance="midnight"><IncomePeriodComparisonExperience api={next} records={[]} hasMore={false}/></FoundationProvider>);
  expect(signal?.aborted).toBe(true);await act(async()=>{finish(value);await Promise.resolve();});expect(screen.queryByRole('region',{name:'Income period comparison'})).not.toBeInTheDocument();
});
it('discards a pending response after an explicit selection change',async()=>{
  const api=new AccessApi(session);let finish:(result:unknown)=>void=()=>undefined;vi.spyOn(api,'product').mockImplementation(()=>new Promise(resolve=>{finish=resolve;}));mount(api);const user=userEvent.setup();await selectPair(user);await user.click(screen.getByRole('button',{name:'Compare selected income periods'}));
  await choose(user,'Earlier saved income',/September pay/);await act(async()=>{finish(value);await Promise.resolve();});expect(screen.queryByRole('region',{name:'Income period comparison'})).not.toBeInTheDocument();expect(screen.getByRole('button',{name:'Compare selected income periods'})).toBeDisabled();
});
it.each(['status','version','days'])('rejects an unsupported comparison response (%s)',async variation=>{
  const api=new AccessApi(session);vi.spyOn(api,'product').mockResolvedValue(variation==='status'?{...value,later:{...later,verificationStatus:'VERIFIED'}}:variation==='version'?{...value,earlier:{...earlier,revision:99}}:{...value,periodDays:1.5});
  mount(api);const user=userEvent.setup();await selectPair(user);await user.click(screen.getByRole('button',{name:'Compare selected income periods'}));await screen.findByText(/Income comparison is unavailable/);expect(screen.queryByRole('region',{name:'Income period comparison'})).not.toBeInTheDocument();
});
it('keeps the record-list bound explicit and enables comparison without update access',async()=>{
  const api=new AccessApi(session);const request=vi.spyOn(api,'product').mockImplementation((path)=>path==='v1/income-records'?Promise.resolve({items:[earlier,later],hasMore:true,dataDurability:'Temporary data'}):Promise.resolve(value));
  render(<FoundationProvider appearance="midnight"><IncomeRecordsExperience api={api} facts={[]} canRead canCreate={false}/></FoundationProvider>);const user=userEvent.setup();await screen.findByRole('region',{name:'Compare income periods'});
  expect(screen.queryByRole('button',{name:'Add income record'})).not.toBeInTheDocument();expect(screen.getByText(/The choices include the 200 most recently added records/)).toBeVisible();
  await selectPair(user);await user.click(screen.getByRole('button',{name:'Compare selected income periods'}));await screen.findByRole('region',{name:'Income period comparison'});expect(request.mock.calls.filter(([,method])=>method==='POST').every(([path])=>path==='v1/income-records/comparison')).toBe(true);
});
