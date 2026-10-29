import { Alert, Button, Card, Drawer, Input, Select, Space, Tag } from 'antd';
import { useCallback, useEffect, useState } from 'react';
import type { AccessApi } from '../access/accessApi';
import { AccessApiError } from '../access/accessApi';
import { RequestAccessAction } from '../access/RequestAccess';
import {
  createBankAccountRecord, getBankAccountRecordHistory, getBankAccountRecords, reviseBankAccountRecord,
  type ProductBankAccountDraft, type ProductBankAccountRecord, type ProductBankAccountRecordHistory,
  type ProductFinancialFact,
} from './productApi';

const money=(value:number,currency:string)=>new Intl.NumberFormat(undefined,{style:'currency',currency}).format(value);
const operation=()=>crypto.randomUUID().replaceAll('-','');
interface Editor {record:ProductBankAccountRecord|null;operationId:string;bankName:string;accountType:string;last4:string;source?:string}

export default function BankAccountRecordsExperience({api,facts,canRead,canCreate}:{
  api:AccessApi;facts:ProductFinancialFact[];canRead:boolean;canCreate:boolean;
}) {
  const [items,setItems]=useState<ProductBankAccountRecord[]>([]);const [hasMore,setHasMore]=useState(false);
  const [loading,setLoading]=useState(false);const [error,setError]=useState('');const [saving,setSaving]=useState(false);
  const [editor,setEditor]=useState<Editor|null>(null);const [selected,setSelected]=useState<ProductBankAccountRecord|null>(null);
  const [historyRecord,setHistoryRecord]=useState<ProductBankAccountRecord|null>(null);
  const sources=facts.filter(fact=>fact.factType==='ASSET'&&fact.assetType==='BANK_ACCOUNT');
  const load=useCallback(async(signal?:AbortSignal,after?:string)=>{
    if(!canRead)return;setLoading(true);setError('');
    try{const page=await getBankAccountRecords(api,signal,after);if(!Array.isArray(page.items)||page.items.length>200||page.hasMore&&page.items.length!==200)throw new Error('Invalid list');
      setItems(page.items);setHasMore(page.hasMore);}
    catch(failure){if(!signal?.aborted)setError(failure instanceof AccessApiError&&failure.status===403
      ?'Your current access does not include bank account records and their source documents.'
      :'Bank account records are unavailable. Try again.');}
    finally{if(!signal?.aborted)setLoading(false);}
  },[api,canRead]);
  useEffect(()=>{const controller=new AbortController();const timer=window.setTimeout(()=>{void load(controller.signal);},0);
    return()=>{window.clearTimeout(timer);controller.abort();};},[load]);
  if(!canRead)return <Alert type="info" title="Document access is required to view bank account records and their sources."/>;
  const source=sources.find(f=>f.financialFactId+'@'+String(f.revision)===editor?.source);
  const save=async()=>{
    if(!editor||!canCreate||(!editor.record&&!source)||saving||!editor.bankName.trim()||!editor.accountType.trim()||!/^[0-9]{4}$/.test(editor.last4))return;
    const account:ProductBankAccountDraft={bankName:editor.bankName,accountType:editor.accountType,accountNumberLast4:editor.last4,
      sourceFinancialFactId:editor.record?.account.sourceFinancialFactId??source?.financialFactId??'',sourceRevision:editor.record?.account.sourceRevision??source?.revision??0};setSaving(true);setError('');
    try{const saved=editor.record?await reviseBankAccountRecord(api,editor.record.bankAccountRecordId,{operationId:editor.operationId,expectedRevision:editor.record.revision,account}):
        await createBankAccountRecord(api,{operationId:editor.operationId,account});
      if(saved.account.sourceFinancialFactId!==account.sourceFinancialFactId||saved.account.sourceRevision!==account.sourceRevision)throw new Error('Invalid receipt');
      setEditor(null);setSelected(saved);await load();}
    catch(failure){setError(failure instanceof AccessApiError&&failure.code==='BANK_ACCOUNT_REVISION_CONFLICT'
      ?'A newer version has been saved. Refresh and review it before editing again.'
      :'The bank account record was not confirmed. Review the fields and try again.');}
    finally{setSaving(false);}
  };
  const edit=(record:ProductBankAccountRecord)=>{setEditor({record,operationId:operation(),bankName:record.account.bankName,accountType:record.account.accountType,
    last4:record.account.accountNumberLast4,source:record.account.sourceFinancialFactId+'@'+String(record.account.sourceRevision)});};
  return <section aria-label="Bank account records" style={{marginBlock:24}}>
    <Space wrap style={{width:'100%',justifyContent:'space-between'}}><h2>Bank accounts</h2>
      <Button disabled={loading} onClick={()=>{void load();}}>Refresh bank accounts</Button>
      {canCreate&&<Button disabled={saving||sources.length===0} onClick={()=>{setEditor({record:null,operationId:operation(),bankName:'',accountType:'',last4:''});}}>Add bank account</Button>}
    </Space>
    <p>These are customer-reviewed records. Monergy does not independently verify the account or connect to the bank. The balance comes from the exact reviewed source and its as-of date.</p>
    {!canCreate&&<RequestAccessAction target="customer.financial-information" requiredCapabilities={['financial-profile.profile.update']}
      title="Adding or correcting a bank account needs access" description="Request the bounded financial information capability. You can keep reviewing existing bank account records."/>}
    {error&&<Alert type="error" title={error}/>} {loading&&<p role="status">Loading bank accounts…</p>}
    {!loading&&items.length===0&&<p>No reviewed bank account records yet. Review a bank-account source figure in Documents, then add the account metadata here.</p>}
    {items.map(record=><Card key={record.bankAccountRecordId} size="small" style={{marginBlock:12}}>
      <Space wrap><strong>{record.account.bankName} · •••• {record.account.accountNumberLast4}</strong><Tag>Customer reviewed</Tag><span>{record.account.accountType}</span></Space>
      <p>Balance: {money(record.balance,record.currency)} · as of {record.balanceAsOfDate}</p>
      <Space wrap><Button onClick={()=>{setSelected(record);}}>View account details</Button>
        {canCreate?<Button onClick={()=>{edit(record);}}>Correct account metadata</Button>:<RequestAccessAction target="customer.financial-information" requiredCapabilities={['financial-profile.profile.update']} title="Correction access required" description="Request access to correct this reviewed record."/>}
        <Button onClick={()=>{setHistoryRecord(record);}}>View change history</Button></Space>
    </Card>)}
    {hasMore&&<Button onClick={()=>{void load(undefined,items.at(-1)?.bankAccountRecordId);}}>Earlier bank accounts</Button>}
    <Drawer title={editor?.record?'Correct bank account metadata':'Add bank account'} open={Boolean(editor)} onClose={()=>{if(!saving)setEditor(null);}} size={560} destroyOnHidden>
      <p>Only the bank name, account type and last four digits are stored here. Never enter a full account number, IFSC, routing code or credential.</p>
      {!editor?.record&&<><label htmlFor="bank-source">Reviewed bank account source</label><Select id="bank-source" aria-label="Reviewed bank account source" style={{width:'100%',marginBottom:16}} value={editor?.source}
        options={sources.map(f=>({value:f.financialFactId+'@'+String(f.revision),label:f.label+' · '+money(f.value,f.currency)+' · '+f.effectiveDate}))}
        onChange={value=>{if(editor)setEditor({...editor,source:value});}}/></>}
      {(source??editor?.record)&&<Alert type="info" title={'Source balance: '+money(source?.value??editor?.record?.balance??0,source?.currency??editor?.record?.currency??'USD')+' as of '+(source?.effectiveDate??editor?.record?.balanceAsOfDate??'')}
        description="Balance, currency and date are copied from this exact reviewed fact revision and cannot be edited in this record."/>}
      {([['bankName','Bank name'],['accountType','Account type'],['last4','Last four digits']] as const).map(([key,label])=><div key={key} style={{marginBlock:14}}><label htmlFor={'bank-'+key}>{label}</label>
        <Input id={'bank-'+key} maxLength={key==='last4'?4:120} inputMode={key==='last4'?'numeric':undefined} value={editor?.[key]??''}
          onChange={event=>{if(editor)setEditor({...editor,[key]:key==='last4'?event.target.value.replace(/[^0-9]/g,''):event.target.value});}}/></div>)}
      <Button type="primary" loading={saving} disabled={(!source&&!editor?.record)||!editor?.bankName.trim()||!editor.accountType.trim()||!/^[0-9]{4}$/.test(editor.last4)} onClick={()=>{void save();}}>
        {editor?.record?'Save correction':'Save reviewed bank account'}</Button>
    </Drawer>
    <Drawer title="Bank account details" open={Boolean(selected)} onClose={()=>{setSelected(null);}} size={560} destroyOnHidden>{selected&&<>
      <Tag>Customer reviewed</Tag><dl><dt>Bank</dt><dd>{selected.account.bankName}</dd><dt>Account type</dt><dd>{selected.account.accountType}</dd>
        <dt>Account number</dt><dd>•••• {selected.account.accountNumberLast4}</dd><dt>Balance</dt><dd>{money(selected.balance,selected.currency)} as of {selected.balanceAsOfDate}</dd>
        <dt>Last updated</dt><dd>{selected.recordedAt}</dd><dt>Record version</dt><dd>{selected.revision}</dd></dl>
      <a href={'/finances?fact='+encodeURIComponent(selected.account.sourceFinancialFactId)}>View exact source history</a>
      <p>This record is customer reviewed. It is not a live bank connection and does not store a full account number.</p></>}</Drawer>
    <Drawer title="Bank account change history" open={Boolean(historyRecord)} onClose={()=>{setHistoryRecord(null);}} size={560} destroyOnHidden>
      {historyRecord&&<BankHistory api={api} record={historyRecord}/>}</Drawer>
  </section>;
}

function BankHistory({api,record}:{api:AccessApi;record:ProductBankAccountRecord}) {
  const [page,setPage]=useState<ProductBankAccountRecordHistory|null>(null);const [failed,setFailed]=useState(false);
  const load=useCallback(async(before?:number)=>{setFailed(false);try{setPage(await getBankAccountRecordHistory(api,record.bankAccountRecordId,before));}catch{setFailed(true);}},[api,record.bankAccountRecordId]);
  useEffect(()=>{const timer=window.setTimeout(()=>{void load();},0);return()=>{window.clearTimeout(timer);};},[load]);
  return <section aria-label="Reviewed bank account change history"><p>Every correction retains the exact original source lineage and immutable source balance.</p>
    {failed&&<Alert type="error" title="Bank account history is unavailable."/>}
    {page?.items.map(item=><Card key={item.revision} size="small"><strong>Version {item.revision}</strong>
      <p>{item.account.bankName} · {item.account.accountType} · •••• {item.account.accountNumberLast4}</p><p>Updated {item.recordedAt}</p></Card>)}
    {page?.hasMore&&page.nextBeforeRevision!==null&&<Button onClick={()=>{void load(page.nextBeforeRevision??undefined);}}>Earlier versions</Button>}</section>;
}
