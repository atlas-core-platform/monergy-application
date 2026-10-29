import { Alert, Button, Card, Drawer, Input, Select, Space, Tag } from 'antd';
import { useCallback, useEffect, useState } from 'react';
import type { AccessApi } from '../access/accessApi';
import { AccessApiError } from '../access/accessApi';
import { RequestAccessAction } from '../access/RequestAccess';
import {
  createCreditCardRecord, getCreditCardRecordHistory, getCreditCardRecords, reviseCreditCardRecord,
  type ProductCreditCardDraft, type ProductCreditCardRecord, type ProductCreditCardRecordHistory,
  type ProductFinancialFact,
} from './productApi';

const money=(value:number,currency:string)=>new Intl.NumberFormat(undefined,{style:'currency',currency}).format(value);
const operation=()=>crypto.randomUUID().replaceAll('-','');
interface Editor {record:ProductCreditCardRecord|null;operationId:string;issuerName:string;cardType:string;last4:string;source?:string}

export default function CreditCardRecordsExperience({api,facts,canRead,canCreate}:{
  api:AccessApi;facts:ProductFinancialFact[];canRead:boolean;canCreate:boolean;
}) {
  const [items,setItems]=useState<ProductCreditCardRecord[]>([]);const [hasMore,setHasMore]=useState(false);
  const [loading,setLoading]=useState(false);const [error,setError]=useState('');const [saving,setSaving]=useState(false);
  const [editor,setEditor]=useState<Editor|null>(null);const [selected,setSelected]=useState<ProductCreditCardRecord|null>(null);
  const [historyRecord,setHistoryRecord]=useState<ProductCreditCardRecord|null>(null);
  const sources=facts.filter(fact=>fact.factType==='LOAN');
  const load=useCallback(async(signal?:AbortSignal,after?:string)=>{
    if(!canRead)return;setLoading(true);setError('');
    try{const page=await getCreditCardRecords(api,signal,after);if(!Array.isArray(page.items)||page.items.length>200||page.hasMore&&page.items.length!==200)throw new Error('Invalid list');
      setItems(page.items);setHasMore(page.hasMore);}
    catch(failure){if(!signal?.aborted)setError(failure instanceof AccessApiError&&failure.status===403
      ?'Your current access does not include credit card records and their source documents.'
      :'Credit card records are unavailable. Try again.');}
    finally{if(!signal?.aborted)setLoading(false);}
  },[api,canRead]);
  useEffect(()=>{const controller=new AbortController();const timer=window.setTimeout(()=>{void load(controller.signal);},0);
    return()=>{window.clearTimeout(timer);controller.abort();};},[load]);
  if(!canRead)return <Alert type="info" title="Document access is required to view credit card records and their sources."/>;
  const source=sources.find(f=>f.financialFactId+'@'+String(f.revision)===editor?.source);
  const save=async()=>{
    if(!editor||!canCreate||(!editor.record&&!source)||saving||!editor.issuerName.trim()||!editor.cardType.trim()||!/^[0-9]{4}$/.test(editor.last4))return;
    const card:ProductCreditCardDraft={issuerName:editor.issuerName,cardType:editor.cardType,cardNumberLast4:editor.last4,
      sourceFinancialFactId:editor.record?.card.sourceFinancialFactId??source?.financialFactId??'',sourceRevision:editor.record?.card.sourceRevision??source?.revision??0};setSaving(true);setError('');
    try{const saved=editor.record?await reviseCreditCardRecord(api,editor.record.creditCardRecordId,{operationId:editor.operationId,expectedRevision:editor.record.revision,card}):
        await createCreditCardRecord(api,{operationId:editor.operationId,card});
      if(saved.card.sourceFinancialFactId!==card.sourceFinancialFactId||saved.card.sourceRevision!==card.sourceRevision)throw new Error('Invalid receipt');
      setEditor(null);setSelected(saved);await load();}
    catch(failure){setError(failure instanceof AccessApiError&&failure.code==='CREDIT_CARD_REVISION_CONFLICT'
      ?'A newer version has been saved. Refresh and review it before editing again.'
      :'The credit card record was not confirmed. Review the fields and try again.');}
    finally{setSaving(false);}
  };
  const edit=(record:ProductCreditCardRecord)=>{setEditor({record,operationId:operation(),issuerName:record.card.issuerName,cardType:record.card.cardType,
    last4:record.card.cardNumberLast4,source:record.card.sourceFinancialFactId+'@'+String(record.card.sourceRevision)});};
  return <section aria-label="Credit card records" style={{marginBlock:24}}>
    <Space wrap style={{width:'100%',justifyContent:'space-between'}}><h2>Credit cards</h2>
      <Button disabled={loading} onClick={()=>{void load();}}>Refresh credit cards</Button>
      {canCreate&&<Button disabled={saving||sources.length===0} onClick={()=>{setEditor({record:null,operationId:operation(),issuerName:'',cardType:'',last4:''});}}>Add credit card</Button>}
    </Space>
    <p>These are customer-reviewed records. Monergy does not independently verify the card or connect to the issuer. The outstanding balance comes from the exact reviewed source and its as-of date.</p>
    {!canCreate&&<RequestAccessAction target="customer.financial-information" requiredCapabilities={['financial-profile.profile.update']}
      title="Adding or correcting a credit card needs access" description="Request the bounded financial information capability. You can keep reviewing existing credit card records."/>}
    {error&&<Alert type="error" title={error}/>} {loading&&<p role="status">Loading credit cards…</p>}
    {!loading&&items.length===0&&<p>No reviewed credit card records yet. Review a credit-card source figure in Documents, then add the card metadata here.</p>}
    {items.map(record=><Card key={record.creditCardRecordId} size="small" style={{marginBlock:12}}>
      <Space wrap><strong>{record.card.issuerName} · •••• {record.card.cardNumberLast4}</strong><Tag>Customer reviewed</Tag><span>{record.card.cardType}</span></Space>
      <p>Outstanding balance: {money(record.balance,record.currency)} · as of {record.balanceAsOfDate}</p>
      <Space wrap><Button onClick={()=>{setSelected(record);}}>View card details</Button>
        {canCreate?<Button onClick={()=>{edit(record);}}>Correct card metadata</Button>:<RequestAccessAction target="customer.financial-information" requiredCapabilities={['financial-profile.profile.update']} title="Correction access required" description="Request access to correct this reviewed record."/>}
        <Button onClick={()=>{setHistoryRecord(record);}}>View change history</Button></Space>
    </Card>)}
    {hasMore&&<Button onClick={()=>{void load(undefined,items.at(-1)?.creditCardRecordId);}}>Earlier credit cards</Button>}
    <Drawer title={editor?.record?'Correct credit card metadata':'Add credit card'} open={Boolean(editor)} onClose={()=>{if(!saving)setEditor(null);}} size={560} destroyOnHidden>
      <p>Only the issuer name, card type and last four digits are stored here. Never enter a full card number, CVV, expiry date, PIN, OTP or credential.</p>
      {!editor?.record&&<><label htmlFor="card-source">Reviewed credit card source</label><Select id="card-source" aria-label="Reviewed credit card source" style={{width:'100%',marginBottom:16}} value={editor?.source}
        options={sources.map(f=>({value:f.financialFactId+'@'+String(f.revision),label:f.label+' · '+money(f.value,f.currency)+' · '+f.effectiveDate}))}
        onChange={value=>{if(editor)setEditor({...editor,source:value});}}/></>}
      {(source??editor?.record)&&<Alert type="info" title={'Source balance: '+money(source?.value??editor?.record?.balance??0,source?.currency??editor?.record?.currency??'USD')+' as of '+(source?.effectiveDate??editor?.record?.balanceAsOfDate??'')}
        description="Balance, currency and date are copied from this exact reviewed fact revision and cannot be edited in this record."/>}
      {([['issuerName','Issuer name'],['cardType','Card type'],['last4','Last four digits']] as const).map(([key,label])=><div key={key} style={{marginBlock:14}}><label htmlFor={'card-'+key}>{label}</label>
        <Input id={'card-'+key} maxLength={key==='last4'?4:120} inputMode={key==='last4'?'numeric':undefined} value={editor?.[key]??''}
          onChange={event=>{if(editor)setEditor({...editor,[key]:key==='last4'?event.target.value.replace(/[^0-9]/g,''):event.target.value});}}/></div>)}
      <Button type="primary" loading={saving} disabled={(!source&&!editor?.record)||!editor?.issuerName.trim()||!editor.cardType.trim()||!/^[0-9]{4}$/.test(editor.last4)} onClick={()=>{void save();}}>
        {editor?.record?'Save correction':'Save reviewed credit card'}</Button>
    </Drawer>
    <Drawer title="Credit card details" open={Boolean(selected)} onClose={()=>{setSelected(null);}} size={560} destroyOnHidden>{selected&&<>
      <Tag>Customer reviewed</Tag><dl><dt>Issuer</dt><dd>{selected.card.issuerName}</dd><dt>Card type</dt><dd>{selected.card.cardType}</dd>
        <dt>Card number</dt><dd>•••• {selected.card.cardNumberLast4}</dd><dt>Outstanding balance</dt><dd>{money(selected.balance,selected.currency)} as of {selected.balanceAsOfDate}</dd>
        <dt>Last updated</dt><dd>{selected.recordedAt}</dd><dt>Record version</dt><dd>{selected.revision}</dd></dl>
      <a href={'/finances?fact='+encodeURIComponent(selected.card.sourceFinancialFactId)}>View exact source history</a>
      <p>This record is customer reviewed. It is not a live issuer connection and does not store a full card number, CVV, expiry, PIN or OTP.</p></>}</Drawer>
    <Drawer title="Credit card change history" open={Boolean(historyRecord)} onClose={()=>{setHistoryRecord(null);}} size={560} destroyOnHidden>
      {historyRecord&&<CreditCardHistory api={api} record={historyRecord}/>}</Drawer>
  </section>;
}

function CreditCardHistory({api,record}:{api:AccessApi;record:ProductCreditCardRecord}) {
  const [page,setPage]=useState<ProductCreditCardRecordHistory|null>(null);const [failed,setFailed]=useState(false);
  const load=useCallback(async(before?:number)=>{setFailed(false);try{setPage(await getCreditCardRecordHistory(api,record.creditCardRecordId,before));}catch{setFailed(true);}},[api,record.creditCardRecordId]);
  useEffect(()=>{const timer=window.setTimeout(()=>{void load();},0);return()=>{window.clearTimeout(timer);};},[load]);
  return <section aria-label="Reviewed credit card change history"><p>Every correction retains the exact original source lineage and immutable source balance.</p>
    {failed&&<Alert type="error" title="Credit card history is unavailable."/>}
    {page?.items.map(item=><Card key={item.revision} size="small"><strong>Version {item.revision}</strong>
      <p>{item.card.issuerName} · {item.card.cardType} · •••• {item.card.cardNumberLast4}</p><p>Updated {item.recordedAt}</p></Card>)}
    {page?.hasMore&&page.nextBeforeRevision!==null&&<Button onClick={()=>{void load(page.nextBeforeRevision??undefined);}}>Earlier versions</Button>}</section>;
}
