import { WorkspaceIcon } from '@monergy/ui-foundation';
import { Alert, Button, Collapse, Drawer, Skeleton, Spin, Tag } from 'antd';
import { useCallback, useEffect, useMemo, useState } from 'react';
import { AccessApiError } from '../access/accessApi';
import { useWorkspaceSession } from '../access/workspaceSession';
import {
  getFinancialFactDetail,
  getFinancialProfile,
  type ProductFinancialFact,
  type ProductFinancialFactDetail,
  type ProductFinancialProfile,
} from './productApi';
import './product.css';

const formatDate = (value: string) =>
  new Intl.DateTimeFormat(undefined, { dateStyle: 'medium' }).format(new Date(value));

const formatDateTime = (value: string) =>
  new Intl.DateTimeFormat(undefined, {
    dateStyle: 'medium',
    timeStyle: 'short',
  }).format(new Date(value));

const formatMoney = (fact: ProductFinancialFact) =>
  new Intl.NumberFormat(undefined, {
    style: 'currency',
    currency: fact.currency,
  }).format(fact.value);

const factTypeName = (value: string) =>
  ({ INCOME: 'Income', EXPENSE: 'Expense', LOAN: 'Loan information', ASSET: 'Asset source figure' })[value] ?? value;

type FinancialCategory = 'INCOME' | 'EXPENSE' | 'LOAN' | 'ASSET';

const categoryFromLocation = (): FinancialCategory | '' => {
  const value = new URLSearchParams(window.location.search).get('type')?.toUpperCase();
  return value === 'INCOME' || value === 'EXPENSE' || value === 'LOAN' || value === 'ASSET' ? value : '';
};

const initialFactId = () => new URLSearchParams(window.location.search).get('fact') ?? '';

