import { BreakpointObserver } from '@angular/cdk/layout';
import { Component, DestroyRef, Inject, OnInit } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, NavigationEnd, Router } from '@angular/router';
import { Observable } from 'rxjs';
import { filter, map, shareReplay, startWith, switchMap, take } from 'rxjs/operators';
import { ApiUser } from 'src/app/models';
import { NotificationsService } from 'src/app/services/notifications.service';
import { isManageItemVisibleForRoles, ManageNavGroup, ManageNavItem, manageNavGroups } from './manage-nav';
import { ApplicationState, applicationState } from 'src/app/state';
import { observeCompactLayout } from 'src/app/utils/compact-layout';

// The rail definition and visibility rule live in manage-nav.ts (component-free) so the help
// registry can import them without dragging this component along.

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
