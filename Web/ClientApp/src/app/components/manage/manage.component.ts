import { BreakpointObserver } from '@angular/cdk/layout';
import { Component, DestroyRef, Inject, OnInit } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, NavigationEnd, Router } from '@angular/router';
import { Observable } from 'rxjs';
import { filter, map, shareReplay, startWith, switchMap, take } from 'rxjs/operators';
import { ApiUser } from 'src/app/models';
import { NotificationsService } from 'src/app/services/notifications.service';
import { rolePermissions } from 'src/app/services/rolepermission.service';
import { ApplicationState, applicationState } from 'src/app/state';
import { observeCompactLayout } from 'src/app/utils/compact-layout';

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
 * The Manage rail, as data. Exported (rather than built inline in the component) because the
 * contextual help registry (app/help/help-topics.ts) derives its topic titles, sections, and
 * visibility from these same entries - one definition, so a renamed label or a moved tool updates
 * the help index automatically. Per-instance state (the Approvals badge stream) is grafted on in
 * the component constructor.
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

/**
 * Manage shell: a grouped left nav rail (Directory / Communications / Governance) over a routed
 * content pane. The rail is a fixed side panel on desktop and a slide-over drawer on mobile.
 * Tab visibility is unchanged from the previous flat tab bar — the same role checks, now expressed
 * once as data instead of nine near-identical getters.
 */
@Component({
  selector: 'app-manage',
  templateUrl: './manage.component.html',
  styleUrls: ['./manage.component.css'],
  standalone: false,
})
export class ManageComponent implements OnInit {
  private readonly apiUser$: Observable<ApiUser | null>;

  /**
   * Live count of held committee emails that have passed the antispam quarantine window and been
   * notified; that is, those actively demanding a moderator decision. Sourced from the notification
   * feed, not the Approvals inbox feed: the badge is a push signal, and the quarantine window exists
   * precisely to avoid pulling moderators in before automatic antispam has acted, so a freshly-held
   * message appears in the Approvals inbox (which reads held status directly) before it counts here.
   */
  readonly approvalsCount$: Observable<number>;

  /** True below the desktop breakpoint, where the rail becomes an over-mode drawer. */
  readonly isHandset$: Observable<boolean>;

  readonly groups: ManageNavGroup[];

  /** The groups (with their visible items) the current user may see; empty groups are dropped. */
  readonly visibleGroups$: Observable<ManageNavGroup[]>;

  constructor(
    @Inject(applicationState) private readonly appState: Observable<ApplicationState>,
    private readonly notifications: NotificationsService,
    private readonly breakpointObserver: BreakpointObserver,
    private readonly router: Router,
    private readonly route: ActivatedRoute,
    private readonly destroyRef: DestroyRef,
  ) {
    this.apiUser$ = this.appState.pipe(map(s => s.apiUser));

    this.approvalsCount$ = this.notifications.notifications$.pipe(map(list => list.filter(n => n.type === 'HeldMessage').length));

    // Shared with the admin tables (utils/compact-layout) and the top navbar's desktop/mobile
    // threshold, so the rail flips to a drawer at the same width everything else switches.
    this.isHandset$ = observeCompactLayout(this.breakpointObserver);

    // The static rail definition plus this instance's live badge stream on Approvals.
    this.groups = manageNavGroups.map(group => ({
      ...group,
      items: group.items.map(item => (item.route === 'approvals' ? { ...item, badgeCount$: this.approvalsCount$ } : item)),
    }));

    this.visibleGroups$ = this.apiUser$.pipe(
      map(user =>
        this.groups
          .map(group => ({ ...group, items: group.items.filter(item => this.isItemVisible(item, user)) }))
          .filter(group => group.items.length > 0),
      ),
      shareReplay({ bufferSize: 1, refCount: true }),
    );
  }

  ngOnInit(): void {
    // Open the first tool the user can access when they land on bare /manage (which would otherwise
    // show a blank pane). Two arrival paths must both work:
    //   - Initial landing: the navigation that creates this component emits its NavigationEnd *before*
    //     ngOnInit runs (activation emits the event; ngOnInit runs on the next change-detection tick),
    //     so that event is already gone — startWith(null) re-checks the current URL synchronously now.
    //   - Re-entry from a child route: the router reuses this component (no new ngOnInit), so later
    //     NavigationEnds drive the check (e.g. the navbar 'Manage' link while on /manage/homes).
    // switchMap cancels a pending profile-wait when a newer navigation supersedes it, so duplicate
    // waits never pile up; the subscribe re-checks the URL so a late profile load can't yank a user
    // who has since moved to a child route. takeUntilDestroyed tears the whole thing down with the view.
    this.router.events
      .pipe(
        filter((event): event is NavigationEnd => event instanceof NavigationEnd),
        startWith(null),
        filter(() => this.isBareManageUrl()),
        switchMap(() => this.visibleGroups$.pipe(filter(groups => groups.length > 0), take(1))),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe(groups => {
        if (!this.isBareManageUrl()) return;
        const first = groups[0].items[0];
        this.router.navigate([first.route], { relativeTo: this.route, replaceUrl: true });
      });
  }

  /** True when the current URL is /manage with no child tool selected. */
  private isBareManageUrl(): boolean {
    const path = this.router.url.split('?')[0].split('#')[0].replace(/\/+$/, '');
    return path === '/manage';
  }

  /** Reproduces the previous per-tab visibility logic (see isManageItemVisibleForRoles). */
  private isItemVisible(item: ManageNavItem, user: ApiUser | null): boolean {
    return user !== null && isManageItemVisibleForRoles(item, user.roles);
  }
}
