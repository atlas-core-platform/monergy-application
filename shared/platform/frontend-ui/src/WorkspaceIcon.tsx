export type WorkspaceIconName =
  | 'home'
  | 'evidence'
  | 'search'
  | 'reports'
  | 'access'
  | 'expert'
  | 'menu'
  | 'panel'
  | 'workflow'
  | 'map'
  | 'play'
  | 'decisions'
  | 'impact'
  | 'changes'
  | 'warning'
  | 'arrow'
  | 'help'
  | 'upload'
  | 'users'
  | 'shield'
  | 'layers'
  | 'close'
  | 'check';

const paths: Record<WorkspaceIconName, string> = {
  home: 'm3 10 9-7 9 7v10H3V10Zm6 10v-7h6v7',
  evidence: 'M6 3h8l4 4v14H6V3Zm8 0v5h4M9 12h6m-6 4h6',
  search: 'M10.5 18a7.5 7.5 0 1 0 0-15 7.5 7.5 0 0 0 0 15Zm5-2 6 6',
  reports: 'M4 20V4m0 16h17M8 16v-5m5 5V7m5 9V3',
  access: 'M12 3 4 6v6c0 5 8 9 8 9s8-4 8-9V6l-8-3Zm-4 9 3 3 5-6',
  expert: 'm12 2 3 7 7 3-7 3-3 7-3-7-7-3 7-3 3-7Z',
  menu: 'M4 6h16M4 12h16M4 18h16',
  panel: 'M3 4h18v16H3V4Zm6 0v16m7-12-4 4 4 4',
  workflow: 'M3 6h14m-4-4 4 4-4 4M21 18H7m4-4-4 4 4 4',
  map: 'M9 3h6v6H9V3ZM2 15h6v6H2v-6Zm14 0h6v6h-6v-6ZM12 9v3M5 15v-3h14v3',
  play: 'm8 4 12 8-12 8V4Z',
  decisions: 'M5 3h14v18H5V3Zm4 5h6m-6 4h6m-6 4h3',
  impact: 'm13 2-9 12h7l-1 8 10-13h-7l1-7Z',
  changes: 'M4 7h16m-4-4 4 4-4 4M20 17H4m4-4-4 4 4 4',
  warning: 'm12 3 10 18H2L12 3Zm0 6v5m0 3h.01',
  arrow: 'M4 12h16m-6-6 6 6-6 6',
  help: 'M9 9a3 3 0 1 1 5 2c-1 1-2 1-2 3m0 3h.01M22 12a10 10 0 1 1-20 0 10 10 0 0 1 20 0Z',
  upload: 'M12 16V3m-5 5 5-5 5 5M4 15v6h16v-6',
  users:
    'M16 21v-3a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v3m20 0v-3a4 4 0 0 0-3-4M9 10a4 4 0 1 0 0-8 4 4 0 0 0 0 8Zm9-7a4 4 0 0 1 0 7',
  shield: 'M12 3 4 6v6c0 5 8 9 8 9s8-4 8-9V6l-8-3Z',
  layers: 'm12 3 10 6-10 6L2 9l10-6ZM2 14l10 6 10-6M2 19l10 5 10-5',
  close: 'm6 6 12 12M6 18 18 6',
  check: 'm5 12 4 4L19 6',
};

export function WorkspaceIcon({ name, size = 20 }: { name: WorkspaceIconName; size?: number }) {
  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.7"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
    >
      <path d={paths[name]} />
    </svg>
  );
}
