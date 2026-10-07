import { WorkspaceIcon } from '@monergy/ui-foundation';
import type { WorkspaceIconName } from '@monergy/ui-foundation';
import { Collapse } from 'antd';

const areas: {
  title: string;
  description: string;
  href: string;
  icon: WorkspaceIconName;
  number: string;
  label: string;
}[] = [
  {
    title: 'Evidence & provenance',
    description:
      'Follow a document from source evidence to financial facts, with its provenance in view.',
    href: '/vs02',
    icon: 'evidence',
    number: '01',
    label: 'Explore evidence',
  },
  {
    title: 'Search & discovery',
    description: 'Find authorized information and follow each result back to its source.',
    href: '/search',
    icon: 'search',
    number: '02',
    label: 'Open search',
  },
  {
    title: 'Financial reports',
    description: 'Bring financial facts together and explore the evidence behind a report.',
    href: '/reports',
    icon: 'reports',
    number: '03',
    label: 'Explore reports',
  },
];

export default function WorkspaceHome() {
  return (
    <main className="workspace-home">
      <div className="workspace-page-heading">
        <div>
          <p className="workspace-eyebrow">YOUR MONERGY WORKSPACE</p>
          <h1>A clearer view of everything.</h1>
          <p className="workspace-subtitle">
            Your workflows, people and platform knowledge. Connected in one place.
          </p>
        </div>
        <span className="workspace-date">LOCAL EXPERIENCE</span>
      </div>
      <section className="workspace-hero" aria-labelledby="workspace-hero-title">
        <div className="workspace-hero-copy">
          <span className="workspace-pill">BUILT AROUND YOUR TEAM</span>
          <h2 id="workspace-hero-title">
            The right access.
            <br />A confident start.
          </h2>
          <p>
            Bring people into your workspace, organize their roles, and make every permission
            intentional.
          </p>
          <a className="workspace-primary-link" href="/access">
            Manage your workspace <WorkspaceIcon name="arrow" size={18} />
          </a>
          <span className="workspace-hero-note">
            <WorkspaceIcon name="shield" size={14} />
            Tenant-scoped. Verified by your services.
          </span>
        </div>
        <div className="workspace-hero-art" aria-hidden="true">
          <div className="workspace-art-grid" />
          <div className="workspace-orbit workspace-orbit-outer" />
          <div className="workspace-orbit workspace-orbit-inner" />
          <div className="workspace-art-core">
            <WorkspaceIcon name="access" size={42} />
          </div>
          <div className="workspace-art-chip chip-people">
            <span>
              <WorkspaceIcon name="users" />
            </span>
            <div>
              <strong>People</strong>
              <small>A place for every teammate</small>
            </div>
          </div>
          <div className="workspace-art-chip chip-permissions">
            <span>
              <WorkspaceIcon name="shield" />
            </span>
            <div>
              <strong>Permissions</strong>
              <small>Purposeful by design</small>
            </div>
          </div>
          <div className="workspace-art-chip chip-workspace">
            <span>
              <WorkspaceIcon name="layers" />
            </span>
            <div>
              <strong>One workspace</strong>
              <small>Shared clarity</small>
            </div>
          </div>
        </div>
      </section>
      <section aria-labelledby="workspace-areas-title">
        <div className="workspace-section-heading">
          <div>
            <p className="workspace-eyebrow">MOVE WORK FORWARD</p>
            <h2 id="workspace-areas-title">Explore your workspace</h2>
          </div>
          <span>Familiar tools. A connected experience.</span>
        </div>
        <div className="workspace-area-grid">
          {areas.map((area) => (
            <a className="workspace-area-card" href={area.href} key={area.title}>
              <div className="workspace-card-top">
                <span className="workspace-card-icon">
                  <WorkspaceIcon name={area.icon} size={23} />
                </span>
                <span className="workspace-card-number">{area.number}</span>
              </div>
              <h3>{area.title}</h3>
              <p>{area.description}</p>
              <span className="workspace-card-action">
                {area.label}
                <WorkspaceIcon name="arrow" size={17} />
              </span>
            </a>
          ))}
        </div>
      </section>
      <div className="workspace-bottom-grid">
        <section className="workspace-expert-card" aria-labelledby="expert-card-title">
          <span className="workspace-expert-symbol">
            <WorkspaceIcon name="expert" size={30} />
          </span>
          <div>
            <p className="workspace-eyebrow">PLATFORM INTELLIGENCE</p>
            <h2 id="expert-card-title">Understand the bigger picture.</h2>
            <p>Ask System Expert about services, decisions and the connections behind Monergy.</p>
            <a href="http://127.0.0.1:4310/">
              Open System Expert <WorkspaceIcon name="arrow" size={16} />
            </a>
          </div>
        </section>
        <section className="workspace-context-card" aria-labelledby="workspace-context-title">
          <h2 id="workspace-context-title">Designed for clear decisions</h2>
          <Collapse
            ghost
            items={[
              {
                key: 'boundaries',
                label: 'What does this workspace connect?',
                children: (
                  <p>
                    Access administration connects to local Customer & Identity and Access
                    Management services. Evidence, search and reports retain their existing
                    reference experiences.
                  </p>
                ),
              },
              {
                key: 'expert',
                label: 'Where does System Expert fit?',
                children: (
                  <p>
                    System Expert is a separate local knowledge application. It uses the same
                    workspace standards and does not participate in business transactions.
                  </p>
                ),
              },
            ]}
          />
        </section>
      </div>
      <footer className="workspace-home-footer">
        <span>MONERGY</span>
        <p>Clarity in every connection.</p>
        <a href="/foundation">
          Engineering foundation <WorkspaceIcon name="arrow" size={13} />
        </a>
      </footer>
    </main>
  );
}
