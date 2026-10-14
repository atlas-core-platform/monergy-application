import { WorkspaceIcon } from '@monergy/ui-foundation';
import type { WorkspaceIconName } from '@monergy/ui-foundation';
import { Tag } from 'antd';
import { useWorkspaceSession } from '../access/workspaceSession';

const journeys: {
  title: string;
  description: string;
  href: string;
  icon: WorkspaceIconName;
  capability: string;
  action: string;
}[] = [
  {
    title: 'Add financial evidence',
    description: 'Upload a source document and receive a traceable evidence version.',
    href: '/evidence',
    icon: 'evidence',
    capability: 'evidence.document.upload',
    action: 'Add evidence',
  },
  {
    title: 'Search trusted information',
    description: 'Find authorized information and follow each result to its source.',
    href: '/search',
    icon: 'search',
    capability: 'search.query.execute',
    action: 'Open search',
  },
  {
    title: 'Build a financial report',
    description: 'Bring available financial facts together with their provenance.',
    href: '/reports',
    icon: 'reports',
    capability: 'reporting.report.generate',
    action: 'Open reports',
  },
];

export default function WorkspaceHome() {
  const { authorization } = useWorkspaceSession();
  const context = authorization?.area === 'product' ? authorization.context : null;
  const capabilities = new Set(context?.capabilities ?? journeys.map((item) => item.capability));
  const available = journeys.filter((item) => capabilities.has(item.capability));
  return (
    <main className="workspace-home product-home">
      <div className="workspace-page-heading">
        <div>
          <p className="workspace-eyebrow">FINANCIAL WORKSPACE</p>
          <h1>Good {new Date().getUTCHours() < 12 ? 'morning' : 'to see you'}.</h1>
          <p className="workspace-subtitle">
            {context?.customerName ?? 'Your Monergy workspace'} · Move from source evidence to
            financial clarity, with every step traceable.
          </p>
        </div>
        <Tag className="product-environment">LOCAL UAT · RUNTIME DATA</Tag>
      </div>

      <section className="workspace-hero" aria-labelledby="workspace-hero-title">
        <div className="workspace-hero-copy">
          <span className="workspace-pill">YOUR NEXT BEST STEP</span>
          <h2 id="workspace-hero-title">Start with what you know.</h2>
          <p>
            Add a financial document. Monergy will verify the source, register its identity and keep
            its provenance ready for the journeys that follow.
          </p>
          {capabilities.has('evidence.document.upload') ? (
            <a className="workspace-primary-link" href="/evidence">
              Add financial evidence <WorkspaceIcon name="arrow" size={18} />
            </a>
          ) : (
            <span className="workspace-unavailable">
              Your current role does not include evidence upload.
            </span>
          )}
          <span className="workspace-hero-note">
            <WorkspaceIcon name="shield" size={14} />
            Customer ownership and access are checked by the authoritative services.
          </span>
        </div>
        <div className="workspace-hero-art" aria-hidden="true">
          <div className="workspace-art-grid" />
          <div className="workspace-orbit workspace-orbit-outer" />
          <div className="workspace-orbit workspace-orbit-inner" />
          <div className="workspace-art-core">
            <WorkspaceIcon name="evidence" size={39} />
          </div>
          <div className="workspace-art-chip chip-people">
            <span>
              <WorkspaceIcon name="evidence" />
            </span>
            <div>
              <strong>Source</strong>
              <small>Your original evidence</small>
            </div>
          </div>
          <div className="workspace-art-chip chip-permissions">
            <span>
              <WorkspaceIcon name="check" />
            </span>
            <div>
              <strong>Verified</strong>
              <small>Identity and hash recorded</small>
            </div>
          </div>
          <div className="workspace-art-chip chip-workspace">
            <span>
              <WorkspaceIcon name="changes" />
            </span>
            <div>
              <strong>Traceable</strong>
              <small>Provenance stays connected</small>
            </div>
          </div>
        </div>
      </section>

      <section aria-labelledby="workspace-areas-title">
        <div className="workspace-section-heading">
          <div>
            <p className="workspace-eyebrow">AVAILABLE TO YOU</p>
            <h2 id="workspace-areas-title">Continue your financial journey</h2>
          </div>
          <span>
            {available.length} of {journeys.length} journeys available
          </span>
        </div>
        {available.length ? (
          <div className="workspace-area-grid">
            {available.map((area, index) => (
              <a className="workspace-area-card" href={area.href} key={area.title}>
                <div className="workspace-card-top">
                  <span className="workspace-card-icon">
                    <WorkspaceIcon name={area.icon} size={21} />
                  </span>
                  <span className="workspace-card-number">0{index + 1}</span>
                </div>
                <h3>{area.title}</h3>
                <p>{area.description}</p>
                <span className="workspace-card-action">
                  {area.action}
                  <WorkspaceIcon name="arrow" size={17} />
                </span>
              </a>
            ))}
          </div>
        ) : (
          <div className="workspace-access-empty">
            <WorkspaceIcon name="shield" size={23} />
            <div>
              <strong>Your workspace is connected, but no product journeys are assigned.</strong>
              <p>Ask a Tenant Administrator to assign an appropriate business role.</p>
            </div>
          </div>
        )}
      </section>

      <section className="workspace-boundary" aria-labelledby="workspace-boundary-title">
        <span className="workspace-card-icon">
          <WorkspaceIcon name="shield" size={20} />
        </span>
        <div>
          <p className="workspace-eyebrow">CLEAR BOUNDARIES</p>
          <h2 id="workspace-boundary-title">
            You are working as {context?.customerName ?? 'a customer'}.
          </h2>
          <p>
            Monergy resolves this customer from your verified identity. The browser does not choose
            or infer the customer record.
          </p>
        </div>
        <div className="workspace-boundary-facts">
          <span>
            <small>Environment</small>
            {context?.environment ?? 'Preview'}
          </span>
          <span>
            <small>Data durability</small>
            {context?.dataDurability ?? 'Reference'}
          </span>
        </div>
      </section>
    </main>
  );
}
