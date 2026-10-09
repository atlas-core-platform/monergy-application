import type { WorkspaceDestination } from '@monergy/ui-foundation';

export const accessDestinations: WorkspaceDestination[] = [
  { id: 'overview', label: 'Overview', icon: 'home', href: '/access', group: 'Administration' },
  { id: 'users', label: 'Users', icon: 'users', href: '/access/users' },
  { id: 'roles', label: 'Roles', icon: 'shield', href: '/access/roles' },
  { id: 'permissions', label: 'Permissions', icon: 'check', href: '/access/permissions' },
  { id: 'groups', label: 'Groups', icon: 'layers', href: '/access/groups' },
  { id: 'resources', label: 'Resource Access', icon: 'map', href: '/access/resources' },
  {
    id: 'activity',
    label: 'Access Activity',
    icon: 'changes',
    href: '/access/activity',
    group: 'Security & oversight',
  },
  { id: 'sessions', label: 'Security & Sessions', icon: 'access', href: '/access/sessions' },
];
export const accessModule = {
  name: 'Access Management',
  homeHref: '/access',
  guide: (
    <>
      <h2>Give the right people the right access.</h2>
      <p>Create users, assign a business role, and review access changes within your tenant.</p>
      <h3>Start with Users</h3>
      <p>
        Create one user or import a CSV for a team. Open a user to change their role, status or
        administrator authority. Onboarding history shows incomplete requests and recovery options.
      </p>
      <h3>Build access with roles</h3>
      <p>
        Permissions describe allowed actions. A role combines permissions, and each user can have
        one business role. Resource Access assigns specific resources when the role requires them.
      </p>
      <h3>Keep organization separate</h3>
      <p>
        Groups organize users; they do not grant access. Tenant administrator authority controls
        administration and does not itself grant business permissions.
      </p>
      <h3>Review before you save</h3>
      <p>
        Changes show a summary before they are applied. Access Activity records changes. Security &
        Sessions lets you revoke all sessions for a user.
      </p>
      <h3>Navigate your way</h3>
      <p>
        Use Ctrl K or Command K to find a page. Collapse the sidebar for more room. Animation
        respects your device’s reduced-motion setting.
      </p>
    </>
  ),
};
