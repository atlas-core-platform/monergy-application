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
    title: 'Add financial documents',
    description: 'Upload a document and keep its source details connected for later review.',
    href: '/evidence',
    icon: 'evidence',
    capability: 'evidence.document.upload',
    action: 'Add document',
  },
  {
    title: 'Search your information',
    description: 'Find authorized information and follow each result back to its source.',
    href: '/search',
    icon: 'search',
    capability: 'search.query.execute',
    action: 'Open search',
  },
  {
    title: 'Build a financial report',
    description: 'Bring available financial information together with where it came from.',
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
          <p className="workspace-eyebrow">MY FINANCES</p>
          <h1>Good {new Date().getUTCHours() < 12 ? 'morning' : 'to see you'}.</h1>
          <p className="workspace-subtitle">
            {context?.customerName ?? 'Your Monergy workspace'} · Bring your financial information
            together and keep every result connected to its source.
          </p>
        </div>
        <Tag className="product-environment">TEST ENVIRONMENT · TEMPORARY DATA</Tag>
      </div>

      <section className="workspace-hero" aria-labelledby="workspace-hero-title">
        <div className="workspace-hero-copy">
          <span className="workspace-pill">YOUR NEXT BEST STEP</span>
          <h2 id="workspace-hero-title">Start with what you already have.</h2>
          <p>
            Add a financial document. Monergy keeps the document version and source details
            connected so you can review extracted information before anything reaches your
            Financial Passport.
          </p>
          {capabilities.has('evidence.document.upload') ? (
            <a className="workspace-primary-link" href="/evidence">
              Add financial document <WorkspaceIcon name="arrow" size={18} />
            </a>
          ) : (
            <span className="workspace-unavailable">
              Your current access does not include document upload.
            </span>
          )}
          <span className="workspace-hero-note">
            <WorkspaceIcon name="shield" size={14} />
            Your identity, customer ownership and current access are checked by Monergy.
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
              <strong>Document</strong>
              <small>Your original source</small>
            </div>
          </div>
          <div className="workspace-art-chip chip-permissions">
            <span>
              <WorkspaceIcon name="check" />
            </span>
            <div>
              <strong>Checked</strong>
              <small>Version and integrity recorded</small>
            </div>
          </div>
          <div className="workspace-art-chip chip-workspace">
            <span>
              <WorkspaceIcon name="changes" />
            </span>
            <div>
              <strong>Connected</strong>
              <small>Source details stay linked</small>
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
              <strong>Your workspace is connected, but no financial journeys are available.</strong>
              <p>Contact your Organization Administrator if you need additional access.</p>
            </div>
          </div>
        )}
      </section>

      <section className="workspace-boundary" aria-labelledby="workspace-boundary-title">
        <span className="workspace-card-icon">
          <WorkspaceIcon name="shield" size={20} />
        </span>
        <div>
          <p className="workspace-eyebrow">YOUR ACCESS</p>
          <h2 id="workspace-boundary-title">
            You are viewing {context?.customerName ?? 'your own financial workspace'}.
          </h2>
          <p>
            Monergy resolves this customer from your verified identity. You do not choose or enter
            an internal customer ID.
          </p>
        </div>
        <div className="workspace-boundary-facts">
          <span>
            <small>Environment</small>
            {context?.environment ?? 'Preview'}
          </span>
          <span>
            <small>Data</small>
            {context?.dataDurability ?? 'Temporary'}
          </span>
        </div>
      </section>
    </main>
  );
}
