import { WorkspaceIcon } from '@monergy/ui-foundation';
import { Alert, Button, Collapse, Skeleton, Tag } from 'antd';
import { useCallback, useEffect, useMemo, useState } from 'react';
import { AccessApiError } from '../access/accessApi';
import { useWorkspaceSession } from '../access/workspaceSession';
import {
  getFinancialProfile,
  type ProductFinancialFact,
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
  ({ INCOME: 'Income', EXPENSE: 'Expense' })[value] ?? value;

export default function FinancialProfileExperience() {
  const { api, authorization } = useWorkspaceSession();
  const product = authorization?.area === 'product' ? authorization.context : null;
  const canRead = product?.capabilities.includes('financial-profile.profile.read') ?? false;
  const [profile, setProfile] = useState<ProductFinancialProfile | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');

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

  const groups = useMemo(() => {
    const facts = profile?.facts ?? [];
    return [
      { key: 'INCOME', title: 'Income', facts: facts.filter((fact) => fact.factType === 'INCOME') },
      { key: 'EXPENSE', title: 'Expenses', facts: facts.filter((fact) => fact.factType === 'EXPENSE') },
    ].filter((group) => group.facts.length > 0);
  }, [profile]);

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
                      <a
                        href={`/evidence?documentVersion=${encodeURIComponent(fact.source.documentVersion)}`}
                        aria-label={`Open source document ${fact.source.sourceName}`}
                      >
                        Source: {fact.source.sourceName}
                      </a>
                      <span>
                        Added {formatDateTime(fact.source.recordedAt)} · revision {fact.revision}
                      </span>
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
        </>
      ) : canRead && !error ? (
        <section className="product-panel product-empty-financial">
          <span className="product-empty-icon">
            <WorkspaceIcon name="reports" size={30} />
          </span>
          <p className="workspace-eyebrow">NOTHING ADDED YET</p>
          <h2>Your financial information will appear here after you review it.</h2>
          <p>
            Open a supported document, extract its Income or Expense details, select the items you
            have checked, and explicitly add them to your financial information.
          </p>
          <Button type="primary" href="/evidence">
            Review documents
          </Button>
        </section>
      ) : null}
    </main>
  );
}
