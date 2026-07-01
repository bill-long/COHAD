import { BreakpointObserver } from '@angular/cdk/layout';
import { Component, DestroyRef, Inject, OnInit } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router } from '@angular/router';
import { Observable } from 'rxjs';
import { filter, map, shareReplay, take } from 'rxjs/operators';
import { ApiUser } from 'src/app/models';
import { NotificationsService } from 'src/app/services/notifications.service';
import { rolePermissions } from 'src/app/services/rolepermission.service';
import { ApplicationState, applicationState } from 'src/app/state';

/** One tool in the Manage rail. Visibility mirrors the old per-tab getters exactly. */
interface ManageNavItem {
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
interface ManageNavGroup {
  label: string;
  items: ManageNavItem[];
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

  /** Live count of held committee emails awaiting a decision — the Approvals inbox feed. */
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

    this.isHandset$ = this.breakpointObserver.observe('(max-width: 899px)').pipe(
      map(result => result.matches),
      shareReplay({ bufferSize: 1, refCount: true }),
    );

    this.groups = [
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
          {
            label: 'Approvals',
            route: 'approvals',
            icon: 'inbox',
            roles: rolePermissions.manageCommitteesRoles,
            badgeCount$: this.approvalsCount$,
          },
          { label: 'Audit Log', route: 'audit-log', icon: 'receipt_long', roles: rolePermissions.manageAuditLogRoles },
        ],
      },
    ];

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
    // Landing on /manage with no tool selected used to show a blank pane. Open the first tool the
    // current user can actually access, so the choice is role-accurate for admins and committees alike.
    const path = this.router.url.split('?')[0].split('#')[0].replace(/\/+$/, '');
    if (path === '/manage') {
      this.visibleGroups$.pipe(
        filter(groups => groups.length > 0),
        take(1),
        // The profile may load after the user has already left /manage; tear the subscription
        // down with the component so a never-firing take(1) can't leak past its usefulness.
        takeUntilDestroyed(this.destroyRef),
      ).subscribe(groups => {
        const first = groups[0].items[0];
        this.router.navigate([first.route], { relativeTo: this.route, replaceUrl: true });
      });
    }
  }

  /** Reproduces the previous per-tab visibility logic: role match, plus the Resident requirement. */
  private isItemVisible(item: ManageNavItem, user: ApiUser | null): boolean {
    if (user === null) return false;
    if (item.requireResident && !user.roles.includes('Resident')) return false;
    return user.roles.some(role => item.roles.includes(role));
  }
}
