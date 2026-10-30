import { WorkspaceIcon } from '@monergy/ui-foundation';
import { Alert, Button, Collapse, Empty, Input, Radio, Skeleton, Tag } from 'antd';
import { useEffect, useMemo, useRef, useState } from 'react';
import { AccessApiError } from '../access/accessApi';
import { RequestAccessAction } from '../access/RequestAccess';
import { useWorkspaceSession } from '../access/workspaceSession';
import type { SearchHit, SearchResults } from '../search/types';
import {
  searchProductInformation,
  type ProductSearchScope,
} from './productApi';
import './product.css';

const resultLabel = (result: SearchHit) =>
  result.resultType === 'Document' ? 'Document' : 'Financial information';

const resultHref = (result: SearchHit) => {
  if (result.resultType === 'Document')
    return '/evidence?version=' + encodeURIComponent(result.source.sourceObjectId);
  if (result.source.sourceObjectType === 'TaxRecord')
    return '/finances?type=TAX&taxRecord=' + encodeURIComponent(result.source.sourceObjectId);
  return '/finances?fact=' + encodeURIComponent(result.source.sourceObjectId);
};

export default function ProductSearchExperience() {
  const { session, authorization } = useWorkspaceSession();
  return <ProductSearchPanel key={JSON.stringify([session, authorization])} />;
}