export default function FinancialProfileExperience() {
  const { api, authorization } = useWorkspaceSession();
  const product = authorization?.area === 'product' ? authorization.context : null;
  const canRead = product?.capabilities.includes('financial-profile.profile.read') ?? false;
  const [profile, setProfile] = useState<ProductFinancialProfile | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [activeCategory, setActiveCategory] = useState<FinancialCategory | ''>(categoryFromLocation);
  const [selectedFactId, setSelectedFactId] = useState(initialFactId);
  const [detail, setDetail] = useState<ProductFinancialFactDetail | null>(null);
  const [detailBusy, setDetailBusy] = useState(false);
  const [detailError, setDetailError] = useState('');

  const load = useCallback(
    async (signal?: AbortSignal) => {
      if (!api || !canRead) {
        setProfile(null);
        return;
      }
      setBusy(true);
      setError('');
      try {
        setProfile(await getFinancialProfile(api, signal));
      } catch (failure) {
        if (!signal?.aborted) {
          setError(
            failure instanceof AccessApiError && failure.status === 403
              ? "Financial information isn't included in your current access. Contact your organization administrator."
              : "We couldn't load your financial information. Try again.",
          );
        }
      } finally {
        if (!signal?.aborted) setBusy(false);
      }
    },
    [api, canRead],
  );

  useEffect(() => {
    const controller = new AbortController();
    const start = window.setTimeout(() => {
      void load(controller.signal);
    }, 0);
    return () => {
      window.clearTimeout(start);
      controller.abort();
    };
  }, [load]);

  useEffect(() => {
    const syncFromLocation = () => {
      setSelectedFactId(new URLSearchParams(window.location.search).get('fact') ?? '');
      setActiveCategory(categoryFromLocation());
    };
    window.addEventListener('popstate', syncFromLocation);
    return () => window.removeEventListener('popstate', syncFromLocation);
  }, []);

  useEffect(() => {
    if (!api || !canRead || !selectedFactId) {
      setDetail(null);
      return;
    }
    const controller = new AbortController();
    setDetailBusy(true);
    setDetailError('');
    setDetail(null);
    void getFinancialFactDetail(api, selectedFactId, controller.signal)
      .then(setDetail)
      .catch((failure) => {
        if (controller.signal.aborted) return;
        setDetailError(
          failure instanceof AccessApiError && failure.status === 403
            ? "You no longer have access to view this financial information or its source."
            : failure instanceof AccessApiError && failure.status === 404
              ? "This financial information is no longer available in your current profile."
              : "We couldn't load the source history for this item. Try again.",
        );
      })
      .finally(() => {
        if (!controller.signal.aborted) setDetailBusy(false);
      });
    return () => controller.abort();
  }, [api, canRead, selectedFactId]);

  const groups = useMemo(() => {
    const facts = profile?.facts ?? [];
    return [
      { key: 'INCOME' as const, title: 'Income', facts: facts.filter((fact) => fact.factType === 'INCOME') },
      { key: 'EXPENSE' as const, title: 'Expenses', facts: facts.filter((fact) => fact.factType === 'EXPENSE') },
      { key: 'LOAN' as const, title: 'Loan information', facts: facts.filter((fact) => fact.factType === 'LOAN') },
      { key: 'ASSET' as const, title: 'Asset source figures', facts: facts.filter((fact) => fact.factType === 'ASSET') },
    ].filter(
      (group) => (!activeCategory || group.key === activeCategory) && group.facts.length > 0,
    );
  }, [activeCategory, profile]);

  const selectCategory = (category: FinancialCategory | '') => {
    setActiveCategory(category);
    setSelectedFactId('');
    setDetail(null);
    setDetailError('');
    const url = category ? '/finances?type=' + encodeURIComponent(category) : '/finances';
    if (window.location.pathname + window.location.search !== url)
      window.history.pushState(null, '', url);
  };

  const openFact = (financialFactId: string) => {
    setSelectedFactId(financialFactId);
    setDetail(null);
    setDetailError('');
    const params = new URLSearchParams();
    if (activeCategory) params.set('type', activeCategory);
    params.set('fact', financialFactId);
    const url = '/finances?' + params.toString();
    if (window.location.pathname + window.location.search !== url)
      window.history.pushState(null, '', url);
  };

  const closeFact = () => {
    setSelectedFactId('');
    setDetail(null);
    setDetailError('');
    if (window.location.pathname === '/finances') {
      const url = activeCategory
        ? '/finances?type=' + encodeURIComponent(activeCategory)
        : '/finances';
      window.history.replaceState(null, '', url);
    }
  };

  return (
    <main className="product-page financial-profile-page">
      <header className="product-heading">
        <div>
          <p className="workspace-eyebrow">FINANCIAL INFORMATION</p>
          <h1>Your trusted financial information.</h1>
          <p>
            Review the income and expense information you have added from your documents, together
            with the source behind each item.
          </p>
        </div>
        <Tag className="product-environment">TEST ENVIRONMENT · TEMPORARY DATA</Tag>
      </header>

      {!canRead && (
        <Alert
          showIcon
          type="warning"
          title="Financial information isn't included in your current access"
          description="Your organization administrator can review your Customer access package."
        />
      )}
      {error && <Alert showIcon type="error" title={error} />}

      {busy && !profile ? (
        <section className="product-panel">
          <Skeleton active paragraph={{ rows: 5 }} />
        </section>
      ) : profile && profile.facts.length > 0 ? (
        <>
          <section className="product-financial-summary" aria-label="Financial information summary">
            <div className="product-panel product-summary-card">
              <span className="product-icon">
                <WorkspaceIcon name="reports" size={20} />
              </span>
              <div>
                <span>Current revision</span>
                <strong>{profile.revision}</strong>
              </div>
            </div>
            <div className="product-panel product-summary-card">
              <span className="product-icon">
                <WorkspaceIcon name="evidence" size={20} />
              </span>
              <div>
                <span>Trusted items</span>
                <strong>{profile.facts.length}</strong>
              </div>
            </div>
            <div className="product-panel product-summary-card">
              <span className="product-icon">
                <WorkspaceIcon name="check" size={20} />
              </span>
              <div>
                <span>Last updated</span>
                <strong>{profile.updatedAt ? formatDateTime(profile.updatedAt) : '—'}</strong>
              </div>
            </div>
          </section>

          <nav className="product-financial-category-filter" aria-label="Financial information categories">
            <Button
              type={activeCategory === '' ? 'primary' : 'default'}
              onClick={() => selectCategory('')}
            >
              All
            </Button>
            <Button
              type={activeCategory === 'INCOME' ? 'primary' : 'default'}
              onClick={() => selectCategory('INCOME')}
            >
              Income
            </Button>
            <Button
              type={activeCategory === 'EXPENSE' ? 'primary' : 'default'}
              onClick={() => selectCategory('EXPENSE')}
            >
              Expenses
            </Button>
            <Button
              type={activeCategory === 'LOAN' ? 'primary' : 'default'}
              onClick={() => selectCategory('LOAN')}
            >
              Loan information
            </Button>
            <Button
              type={activeCategory === 'ASSET' ? 'primary' : 'default'}
              onClick={() => selectCategory('ASSET')}
            >
              Asset source figures
            </Button>
          </nav>

          {groups.map((group) => (
            <section key={group.key} className="product-panel product-financial-section">
              <div className="product-section-heading">
                <div>
                  <p className="workspace-eyebrow">{group.key}</p>
                  <h2>{group.title}</h2>
                </div>
                <span>
                  {group.facts.length} {group.facts.length === 1 ? 'item' : 'items'}
                </span>
              </div>
              <div className="product-financial-list">
                {group.facts.map((fact) => (
                  <article key={fact.financialFactId} className="product-financial-card">
                    <div className="product-financial-value">
                      <Tag>{factTypeName(fact.factType)}</Tag>
                      <strong>{formatMoney(fact)}</strong>
                      <span>{fact.label}</span>
                    </div>
                    <div className="product-financial-source">
                      <span>Effective {formatDate(fact.effectiveDate)}</span>
                      <strong>Source: {fact.source.sourceName}</strong>
                      <span>
                        Recorded {formatDateTime(fact.source.recordedAt)} · revision {fact.revision}
                      </span>
                    </div>
                    <div className="product-financial-card-actions">
                      <Button type="link" onClick={() => openFact(fact.financialFactId)}>
                        View source &amp; history
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
                                <dt>Financial fact</dt>
                                <dd>{fact.financialFactId}</dd>
                              </div>
                              <div>
                                <dt>Financial provenance</dt>
                                <dd>{fact.source.technicalProvenanceId}</dd>
                              </div>
                              <div>
                                <dt>Document version</dt>
                                <dd>{fact.source.documentVersion}</dd>
                              </div>
                              <div>
                                <dt>Source fact</dt>
                                <dd>{fact.source.technicalSourceFactId}</dd>
                              </div>
                            </dl>
                          ),
                        },
                      ]}
                    />
                  </article>
                ))}
              </div>
            </section>
          ))}
          {activeCategory && groups.length === 0 && (
            <section className="product-panel product-empty-financial product-category-empty">
              <span className="product-empty-icon">
                <WorkspaceIcon name="reports" size={28} />
              </span>
              <p className="workspace-eyebrow">{activeCategory}</p>
              <h2>No trusted {activeCategory === 'INCOME' ? 'income' : activeCategory === 'LOAN' ? 'loan' : activeCategory === 'ASSET' ? 'asset' : 'expense'} information yet.</h2>
              <p>
                This category has no reviewed financial information yet. Review supported documents
                and add only the Income, Expense, Loan or Asset source details you have checked.
              </p>
              <div className="product-category-empty-actions">
                <Button type="primary" href="/evidence">
                  Review documents
                </Button>
                <Button onClick={() => selectCategory('')}>Show all financial information</Button>
              </div>
            </section>
          )}
        </>
      ) : canRead && !error ? (
        <section className="product-panel product-empty-financial">
          <span className="product-empty-icon">
            <WorkspaceIcon name="reports" size={30} />
          </span>
          <p className="workspace-eyebrow">NOTHING ADDED YET</p>
          <h2>Your financial information will appear here after you review it.</h2>
          <p>
            Open a supported document, extract its Income, Expense or Loan details, select the items you
            have checked, and explicitly add them to your financial information.
          </p>
          <Button type="primary" href="/evidence">
            Review documents
          </Button>
        </section>
      ) : null}

      <Drawer
        title="Financial information details"
        aria-label="Financial information details"
        open={Boolean(selectedFactId)}
        onClose={closeFact}
        destroyOnHidden
        size="large"
      >
        {detailBusy && (
          <div className="product-detail-loading" aria-live="polite">
            <Spin />
            <p>Loading source history…</p>
          </div>
        )}
        {detailError && <Alert showIcon type="error" title={detailError} />}
        {detail && (
          <div className="product-financial-detail">
            <section className="product-financial-detail-hero">
              <div>
                <Tag>{factTypeName(detail.current.factType)}</Tag>
                <p className="workspace-eyebrow">TRUSTED INFORMATION</p>
                <h2>{detail.current.label}</h2>
                <strong>{formatMoney(detail.current)}</strong>
              </div>
              <dl>
                <div>
                  <dt>Effective</dt>
                  <dd>{formatDate(detail.current.effectiveDate)}</dd>
                </div>
                <div>
                  <dt>Current revision</dt>
                  <dd>{detail.current.revision}</dd>
                </div>
                <div>
                  <dt>Current source</dt>
                  <dd>{detail.current.source.sourceName}</dd>
                </div>
                <div>
                  <dt>Recorded</dt>
                  <dd>{formatDateTime(detail.current.source.recordedAt)}</dd>
                </div>
              </dl>
              <Button
                type="primary"
                href={
                  '/evidence?version=' +
                  encodeURIComponent(detail.current.source.documentVersion)
                }
              >
                Open source document
              </Button>
            </section>

            <section className="product-financial-history" aria-labelledby="financial-history-title">
              <div className="product-section-heading">
                <div>
                  <p className="workspace-eyebrow">SOURCE HISTORY</p>
                  <h2 id="financial-history-title">How this information changed</h2>
                </div>
                <span>
                  {detail.history.length}{' '}
                  {detail.history.length === 1 ? 'revision' : 'revisions'}
                </span>
              </div>
              <div className="product-financial-history-list">
                {detail.history.map((revision) => (
                  <article
                    key={revision.revision}
                    className={
                      revision.revision === detail.current.revision
                        ? 'product-financial-history-item current'
                        : 'product-financial-history-item'
                    }
                  >
                    <div className="product-financial-history-heading">
                      <span>
                        Revision {revision.revision}
                        {revision.revision === detail.current.revision && (
                          <Tag color="success">CURRENT</Tag>
                        )}
                      </span>
                      <strong>{formatMoney(revision)}</strong>
                    </div>
                    <div className="product-financial-history-meta">
                      <span>Effective {formatDate(revision.effectiveDate)}</span>
                      <span>Source: {revision.source.sourceName}</span>
                      <span>Recorded {formatDateTime(revision.source.recordedAt)}</span>
                    </div>
                    <Button
                      type="link"
                      href={
                        '/evidence?version=' +
                        encodeURIComponent(revision.source.documentVersion)
                      }
                    >
                      View this source document
                    </Button>
                  </article>
                ))}
              </div>
            </section>

            <Collapse
              items={[
                {
                  key: 'technical',
                  label: 'Technical details',
                  children: (
                    <div className="product-financial-technical-history">
                      {detail.history.map((revision) => (
                        <dl className="product-technical-grid" key={revision.revision}>
                          <div>
                            <dt>Revision</dt>
                            <dd>{revision.revision}</dd>
                          </div>
                          <div>
                            <dt>Financial fact</dt>
                            <dd>{revision.financialFactId}</dd>
                          </div>
                          <div>
                            <dt>Financial provenance</dt>
                            <dd>{revision.source.technicalProvenanceId}</dd>
                          </div>
                          <div>
                            <dt>Document version</dt>
                            <dd>{revision.source.documentVersion}</dd>
                          </div>
                          <div>
                            <dt>Source fact</dt>
                            <dd>{revision.source.technicalSourceFactId}</dd>
                          </div>
                        </dl>
                      ))}
                    </div>
                  ),
                },
              ]}
            />
          </div>
        )}
      </Drawer>
    </main>
  );
}
