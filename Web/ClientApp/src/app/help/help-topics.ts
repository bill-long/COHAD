import { isManageItemVisibleForRoles, manageNavGroups } from '../components/manage/manage-nav';
import { rolePermissions } from '../services/rolepermission.service';

/**
 * A help topic backed by a markdown document in `assets/help/{id}.md`.
 *
 * Topics for Manage rail tools are DERIVED from the rail definition (manageNavGroups), so titles,
 * sections, and visibility roles have exactly one source and cannot drift from the navigation the
 * admin is looking at. Only topics with no rail entry (the onboarding guide, the email-job detail
 * page) are declared by hand here.
 */
export interface HelpTopic {
  /** Also the markdown file name: assets/help/{id}.md (rail topics use the tool's route name). */
  id: string;
  title: string;
  /** Index grouping heading. */
  section: string;
  /** URL prefix that selects this topic contextually; longest match wins. */
  routePrefix: string;
  /** Roles that may see this topic in the index (any-of), same rule as the rail. */
  roles: string[];
  /** When true, the topic also requires the Resident role, mirroring the rail and RoleGuard. */
  requireResident?: boolean;
}

function buildHelpTopics(): HelpTopic[] {
  const topics: HelpTopic[] = [
    // '/manage' is the landing page, so the onboarding guide doubles as its contextual topic;
    // longest-prefix matching keeps the per-screen topics winning on their own routes.
    {
      id: 'new-administrator',
      title: 'New Administrator Guide',
      section: 'Getting started',
      routePrefix: '/manage',
      roles: rolePermissions.manageRoles,
    },
  ];
  for (const group of manageNavGroups) {
    for (const item of group.items) {
      topics.push({
        id: item.route,
        title: item.label,
        section: group.label,
        routePrefix: `/manage/${item.route}`,
        roles: item.roles,
        requireResident: item.requireResident,
      });
      // The email-job detail page is reached from the Email screen rather than the rail, so it has
      // no rail entry of its own; it inherits Email's section and audience and sits next to it in
      // the index, matching how it is reached.
      if (item.route === 'send-email') {
        topics.push({
          id: 'email-jobs',
          title: 'Email Job Details',
          section: group.label,
          routePrefix: '/manage/email-jobs',
          roles: item.roles,
          requireResident: item.requireResident,
        });
      }
    }
  }
  return topics;
}

export const helpTopics: HelpTopic[] = buildHelpTopics();

/** The contextual topic for a router URL (query string and fragment ignored), or null. */
export function topicForUrl(topics: HelpTopic[], url: string): HelpTopic | null {
  const path = url.split(/[?#]/, 1)[0];
  let best: HelpTopic | null = null;
  for (const topic of topics) {
    const prefix = topic.routePrefix;
    if (!path.startsWith(prefix)) {
      continue;
    }
    // Segment boundary: '/manage' must not match '/managefoo'.
    if (path.length > prefix.length && path[prefix.length] !== '/') {
      continue;
    }
    if (!best || prefix.length > best.routePrefix.length) {
      best = topic;
    }
  }
  return best;
}

/** True when the user's roles may see the topic - the rail's own visibility rule. */
export function userCanSeeTopic(topic: HelpTopic, userRoles: string[]): boolean {
  return isManageItemVisibleForRoles(topic, userRoles);
}
