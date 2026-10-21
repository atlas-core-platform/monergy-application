import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import { Alert, Button, Card, Checkbox, Drawer, Input, Select, Space, Tag } from 'antd';
import type { AccessApi } from '../access/accessApi';
import { AccessApiError } from '../access/accessApi';
import { useWorkspaceSession } from '../access/workspaceSession';
import { getAssetSnapshot, getLiabilitySnapshot } from './productApi';
import { calculateNetWorth, exactMoney, getNetWorthPositions, getNetWorthResult, getNetWorthResults, netWorthIssues, saveNetWorthPosition, validNetWorthDraft, validNetWorthPage, validNetWorthPosition, validNetWorthResult } from './netWorthApi';
import type { NetWorthDraft, NetWorthLine, NetWorthPage, NetWorthPosition, NetWorthResult } from './netWorthApi';
const uuid = () => crypto.randomUUID().replaceAll('-', '');
const blank = (): NetWorthDraft => ({ asOfDate: '', currency: '', lines: [], assetsComplete: false, liabilitiesComplete: false, replacesPositionId: null });
const amount = (value: string) => {
  if (value === '') return null;
  const parts = value.split('.'); const whole = parts[0] ?? ''; const cents = parts[1] ?? '';
  return parts.length <= 2 && /^(?:0|[1-9]\d{0,11})$/u.test(whole) && /^\d{0,2}$/u.test(cents) ? whole + '.' + cents.padEnd(2, '0') : value;
};
const today = () => new Date(Date.now() + 14 * 60 * 60 * 1000).toISOString().slice(0, 10);
const kindNames: Record<string, string> = { CASH: 'Cash', BANK_ACCOUNT: 'Bank account', INVESTMENT: 'Investment', PROPERTY: 'Property', VEHICLE: 'Vehicle', OTHER_ASSET: 'Other asset', LOAN: 'Loan', CREDIT_CARD: 'Credit card', OTHER_LIABILITY: 'Other liability' };
const fields = ['ASSET', 'LIABILITY'] as const;
interface Submission { save: { operationId: string; position: NetWorthDraft }; calculationOperation: string; saved: NetWorthPosition | null }
export default function NetWorthPageExperience() {
  const { api, authorization } = useWorkspaceSession(); const product = authorization?.area === 'product' ? authorization.context : null;
  if (!api || !product) return <Alert type="info" title="Your financial workspace is unavailable." />;
  return <NetWorthExperience api={api} customerId={product.customerId} canRead={product.capabilities.includes('financial-profile.profile.read')} canWrite={product.capabilities.includes('financial-profile.profile.update')} canCalculate={product.capabilities.includes('financial-rules.calculation.execute')} canReadDocuments={product.capabilities.includes('evidence.document.read')} />;
}
export function NetWorthExperience({ api, customerId, canRead, canWrite, canCalculate, canReadDocuments }: { api: AccessApi; customerId: string; canRead: boolean; canWrite: boolean; canCalculate: boolean; canReadDocuments: boolean }) {
  const context = useMemo(() => ({ api, customerId, canRead, canWrite, canCalculate, canReadDocuments }), [api, customerId, canRead, canWrite, canCalculate, canReadDocuments]);
  const scope = useRef(context); useLayoutEffect(() => { scope.current = context; return () => { scope.current = { ...context, canRead: false }; }; }, [context]);
  const [draft, setDraft] = useState<{ context: typeof context; value: NetWorthDraft; open: boolean } | null>(null);
  const [submission, setSubmission] = useState<{ context: typeof context; value: Submission } | null>(null);
  const [pending, setPending] = useState<typeof context | null>(null); const [conflict, setConflict] = useState<typeof context | null>(null);
  const [error, setError] = useState<{ context: typeof context; text: string } | null>(null);
  const [positions, setPositions] = useState<{ context: typeof context; page: NetWorthPage<NetWorthPosition> } | null>(null);
  const [results, setResults] = useState<{ context: typeof context; page: NetWorthPage<NetWorthResult> } | null>(null);
  const [historyError, setHistoryError] = useState<{ context: typeof context; text: string } | null>(null);
  const [selected, setSelected] = useState<{ context: typeof context; result: NetWorthResult } | null>(null);
  const [review, setReview] = useState<{ context: typeof context; position: NetWorthPosition } | null>(null);
  const [loading, setLoading] = useState<typeof context | null>(null); const generation = useRef(0); const reading = useRef<AbortController | null>(null);
  const current = draft?.context === context ? draft : null; const frozen = submission?.context === context ? submission.value : null; const busy = pending === context;
  const load = useCallback(async (positionCursor?: string, resultCursor?: string) => {
    if (!canRead) return; reading.current?.abort(); const controller = new AbortController(); reading.current = controller; const version = ++generation.current;
    const active = () => scope.current === context && !controller.signal.aborted && version === generation.current;
    setLoading(context); setHistoryError(null); setPositions(null); setResults(null);
    try {
      const [p, r] = await Promise.all([getNetWorthPositions(api, positionCursor, controller.signal), canCalculate ? getNetWorthResults(api, resultCursor, controller.signal) : Promise.resolve(null)]);
      if (!validNetWorthPage(p, item => validNetWorthPosition(item, customerId), item => item.positionId) || p.items.some(item => item.positionId === positionCursor) || r && (!validNetWorthPage(r, item => validNetWorthResult(item, customerId), item => item.calculationId) || r.items.some(item => item.calculationId === resultCursor))) throw new Error('Invalid saved history');
      if (active()) { setPositions({ context, page: p }); if (r) setResults({ context, page: r }); }
    } catch { if (active()) setHistoryError({ context, text: 'Saved net worth reviews are unavailable. Check your current access and try again.' }); }
    finally { if (active()) setLoading(null); }
  }, [api, canRead, canCalculate, context, customerId]);
  useEffect(() => { const timer = window.setTimeout(() => { void load(); }, 0); return () => { window.clearTimeout(timer); reading.current?.abort(); }; }, [load]);
  useEffect(() => {
    const id = new URLSearchParams(window.location.search).get('calculation');
    if (!id || !canRead || !canCalculate) return;
    const controller = new AbortController();
    void getNetWorthResult(api, id, controller.signal).then(result => {
      if (!validNetWorthResult(result, customerId) || result.calculationId !== id) throw new Error('Invalid calculation');
      if (scope.current === context && !controller.signal.aborted) setSelected({ context, result });
    }).catch(() => { if (scope.current === context && !controller.signal.aborted) setHistoryError({ context, text: 'The linked calculation is unavailable under your current access.' }); });
    return () => { controller.abort(); };
  }, [api, canRead, canCalculate, context, customerId]);
  const edit = (value: NetWorthDraft) => { if (!canWrite || busy || frozen) return; setDraft({ context, value, open: true }); setError(null); setConflict(null); };
  const change = (value: NetWorthDraft) => { if (current && !frozen && !busy && conflict !== context) setDraft({ ...current, value }); };
  const lineChange = (index: number, value: NetWorthLine) => { if (current) change({ ...current.value, lines: current.value.lines.map((line, i) => i === index ? value : line), assetsComplete: false, liabilitiesComplete: false }); };
  const add = (side: string) => { if (current && current.value.lines.length < 200) change({ ...current.value, assetsComplete: false, liabilitiesComplete: false, lines: [...current.value.lines, { lineId: uuid(), side, kind: side === 'ASSET' ? 'OTHER_ASSET' : 'OTHER_LIABILITY', name: '', amount: null, currency: current.value.currency, valueDate: current.value.asOfDate || null, sourceNote: '', record: null }] }); };
  const importRecords = async () => {
    if (!current || frozen || busy || !canReadDocuments || !current.value.asOfDate || !/^[A-Z]{3}$/u.test(current.value.currency)) return;
    const original = current; setPending(context); setError(null);
    try {
      const [a, l] = await Promise.all([getAssetSnapshot(api, original.value.asOfDate, original.value.currency), getLiabilitySnapshot(api, original.value.asOfDate, original.value.currency)]);
      if (a.asOfDate !== original.value.asOfDate || l.asOfDate !== original.value.asOfDate || a.currency !== original.value.currency || l.currency !== original.value.currency || a.investments.hasMore || a.properties.hasMore || a.vehicles.hasMore || l.hasMore) throw new Error('Incomplete or changed record selection');
      const make = (kind: string, side: string, id: string, revision: number, name: string, value: number, currency: string, date: string): NetWorthLine => ({ lineId: uuid(), side, kind: side === 'LIABILITY' ? 'LOAN' : kind, name, amount: value.toFixed(2), currency, valueDate: date, sourceNote: 'Saved customer-reviewed record', record: { kind, recordId: id, revision } });
      const records = [...a.investments.items, ...a.properties.items, ...a.vehicles.items, ...l.items];
      if (records.some(item => item.customerId !== customerId || !Number.isInteger(item.revision) || item.revision < 1)) throw new Error('Foreign or invalid saved records');
      const imported = [...a.investments.items.map(x => make('INVESTMENT', 'ASSET', x.investmentRecordId, x.revision, x.investment.name, x.investment.currentValue, x.currency, x.investment.valueAsOfDate)), ...a.properties.items.map(x => make('PROPERTY', 'ASSET', x.propertyRecordId, x.revision, x.property.name, x.property.currentValue, x.currency, x.property.valueAsOfDate)), ...a.vehicles.items.map(x => make('VEHICLE', 'ASSET', x.vehicleRecordId, x.revision, x.vehicle.name, x.vehicle.currentValue, x.currency, x.vehicle.valueAsOfDate)), ...l.items.map(x => make('LIABILITY', 'LIABILITY', x.liabilityRecordId, x.revision, x.liability.name, x.liability.outstandingAmount, x.currency, x.liability.balanceAsOfDate))];
      const existing = new Set(original.value.lines.map(x => x.record && x.record.kind + '|' + x.record.recordId)); const combined = [...original.value.lines, ...imported.filter(x => x.record === null || !existing.has(x.record.kind + '|' + x.record.recordId))];
      const next = { ...original.value, lines: combined, assetsComplete: false, liabilitiesComplete: false };
      if (!validNetWorthDraft(next) || imported.some(x => x.currency !== original.value.currency || x.valueDate !== original.value.asOfDate)) throw new Error('Invalid or excessive saved records');
      if (scope.current === context) { setDraft({ ...original, value: next }); setError({ context, text: imported.length === 0 ? 'No saved records match this date and currency. Add and review any other assets or liabilities; this does not mean zero.' : 'Saved records added. Review other assets and liabilities, missing records and possible overlap before confirming the inventory.' }); }
    } catch { if (scope.current === context) setError({ context, text: 'Saved records could not be imported completely. No partial import was applied. Review the date, currency, record limit and document access.' }); }
    finally { setPending(value => value === context ? null : value); }
  };
  const save = async (existing?: NetWorthPosition) => {
    if (!canRead || !canCalculate || busy || !frozen && !existing && (!current || !canWrite || !validNetWorthDraft(current.value) || conflict === context)) return;
    const request: Submission = frozen ?? { save: { operationId: uuid(), position: existing?.position ?? current?.value ?? blank() }, calculationOperation: uuid(), saved: existing ?? null };
    let confirmed = request.saved;
    setSubmission({ context, value: request }); setPending(context); setError(null);
    try {
      const position = request.saved ?? await saveNetWorthPosition(api, request.save);
      if (!validNetWorthPosition(position, customerId) || !request.saved && (position.operationId !== request.save.operationId || JSON.stringify(position.position) !== JSON.stringify(request.save.position))) throw new Error('Invalid review confirmation');
      confirmed = position; if (scope.current !== context) return; setSubmission({ context, value: { ...request, saved: position } });
      const result = await calculateNetWorth(api, { operationId: request.calculationOperation, positionId: position.positionId });
      if (!validNetWorthResult(result, customerId) || result.operationId !== request.calculationOperation || JSON.stringify(result.position) !== JSON.stringify(position)) throw new Error('Invalid calculation confirmation');
      if (scope.current !== context) return; setSelected({ context, result }); setSubmission(null); setDraft(null); await load();
    } catch (failure) {
      if (scope.current !== context) return;
      const rejected = failure instanceof AccessApiError && (failure.status === 400 || failure.status === 409);
      if (rejected) { setSubmission(null); if (failure.status === 409 || confirmed) setConflict(context); }
      setError({ context, text: rejected ? 'The review was rejected or a referenced record changed. Refresh, review the saved versions and start a new review.' : confirmed ? 'Calculation has not been confirmed. Retry confirmation using this saved review and the same calculation request.' : 'Saving has not been confirmed. Retry confirmation using the same review and request.' });
    } finally { setPending(value => value === context ? null : value); }
  };
  const openResult = async (item: NetWorthResult) => {
    setError(null); setLoading(null); const controller = new AbortController(); reading.current?.abort(); reading.current = controller;
    try { const result = await getNetWorthResult(api, item.calculationId, controller.signal); if (!validNetWorthResult(result, customerId) || result.calculationId !== item.calculationId || JSON.stringify(result) !== JSON.stringify(item)) throw new Error('Changed result'); if (scope.current === context && !controller.signal.aborted) setSelected({ context, result }); }
    catch { if (scope.current === context && !controller.signal.aborted) setError({ context, text: 'The saved result could not be confirmed under your current access.' }); }
  };
  if (!canRead) return <Alert type="info" title="Net worth is not included in your current access." />;
  const positionPage = positions?.context === context ? positions.page : null; const resultPage = results?.context === context ? results.page : null;
  return <main className="product-page"><h1>Net Worth</h1><p>Review everything you own and owe at one valuation date and in one currency. Net worth is total assets minus total liabilities.</p>
    <p>Saved record versions keep their source links. Other values are your declarations and are not independently verified. No exchange rates or older values are assumed.</p>
    <Space wrap>{canWrite && canCalculate && <Button type="primary" disabled={busy || Boolean(frozen)} onClick={() => { edit(blank()); }}>Start net worth review</Button>}{frozen && <Button loading={busy} onClick={() => { if (current) setDraft({ ...current, open: true }); else void save(); }}>Continue net worth confirmation</Button>}<Button disabled={busy} loading={loading === context} onClick={() => { void load(); }}>Refresh saved reviews</Button></Space>
    {!canCalculate && <Alert type="info" title="Calculation access is required to calculate and view saved net worth results." />}
    {error?.context === context && !current?.open && <Alert type="error" title={error.text} />}{historyError?.context === context && <Alert type="error" title={historyError.text} />}
    <h2>Saved results</h2>{resultPage?.items.length === 0 && <p>No saved net worth calculations yet. This does not mean zero net worth.</p>}
    {resultPage?.items.map(item => <Card key={item.calculationId} size="small" style={{ marginBlock: 12 }}><h3>{item.position.position.asOfDate} · {item.position.position.currency}</h3><p>{item.state === 'CALCULATED' && item.netWorth !== null ? exactMoney(item.netWorth, item.position.position.currency) : 'More information needed'}</p><p>Calculated {item.recordedAt}</p><Button onClick={() => { void openResult(item); }}>View saved result: {item.position.position.asOfDate}</Button></Card>)}
    {resultPage?.hasMore && resultPage.nextBeforeId && <Button onClick={() => { void load(undefined, resultPage.nextBeforeId ?? undefined); }}>Earlier results</Button>}
    <h2>Saved inventories</h2>{positionPage?.items.length === 0 && <p>No reviewed inventories saved yet.</p>}
    {positionPage?.items.map(item => <Card key={item.positionId} size="small" style={{ marginBlock: 12 }}><p>{item.position.asOfDate} · {item.position.currency} · {item.lines.length} lines · Reviewed {item.recordedAt}</p><Button onClick={() => { setReview({ context, position: item }); }}>View saved inventory: {item.position.asOfDate}</Button>{canCalculate && <Button disabled={busy || Boolean(frozen)} onClick={() => { void save(item); }}>Calculate saved inventory: {item.position.asOfDate}</Button>}{canWrite && canCalculate && <Button disabled={busy || Boolean(frozen)} onClick={() => { edit({ ...structuredClone(item.position), assetsComplete: false, liabilitiesComplete: false, replacesPositionId: item.positionId }); }}>Update this inventory: {item.position.asOfDate}</Button>}</Card>)}
    {positionPage?.hasMore && positionPage.nextBeforeId && <Button onClick={() => { void load(positionPage.nextBeforeId ?? undefined); }}>Earlier inventories</Button>}
    <Drawer title="Review net worth" open={Boolean(current?.open && canWrite && canCalculate)} onClose={() => { if (current) setDraft({ ...current, open: false }); }} size={760} closable={!busy} mask={{ closable: !busy }} destroyOnHidden>
      {error?.context === context && <Alert type="info" title={error.text} />}
      {current && <><fieldset disabled={busy || Boolean(frozen) || conflict === context} style={{ border: 0, padding: 0 }}>
        <label htmlFor="net-worth-date">Valuation date</label><Input id="net-worth-date" type="date" min="1900-01-01" max={today()} value={current.value.asOfDate} onChange={e => { change({ ...current.value, asOfDate: e.target.value, assetsComplete: false, liabilitiesComplete: false }); }} />
        <label htmlFor="net-worth-currency">Review currency</label><Input id="net-worth-currency" maxLength={3} placeholder="For example, INR" value={current.value.currency} onChange={e => { change({ ...current.value, currency: e.target.value.toUpperCase(), assetsComplete: false, liabilitiesComplete: false }); }} />
        {canReadDocuments && <Button style={{ marginBlock: 12 }} disabled={!current.value.asOfDate || !/^[A-Z]{3}$/u.test(current.value.currency)} onClick={() => { void importRecords(); }}>Add saved records for this date and currency</Button>}
        <p>Add cash, bank accounts, investments, properties, vehicles and other assets, then all loans, credit cards and other liabilities. Include only your share of each asset and each debt once. Describe the source of each declared value. Use owned current values and outstanding debt, excluding insurance cover, credit limits and original loan amounts. Up to 200 lines can be reviewed together.</p>
        {fields.map(side => <section key={side} aria-label={side === 'ASSET' ? 'Review assets' : 'Review liabilities'}><h2>{side === 'ASSET' ? 'Assets' : 'Liabilities'}</h2>
          {current.value.lines.filter(line => line.side === side).length === 0 && <p>No {side === 'ASSET' ? 'asset' : 'liability'} lines. Confirm the inventory below only after checking that you have none to include.</p>}
          {current.value.lines.map((line, index) => line.side !== side ? null : <Card key={line.lineId} size="small" style={{ marginBlock: 12 }}><Tag>{line.record ? 'Saved reviewed record · version ' + String(line.record.revision) : 'Customer declaration'}</Tag>
            <label htmlFor={'net-kind-' + line.lineId}>Type</label><Select id={'net-kind-' + line.lineId} aria-label={'Type for line ' + String(index + 1)} disabled={Boolean(line.record) || busy || Boolean(frozen) || conflict === context} value={line.kind} options={Object.entries(kindNames).filter(([key]) => side === 'ASSET' ? !['LOAN', 'CREDIT_CARD', 'OTHER_LIABILITY'].includes(key) : ['LOAN', 'CREDIT_CARD', 'OTHER_LIABILITY'].includes(key)).map(([value, label]) => ({ value, label }))} onChange={value => { lineChange(index, { ...line, kind: value }); }} style={{ width: '100%' }} />
            <label htmlFor={'net-name-' + line.lineId}>Name</label><Input id={'net-name-' + line.lineId} aria-label={'Name for line ' + String(index + 1)} maxLength={120} disabled={Boolean(line.record)} value={line.name} onChange={e => { lineChange(index, { ...line, name: e.target.value }); }} />
            <label htmlFor={'net-amount-' + line.lineId}>{side === 'ASSET' ? 'Current value' : 'Outstanding balance'}</label><Input id={'net-amount-' + line.lineId} aria-label={'Amount for line ' + String(index + 1)} inputMode="decimal" disabled={Boolean(line.record)} placeholder="Leave blank if unknown" value={line.amount ?? ''} onChange={e => { lineChange(index, { ...line, amount: e.target.value === '' ? null : e.target.value }); }} onBlur={() => { if (!line.record) lineChange(index, { ...line, amount: amount(line.amount ?? '') }); }} />
            <label htmlFor={'net-currency-' + line.lineId}>Value currency</label><Input id={'net-currency-' + line.lineId} aria-label={'Currency for line ' + String(index + 1)} maxLength={3} disabled={Boolean(line.record)} value={line.currency} onChange={e => { lineChange(index, { ...line, currency: e.target.value.toUpperCase() }); }} />
            <label htmlFor={'net-value-date-' + line.lineId}>Value date</label><Input id={'net-value-date-' + line.lineId} aria-label={'Value date for line ' + String(index + 1)} type="date" disabled={Boolean(line.record)} value={line.valueDate ?? ''} onChange={e => { lineChange(index, { ...line, valueDate: e.target.value || null }); }} />
            <label htmlFor={'net-note-' + line.lineId}>Source or explanation</label><Input.TextArea id={'net-note-' + line.lineId} aria-label={'Source for line ' + String(index + 1)} maxLength={500} value={line.sourceNote} onChange={e => { lineChange(index, { ...line, sourceNote: e.target.value }); }} />
            <Button danger onClick={() => { change({ ...current.value, lines: current.value.lines.filter(x => x.lineId !== line.lineId), assetsComplete: false, liabilitiesComplete: false }); }}>Remove line {index + 1}</Button>{line.record && <p>To change this linked value, correct its saved financial record or remove it and add a clearly identified declaration.</p>}
          </Card>)}<Button disabled={current.value.lines.length >= 200} onClick={() => { add(side); }}>Add {side === 'ASSET' ? 'asset' : 'liability'}</Button></section>)}
        <p>Missing information is saved for review and produces no net worth total. Confirming an empty side explicitly declares that you have none on this date in the review.</p>
        <Checkbox checked={current.value.assetsComplete} onChange={e => { change({ ...current.value, assetsComplete: e.target.checked }); }}>I have reviewed and included all my assets, or confirm I have none.</Checkbox>
        <Checkbox checked={current.value.liabilitiesComplete} onChange={e => { change({ ...current.value, liabilitiesComplete: e.target.checked }); }}>I have reviewed and included all my liabilities, or confirm I have none.</Checkbox>
      </fieldset><Button type="primary" style={{ marginBlock: 16 }} loading={busy} disabled={conflict === context || !frozen && !validNetWorthDraft(current.value)} onClick={() => { void save(); }}>{frozen ? 'Retry net worth confirmation' : 'Save review and calculate net worth'}</Button></>}
    </Drawer>
    <Drawer title="Saved net worth result" open={selected?.context === context && canCalculate} onClose={() => { setSelected(null); }} size={760} destroyOnHidden>{selected?.context === context && <NetWorthDetails result={selected.result} />}</Drawer>
    <Drawer title="Saved reviewed inventory" open={review?.context === context} onClose={() => { setReview(null); }} size={760} destroyOnHidden>{review?.context === context && <PositionDetails position={review.position} />}</Drawer>
  </main>;
}
function NetWorthDetails({ result }: { result: NetWorthResult }) {
  return <section aria-label="Saved net worth calculation"><h2>{result.position.position.asOfDate} · {result.position.position.currency}</h2><Tag>Customer-reviewed inputs</Tag>
    {result.state === 'CALCULATED' && result.totalAssets !== null && result.totalLiabilities !== null && result.netWorth !== null ? <dl><dt>Total assets</dt><dd>{exactMoney(result.totalAssets, result.position.position.currency)}</dd><dt>Total liabilities</dt><dd>{exactMoney(result.totalLiabilities, result.position.position.currency)}</dd><dt>Net worth</dt><dd>{exactMoney(result.netWorth, result.position.position.currency)}</dd></dl> : <Alert type="info" title="More information needed before net worth can be calculated" description={<ul>{result.issues.map(issue => <li key={issue}>{netWorthIssues[issue]}</li>)}</ul>} />}
    <p>Net worth = total assets − total liabilities. This saved result uses the exact reviewed inventory below. Later changes require a new review and calculation.</p><p>Rule version {result.ruleVersion} · Calculated {result.recordedAt}</p><PositionDetails position={result.position} />
    <details><summary>Calculation details</summary><p>{result.numericSemantics}</p><p>Rule: {result.ruleId} · Definition: {result.definitionHash}</p><p>Calculation: {result.calculationId} · Recorded by: {result.recordedBy}</p></details>
  </section>;
}
function PositionDetails({ position }: { position: NetWorthPosition }) {
  return <section aria-label="Reviewed net worth inventory"><h3>Reviewed inventory · {position.position.asOfDate} · {position.position.currency}</h3><p>Assets confirmed complete: {position.position.assetsComplete ? 'Yes' : 'No'} · Liabilities confirmed complete: {position.position.liabilitiesComplete ? 'Yes' : 'No'}</p>
    {position.lines.map(({ line, source }) => <Card key={line.lineId} size="small" style={{ marginBlock: 12 }}><h4>{line.name}</h4><Tag>{kindNames[line.kind]}</Tag><Tag>{source ? 'Saved reviewed record' : 'Customer declaration'}</Tag><dl><dt>{line.side === 'ASSET' ? 'Current value' : 'Outstanding balance'}</dt><dd>{line.amount === null ? 'Value missing' : exactMoney(line.amount, line.currency)}</dd><dt>Value date</dt><dd>{line.valueDate ?? 'Date missing'}</dd><dt>Source or explanation</dt><dd>{line.sourceNote}</dd></dl>
      {source && <><p>Record version {line.record?.revision} · Source revision {source.sourceRevision} · Reviewed {source.reviewedAt}</p><a href={'/finances?fact=' + encodeURIComponent(source.financialFactId)}>View source history for {line.name}</a><details><summary>Exact source references</summary><p>Financial fact: {source.financialFactId}, revision {source.sourceRevision}</p><p>Document version: {source.documentVersionId} · Evidence: {source.evidenceId}</p><p>Provenance: {source.provenanceId}</p></details></>}
    </Card>)}<p>Values and inventory completeness were reviewed by the customer. Independent ownership, valuation and financial verification are not established by this calculation.</p>
  </section>;
}
