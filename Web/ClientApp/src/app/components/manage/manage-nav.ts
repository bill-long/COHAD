import { Observable } from 'rxjs';
import { rolePermissions } from 'src/app/services/rolepermission.service';

/** One tool in the Manage rail. Visibility mirrors the old per-tab getters exactly. */
export interface ManageNavItem {
  label: string;
  /** Child route under /manage. */
  route: string;
  /** Material icon name. */
  icon: string;
  /** Roles that may see this tool (any-of). */
  roles: string[];
  /** When true, the tool also requires the Resident role (Events, News). */
  requireResident?: boolean;
  /** Live count rendered as a badge; the badge is hidden when the count is 0. */
  badgeCount$?: Observable<number>;
}

/** A labelled cluster of tools. A group renders only when at least one of its items is visible. */
export interface ManageNavGroup {
  label: string;
  items: ManageNavItem[];
}

/**
 * The Manage rail, as data. Lives in this component-free module (rather than manage.component.ts)
 * because the contextual help registry (app/help/help-topics.ts) derives its topic titles,
 * sections, and visibility from these same entries - one definition, so a renamed label or a moved
 * tool updates the help index automatically, and importing it does not drag the Manage shell
 * component along. Per-instance state (the Approvals badge stream) is grafted on in the
 * ManageComponent constructor.
 */
export const manageNavGroups: ManageNavGroup[] = [
  {
    label: 'Directory',
    items: [
      { label: 'Users', route: 'users', icon: 'group', roles: rolePermissions.manageUsersRoles },
      { label: 'Homes', route: 'homes', icon: 'home', roles: rolePermissions.manageHomesRoles },
      { label: 'Print Directory', route: 'print', icon: 'print', roles: rolePermissions.printDirectoryRoles },
    ],
  },
  {
    label: 'Communications',
    items: [
      { label: 'Email', route: 'send-email', icon: 'mail', roles: rolePermissions.manageEmailRoles },
      { label: 'Suppressions', route: 'suppressions', icon: 'unsubscribe', roles: rolePermissions.manageSuppressionsRoles },
      { label: 'News', route: 'blog', icon: 'article', roles: rolePermissions.manageBlogRoles, requireResident: true },
      { label: 'Events', route: 'events', icon: 'event', roles: rolePermissions.manageEventsRoles, requireResident: true },
      // Documents mirrors the old getter: gated by the manage-users (Administrator) role set.
      { label: 'Documents', route: 'documents', icon: 'folder', roles: rolePermissions.manageUsersRoles },
    ],
  },
  {
    label: 'Governance',
    items: [
      { label: 'Committees', route: 'committees', icon: 'diversity_3', roles: rolePermissions.manageCommitteesRoles },
      { label: 'Approvals', route: 'approvals', icon: 'inbox', roles: rolePermissions.manageCommitteesRoles },
      { label: 'Audit Log', route: 'audit-log', icon: 'receipt_long', roles: rolePermissions.manageAuditLogRoles },
    ],
  },
];

/**
 * The rail's visibility rule, shared with the help registry: role match, plus the Resident
 * requirement. This is the single place that answers "may these roles see this tool".
 */
export function isManageItemVisibleForRoles(
  item: { roles: string[]; requireResident?: boolean },
  userRoles: string[],
): boolean {
  if (item.requireResident && !userRoles.includes('Resident')) return false;
  return userRoles.some(role => item.roles.includes(role));
}
