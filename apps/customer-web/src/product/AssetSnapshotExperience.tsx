import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import { Alert, Button, Card, Input, Tag } from 'antd';
import type { AccessApi } from '../access/accessApi';
import { AccessApiError } from '../access/accessApi';
import { getAssetSnapshot } from './productApi';
import type { ProductAssetSnapshot } from './productApi';

function rows(value:ProductAssetSnapshot) {
  return [
    ...value.investments.items.map(item => ({kind:'Investment',id:item.investmentRecordId,name:item.investment.name,details:item.investment.institution+' · '+item.investment.investmentType,purchase:item.investment.purchaseValue,current:item.investment.currentValue,date:item.investment.valueAsOfDate,source:item.investment.sourceFinancialFactId,sourceRevision:item.investment.sourceRevision,currency:item.currency,revision:item.revision,recordedAt:item.recordedAt})),
    ...value.properties.items.map(item => ({kind:'Property',id:item.propertyRecordId,name:item.property.name,details:item.property.location+' · '+item.property.propertyType,purchase:item.property.purchaseValue,current:item.property.currentValue,date:item.property.valueAsOfDate,source:item.property.sourceFinancialFactId,sourceRevision:item.property.sourceRevision,currency:item.currency,revision:item.revision,recordedAt:item.recordedAt})),
    ...value.vehicles.items.map(item => ({kind:'Vehicle',id:item.vehicleRecordId,name:item.vehicle.name,details:(item.vehicle.registrationNumber ?? 'Registration not supplied')+' · '+item.vehicle.vehicleType,purchase:item.vehicle.purchaseValue,current:item.vehicle.currentValue,date:item.vehicle.valueAsOfDate,source:item.vehicle.sourceFinancialFactId,sourceRevision:item.vehicle.sourceRevision,currency:item.currency,revision:item.revision,recordedAt:item.recordedAt})),
  ];
}
interface View { api: AccessApi; refreshKey: unknown; date: string; currency: string }
export default function AssetSnapshotExperience({ api, refreshKey }: { api: AccessApi; refreshKey: unknown }) {
  const [date, setDate] = useState('');
  const [currency, setCurrency] = useState('');
  const [pending, setPending] = useState<View | null>(null);
  const [result, setResult] = useState<(View & { value: ProductAssetSnapshot }) | null>(null);
  const [failure, setFailure] = useState<(View & { text: string }) | null>(null);
  const scope = useRef({ api, refreshKey });
  const sequence = useRef(0);
  const controller = useRef<AbortController | null>(null);
  useLayoutEffect(() => { scope.current = { api, refreshKey }; }, [api, refreshKey]);
  useEffect(() => () => { controller.current?.abort(); }, [api, refreshKey]);
  const matches = (view: View | null) => view?.api === api && view.refreshKey === refreshKey && view.date === date && view.currency === currency;
  const choose = (field: 'date' | 'currency', value: string) => {
    sequence.current++; controller.current?.abort(); setPending(null); setResult(null); setFailure(null);
    if (field === 'date') setDate(value); else setCurrency(value.toUpperCase());
  };
  const load = async () => {
    if (!date || !/^[A-Z]{3}$/.test(currency) || matches(pending)) return;
    const view = { api, refreshKey, date, currency }; const generation = ++sequence.current;
    controller.current?.abort(); const request = new AbortController(); controller.current = request;
    const active = () => !request.signal.aborted && sequence.current === generation && scope.current.api === api && scope.current.refreshKey === refreshKey;
    setPending(view); setResult(null); setFailure(null);
    try {
      const value = await getAssetSnapshot(api, date, currency, request.signal);
      const groups = [value.investments, value.properties, value.vehicles];
      if (value.asOfDate !== date || value.currency !== currency || groups.some(group => !Array.isArray(group.items) || group.items.length > 200 || typeof group.hasMore !== 'boolean' || group.hasMore && group.items.length !== 200) ||
          groups.some(group => new Set(group.items.map(item => 'investmentRecordId' in item ? item.investmentRecordId : 'propertyRecordId' in item ? item.propertyRecordId : item.vehicleRecordId)).size !== group.items.length) ||
          rows(value).some(item => !item.id || !Number.isInteger(item.revision) || item.revision < 1 || item.currency !== currency || item.date !== date || !item.source || !Number.isInteger(item.sourceRevision) || item.sourceRevision < 1 || !Number.isFinite(item.purchase) || !Number.isFinite(item.current) || item.purchase < 0 || item.current < 0))
        throw new Error('Invalid dated assets');
      if (active()) setResult({ ...view, value });
    } catch (error) {
      if (active()) setFailure({ ...view, text: error instanceof AccessApiError && error.status === 400
        ? 'Choose a value date from 1900 through today and a three-letter currency code.'
        : 'Dated assets are unavailable. Check your current document access and try again.' });
    } finally { if (active()) setPending(null); }
  };
  const visible = matches(result) ? result?.value : null;
  const repeatedSources = visible && new Set(rows(visible).map(item => item.source)).size < rows(visible).length;
  return <Card size="small" style={{ marginBlock: 16 }}><section aria-label="Dated asset snapshot">
    <h3>Dated asset snapshot</h3>
    <p>Choose an exact value date and currency to review the latest saved versions dated that day. Older values are not carried forward and currencies are not converted. Corrections can move records into or out of this view.</p>
    <label htmlFor="asset-snapshot-date">Snapshot value date</label>
    <Input id="asset-snapshot-date" type="date" value={date} onChange={event => { choose('date', event.target.value); }} />
    <label htmlFor="asset-snapshot-currency">Snapshot currency</label>
    <Input id="asset-snapshot-currency" placeholder="For example, USD or INR" maxLength={3} value={currency} onChange={event => { choose('currency', event.target.value); }} />
    <Button style={{ marginBlock: 12 }} disabled={!date || !/^[A-Z]{3}$/.test(currency)} loading={matches(pending)} onClick={() => { void load(); }}>View dated assets</Button>
    {matches(failure) && <Alert type="error" title={failure?.text} />}
    {visible && <section aria-label="Dated asset results">
      <h3>Latest reviewed assets dated {visible.asOfDate} · {visible.currency}</h3>
      <p>These are customer-reviewed Investment, Property and Vehicle records. Missing assets, other asset kinds, dates and currencies, ownership and independent valuation remain outside this view. Each kind is read independently; this is current reviewed knowledge, not a single historical point in time. No total assets, net worth or complete asset position is established.</p>
      {(['investments','properties','vehicles'] as const).map(kind => <p key={kind}>{kind === 'investments' ? 'Investments' : kind === 'properties' ? 'Properties' : 'Vehicles'}: {visible[kind].items.length} matching reviewed records{visible[kind].hasMore ? ' · Showing the first 200 for this kind in order added. More matching records exist.' : ''}</p>)}
      {rows(visible).length === 0 && <p>No saved asset records match this exact date and currency. This does not mean zero assets.</p>}
      {repeatedSources && <Alert type="warning" title="More than one record references the same source. Review for overlap before using these values." />}
      {rows(visible).map(record => <Card key={record.kind + ':' + record.id} size="small" style={{ marginBlock: 12 }}><h4>{record.name}</h4><Tag>Customer reviewed</Tag><Tag>{record.kind}</Tag>
        <dl><dt>Reviewed details</dt><dd>{record.details}</dd><dt>Purchase value</dt><dd>{new Intl.NumberFormat('en', { style: 'currency', currency: record.currency }).format(record.purchase)}</dd>
          <dt>Current value</dt><dd>{new Intl.NumberFormat('en', { style: 'currency', currency: record.currency }).format(record.current)}</dd>
          <dt>Value as of</dt><dd>{record.date}</dd><dt>Saved version</dt><dd>{record.revision}</dd><dt>Reviewed</dt><dd>{record.recordedAt}</dd><dt>Source revision</dt><dd>{record.sourceRevision}</dd></dl>
        <a href={'/finances?fact=' + encodeURIComponent(record.source)}>View source history for {record.name}</a>
      </Card>)}
    </section>}
  </section></Card>;
}
