import type { WorkspaceDestination } from '@monergy/ui-foundation';

export const destinations: WorkspaceDestination[] = [
  { id: 'home', label: 'Overview', icon: 'home', href: '/', group: 'Your finances' },
  { id: 'evidence', label: 'Documents', icon: 'evidence', href: '/evidence' },
  { id: 'search', label: 'Search', icon: 'search', href: '/search' },
  { id: 'reports', label: 'Reports', icon: 'reports', href: '/reports' },
];

export const productModule = {
  name: 'Financial Workspace',
  homeHref: '/',
  navigationLabel: 'Jump to a financial page',
  findLabel: 'Find a financial page',
  guideLabel: 'Financial workspace guide',
  guideTitle: 'Your financial workspace guide',
  guide: (
    <>
      <h2>Turn your documents into clear financial information.</h2>
      <p>
        Start in Documents to add financial records. Search and Reports appear only when your
        current access includes those journeys.
      </p>
      <h3>Add documents</h3>
      <p>
        Drop or choose a supported file, review what will be uploaded, and wait for Monergy to
        confirm the saved document version.
      </p>
      <h3>Keep the source connected</h3>
      <p>
        Open a document to review its version and where extracted information came from. Technical
        identifiers and file-integrity details stay under Technical details.
      </p>
      <h3>Know this environment</h3>
      <p>
        This is a test environment. Documents and other reference data may be reset; Production
        retention and release remain separately governed.
      </p>
    </>
  ),
};