function ProductSearchPanel() {
  const { api, authorization } = useWorkspaceSession();
  const product = authorization?.area === 'product' ? authorization.context : null;
  const capabilities = new Set(product?.capabilities ?? []);
  const canSearch = capabilities.has('search.query.execute');
  const canDocuments = canSearch && capabilities.has('evidence.document.read');
  const canFinancial = canSearch && capabilities.has('financial-profile.profile.read');
  const canAll = canDocuments && canFinancial;
  const [scope, setScope] = useState<ProductSearchScope>('all');
  const [query, setQuery] = useState('');
  const [result, setResult] = useState<SearchResults | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const active = useRef<AbortController | null>(null);
  useEffect(() => () => { active.current?.abort(); }, []);
  const canAsk = ['ai-intelligence.analysis.execute', 'search.query.execute', 'financial-profile.profile.read', 'evidence.document.read'].every(capability => capabilities.has(capability));

  const trimmed = query.trim();
  const selectedScopeAvailable = scope === 'all' ? canAll : scope === 'documents' ? canDocuments : canFinancial;
  const effectiveScope: ProductSearchScope = selectedScopeAvailable
    ? scope
    : canAll ? 'all' : canDocuments ? 'documents' : canFinancial ? 'financial' : 'all';
  const scopeAvailable = effectiveScope === 'all' ? canAll : effectiveScope === 'documents' ? canDocuments : canFinancial;
  const canSubmit = canSearch && scopeAvailable && !busy && trimmed.length > 0;

  const message = (failure: unknown) => {
    if (failure instanceof AccessApiError) {
      if (failure.status === 403)
        return "Search isn't included in your current access.";
      if (failure.status === 422)
        return 'This search mode is not available in the current customer experience.';
      if (failure.status === 503)
        return "We couldn't refresh your searchable information. Try again.";
    }
    return "We couldn't search your information. Try again.";
  };

  const submit = async () => {
    if (!api || !canSubmit) return;
    active.current?.abort();
    const controller = new AbortController(); active.current = controller;
    setBusy(true);
    setError('');
    setResult(null);
    try {
      const value = await searchProductInformation(api, trimmed, effectiveScope, controller.signal);
      if (!controller.signal.aborted) setResult(value);
    } catch (failure) {
      if (!controller.signal.aborted) setError(message(failure));
    } finally {
      if (!controller.signal.aborted) setBusy(false);
    }
  };

  const resultSummary = useMemo(() => {
    if (!result) return '';
    return (
      String(result.items.length) +
      ' ' +
      (result.items.length === 1 ? 'result' : 'results') +
      ' for “' +
      result.query +
      '”'
    );
  }, [result]);

  return (
    <main className="product-page product-search-page">
      <header className="product-heading">
        <div>
          <p className="workspace-eyebrow">SEARCH</p>
          <h1>Find information already in your Monergy workspace.</h1>
          <p>
            Search your current Documents, trusted financial source facts and customer-reviewed tax records. Results are
            rebuilt from the current authorized sources and link back to the exact document or
            financial item. Loan source amounts are not verified outstanding balances, EMI or eligibility; Asset source figures are not certified valuations or net worth; Investment source figures are not live holdings, NAV or market values; Insurance source figures are not verified policy coverage, claim benefits, premiums or cash value. Tax source figures and reviewed tax records are not assessed tax owed, accepted filings, payment confirmation, refunds or tax advice. This search uses lexical matching only.
          </p>
        </div>
        <Tag className="product-environment">
          {(product?.environment ?? 'Test environment').toUpperCase()} ·{' '}
          {(product?.dataDurability ?? 'Temporary data').toUpperCase()}
        </Tag>
      </header>

      {canAsk && <section className="product-panel" aria-label="Natural-language financial search">
        <h2>Search with a question</h2>
        <p>Ask about your recorded financial information in your own words. Review the configured provider notice and give permission before a question is sent. Results include source references; missing information is stated explicitly.</p>
        <Button href="/financial-qa">Ask a financial question</Button>
      </section>}

      {!canSearch && (
        <Alert
          showIcon
          type="warning"
          title="Search isn't included in your current access"
          description="Your organization can review the recommended Customer access update before enabling this journey."
        />
      )}
      {canSearch && !canDocuments && <RequestAccessAction
        target="customer.documents"
        requiredCapabilities={['evidence.document.read']}
        title="Document search access required"
        description="Request access to search your current Documents. Financial-information search remains available when permitted."
      />}
      {canSearch && !canFinancial && <RequestAccessAction
        target="customer.financial-information"
        requiredCapabilities={['financial-profile.profile.read']}
        title="Financial search access required"
        description="Request access to search trusted financial information. Document search remains available when permitted."
      />}
      {error && <Alert showIcon type="error" title={error} />}

      <section className="product-panel product-search-controls" aria-label="Search your information">
        <div>
          <label htmlFor="product-search-query">Search query</label>
          <Input.Search
            id="product-search-query"
            value={query}
            maxLength={256}
            autoComplete="off"
            placeholder="Try salary, rent, statement, or a filename"
            enterButton="Search"
            loading={busy}
            disabled={!canSearch || !canDocuments && !canFinancial}
            onChange={(event) => { setQuery(event.target.value); }}
            onSearch={() => {
              void submit();
            }}
          />
        </div>
        <div>
          <span className="product-search-label">Where to search</span>
          <Radio.Group
            value={effectiveScope}
            disabled={!canSearch || !canDocuments && !canFinancial || busy}
            onChange={(event) => { setScope(event.target.value as ProductSearchScope); }}
            optionType="button"
            buttonStyle="solid"
          >
            <Radio.Button value="all" disabled={!canAll}>All</Radio.Button>
            <Radio.Button value="documents" disabled={!canDocuments}>Documents</Radio.Button>
            <Radio.Button value="financial" disabled={!canFinancial}>Financial information</Radio.Button>
          </Radio.Group>
        </div>
        <p className="product-search-note">
          <WorkspaceIcon name="shield" size={14} />
          Search does not broaden your access. Only currently permitted sources can be selected,
          and every query is rechecked before and after the owner reads.
        </p>
      </section>

      <section aria-live="polite" aria-busy={busy} aria-label="Search results">
        {busy && (
          <div className="product-panel">
            <Skeleton active paragraph={{ rows: 5 }} />
          </div>
        )}

        {!busy && result && (
          <>
            <div className="product-section-heading product-search-result-heading">
              <div>
                <p className="workspace-eyebrow">RESULTS</p>
                <h2>{resultSummary}</h2>
              </div>
              <span>Lexical · current authorized sources</span>
            </div>

            {result.items.length > 0 ? (
              <div className="product-search-results">
                {result.items.map((item) => (
                  <article className="product-panel product-search-result" key={item.searchRecordId}>
                    <div className="product-search-result-main">
                      <span className="product-search-result-icon">
                        <WorkspaceIcon
                          name={item.resultType === 'Document' ? 'evidence' : 'reports'}
                          size={20}
                        />
                      </span>
                      <div>
                        <Tag>{resultLabel(item)}</Tag>
                        <h3>{item.title}</h3>
                        <p>{item.summary}</p>
                      </div>
                    </div>
                    <div className="product-search-result-action">
                      <Button type="primary" href={resultHref(item)}>
                        {item.resultType === 'Document'
                          ? 'Open document'
                          : 'View source & history'}
                      </Button>
                    </div>
                    <Collapse
                      ghost
                      items={[
                        {
                          key: 'technical',
                          label: 'Technical details',
                          children: (
                            <dl className="product-technical-grid">
                              <div>
                                <dt>Derived search record</dt>
                                <dd>{item.searchRecordId}</dd>
                              </div>
                              <div>
                                <dt>Authoritative owner</dt>
                                <dd>{item.authoritativeOwner}</dd>
                              </div>
                              <div>
                                <dt>Source object</dt>
                                <dd>{item.source.sourceObjectId}</dd>
                              </div>
                              <div>
                                <dt>Source reference</dt>
                                <dd>{item.source.sourceReferenceId}</dd>
                              </div>
                              {item.source.provenanceReferenceId && (
                                <div>
                                  <dt>Provenance</dt>
                                  <dd>{item.source.provenanceReferenceId}</dd>
                                </div>
                              )}
                            </dl>
                          ),
                        },
                      ]}
                    />
                  </article>
                ))}
              </div>
            ) : (
              <div className="product-panel product-search-empty">
                <Empty
                  description={
                    effectiveScope === 'documents'
                      ? 'No current Documents matched this query.'
                      : effectiveScope === 'financial'
                        ? 'No trusted financial information matched this query.'
                        : 'No current Documents or trusted financial information matched this query.'
                  }
                />
              </div>
            )}
          </>
        )}

        {!busy && !result && canSearch && !error && (
          <div className="product-panel product-search-empty">
            <Empty description="Enter a word or phrase to search your current information." />
          </div>
        )}
      </section>
    </main>
  );
}
