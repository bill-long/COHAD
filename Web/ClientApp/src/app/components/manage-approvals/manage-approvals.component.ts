import { Component, OnDestroy, OnInit } from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { ActivatedRoute } from '@angular/router';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';
import { MatSnackBar } from '@angular/material/snack-bar';
import { Subscription } from 'rxjs';
import { CommitteeService, HeldMessageBody, PendingHeldMessage } from 'src/app/services/committee.service';

interface HeldBodyState {
  expanded: boolean;
  loading: boolean;
  loaded: boolean;
  error: string | null;
  data: HeldMessageBody | null;
  /** Sanitized srcdoc for the sandboxed iframe (HTML bodies only). */
  safeBody: SafeHtml | null;
  /** Whether remote content (images/styles) is currently blocked in the preview. */
  imagesBlocked: boolean;
  /** Whether the HTML body references remote images/content worth offering to load. */
  hasRemoteContent: boolean;
}

/**
 * The Approvals inbox: a flat, cross-committee queue of held committee emails awaiting a moderator's
 * decision, newest first. Replaces the old in-context moderation buried inside each committee panel on
 * Manage → Committees — a notification (or the Approvals tab) now lands the moderator directly on the
 * email to view, approve, or reject. Sourced from the held-message store, so the queue is authoritative
 * even for a message whose in-app notification was never raised.
 */
@Component({
  selector: 'app-manage-approvals',
  templateUrl: './manage-approvals.component.html',
  styleUrls: ['./manage-approvals.component.css'],
  standalone: false,
})
export class ManageApprovalsComponent implements OnInit, OnDestroy {
  pending: PendingHeldMessage[] = [];
  loading = false;
  error = '';

  /** Ids of messages with an Approve/Reject request in flight — guards each row independently so one
   *  action completing can't re-enable another still-pending row's buttons (double-submit). */
  private actioning = new Set<string>();

  /** Id of the message a deep link pointed at, highlighted so the moderator spots it immediately. */
  highlightedId: string | null = null;

  /** True when a ?message= deep link pointed at a message that isn't in the pending queue — it was
   *  already approved/rejected (often by another moderator). Drives a dismissible info notice so the
   *  moderator gets explicit feedback instead of silently landing on an inbox without "their" email. */
  staleDeepLinkNotice = false;

  /** Lazily-loaded body preview state per held message id. */
  bodies = new Map<string, HeldBodyState>();

  /** Message id requested via ?message= — highlighted/opened/scrolled-to once it appears in the queue. */
  private deepLinkMessageId: string | null = null;
  /** True once the first load has completed, so a later ?message= change can re-target the queue. */
  private loaded = false;
  /** Monotonic token so an out-of-order list fetch (overlapping refreshes) can't apply stale data. */
  private loadGeneration = 0;
  private routeSub: Subscription | null = null;

  constructor(
    private readonly committeeService: CommitteeService,
    private readonly sanitizer: DomSanitizer,
    private readonly route: ActivatedRoute,
    private readonly snackBar: MatSnackBar,
  ) {}

  ngOnInit(): void {
    this.routeSub = this.route.queryParamMap.subscribe(params => {
      this.deepLinkMessageId = params.get('message');
      // The first emission is handled by the initial load() below (data isn't here yet). A later one
      // means the moderator clicked another held-message notification while already on this page
      // (Angular reuses the component for query-param-only navigations). Clear any prior highlight,
      // then target the new message: highlight it directly if it's already in the queue, otherwise
      // do a (non-destructive) re-sync in case it just arrived.
      if (!this.loaded) return;
      this.highlightedId = null;
      this.staleDeepLinkNotice = false; // start the new navigation clean; resolveDeepLink re-decides
      if (this.deepLinkMessageId && this.pending.some(m => m.id === this.deepLinkMessageId)) {
        this.resolveDeepLink();
      } else if (this.deepLinkMessageId) {
        this.refresh();
      }
    });
    this.load();
  }

  ngOnDestroy(): void {
    this.routeSub?.unsubscribe();
  }

  isActioning(messageId: string): boolean {
    return this.actioning.has(messageId);
  }

  load(): void {
    this.loading = true;
    this.error = '';
    const generation = ++this.loadGeneration;
    this.committeeService.getPendingHeldMessages().subscribe({
      next: messages => {
        if (generation !== this.loadGeneration) return;
        this.loading = false;
        this.loaded = true;
        this.reconcilePending(messages ?? []); // also resolves any pending deep link
      },
      error: () => {
        if (generation !== this.loadGeneration) return;
        this.pending = [];
        this.loading = false;
        this.loaded = true;
        this.error = 'Failed to load pending approvals.';
      },
    });
  }

  /**
   * If the deep-link target is in the current queue, highlight, open, and scroll to it (one-shot). Runs
   * after every successful list fetch (via reconcilePending) and on a same-page re-target. If the target
   * isn't present — already handled by this or another moderator — it raises a dismissible info notice so
   * the moderator gets explicit feedback; they still land on the inbox showing what's actually pending.
   */
  private resolveDeepLink(): void {
    const id = this.deepLinkMessageId;
    if (!id) return;
    this.deepLinkMessageId = null; // one-shot, whether or not the target was found

    const target = this.pending.find(m => m.id === id);
    if (!target) {
      this.staleDeepLinkNotice = true;
      return;
    }

    this.staleDeepLinkNotice = false;
    this.highlightedId = id;
    this.ensureBodyExpanded(target);
    // Defer until the row has rendered, then bring it into view.
    setTimeout(() => document.getElementById('approval-' + id)?.scrollIntoView({ behavior: 'smooth', block: 'center' }));
  }

  /** Dismisses the stale-deep-link info notice (user clicked its close button). */
  dismissStaleDeepLinkNotice(): void {
    this.staleDeepLinkNotice = false;
  }

  getBody(messageId: string): HeldBodyState | undefined {
    return this.bodies.get(messageId);
  }

  /** Expand/collapse the message body preview (user toggle), lazily fetching it on first open. */
  toggleBody(message: PendingHeldMessage): void {
    const existing = this.bodies.get(message.id);
    if (existing) {
      existing.expanded = !existing.expanded;
      if (existing.expanded && !existing.loaded && !existing.loading) {
        this.fetchBody(message);
      }
      return;
    }
    this.ensureBodyExpanded(message);
  }

  /** Opens the body preview if it isn't already open (never collapses) — used by the deep-link path. */
  private ensureBodyExpanded(message: PendingHeldMessage): void {
    const existing = this.bodies.get(message.id);
    if (existing) {
      if (!existing.expanded) {
        existing.expanded = true;
        if (!existing.loaded && !existing.loading) {
          this.fetchBody(message);
        }
      }
      return;
    }

    const state: HeldBodyState = {
      expanded: true,
      loading: false,
      loaded: false,
      error: null,
      data: null,
      safeBody: null,
      imagesBlocked: true,
      hasRemoteContent: false,
    };
    this.bodies.set(message.id, state);
    this.fetchBody(message);
  }

  private fetchBody(message: PendingHeldMessage): void {
    const state = this.bodies.get(message.id);
    if (!state) return;
    state.loading = true;
    state.error = null;
    this.committeeService.getHeldMessageBody(message.committeeId, message.id).subscribe({
      next: body => {
        state.data = body;
        if (body.available && body.isHtml && body.body != null) {
          state.imagesBlocked = true;
          state.hasRemoteContent = this.hasRemoteContent(body.body);
          state.safeBody = this.buildBodyDoc(body.body, state.imagesBlocked);
        } else {
          state.safeBody = null;
        }
        state.loaded = true;
        state.loading = false;
      },
      error: () => {
        state.error = 'Failed to load the message body.';
        state.loading = false;
      },
    });
  }

  /** Re-render the HTML preview with remote images/content allowed. */
  displayImages(messageId: string): void {
    const state = this.bodies.get(messageId);
    if (!state || !state.data?.isHtml || state.data.body == null) return;
    state.imagesBlocked = false;
    state.safeBody = this.buildBodyDoc(state.data.body, false);
  }

  /**
   * Heuristic: does the body reference remote images or CSS resources worth offering to load?
   * Each pattern uses a single `[\s"']*` class (rather than adjacent `\s*["']?\s*` runs) so it
   * stays linear-time on hostile bodies — overlapping whitespace quantifiers would allow
   * catastrophic backtracking (ReDoS) that could freeze the tab.
   */
  private hasRemoteContent(html: string): boolean {
    return (
      // src / srcset / background attributes pointing at an absolute or protocol-relative URL
      /(?:src|srcset|background)\s*=[\s"']*(?:https?:)?\/\//i.test(html) ||
      // CSS url(...) in inline styles or <style> blocks
      /url\([\s"']*(?:https?:)?\/\//i.test(html) ||
      // @import "https://..." (with or without url())
      /@import[\s"']+(?:url\([\s"']*)?(?:https?:)?\/\//i.test(html) ||
      // <link rel="stylesheet" href="https://...">
      /<link\b[^>]*\bhref\s*=[\s"']*(?:https?:)?\/\//i.test(html)
    );
  }

  /**
   * Wraps the still-untrusted email HTML in a document with a Content-Security-Policy that
   * blocks (or, once the admin opts in, allows) remote content. This does NOT sanitize the
   * body — the security boundary is the sandboxed iframe (no scripts, no navigation, opaque
   * origin) plus this CSP. The wrapper's CSP applies to the whole document, including any
   * nested html/head the email may carry, and blocks base-uri/form hijacking on top.
   *
   * `<meta http-equiv="refresh">` is removed first as defense-in-depth: CSP does not govern
   * document self-navigation, so an email could otherwise try to auto-navigate the frame to a
   * remote URL (a tracking beacon). (The sandbox already blocks meta-refresh, since it lacks
   * allow-scripts; this removal is belt-and-suspenders.)
   */
  private buildBodyDoc(html: string, imagesBlocked: boolean): SafeHtml {
    const withoutMetaRefresh = html.replace(/<meta\b[^>]*http-equiv\s*=\s*["']?\s*refresh\b[^>]*>/gi, '');
    // Remote sources are appended only when the admin opts in via "Display images".
    // img-src additionally allows cid: (embedded message parts) once unblocked.
    const remote = imagesBlocked ? '' : ' http: https:';
    const csp =
      "default-src 'none'; base-uri 'none'; form-action 'none'; " +
      `style-src 'unsafe-inline' data:${remote}; ` +
      `img-src data:${remote}${imagesBlocked ? '' : ' cid:'}; ` +
      `font-src data:${remote};`;
    const doc =
      `<!DOCTYPE html><html><head><meta charset="utf-8">` +
      `<meta http-equiv="Content-Security-Policy" content="${csp}">` +
      `<meta name="referrer" content="no-referrer">` +
      `</head><body>${withoutMetaRefresh}</body></html>`;
    // bypassSecurityTrustHtml is safe ONLY because this string is bound to the sandboxed
    // iframe's srcdoc; never bind it to a parent-page [innerHTML].
    return this.sanitizer.bypassSecurityTrustHtml(doc);
  }

  approve(message: PendingHeldMessage): void {
    this.actioning.add(message.id);
    const sender = this.senderLabel(message);
    this.committeeService.approveHeldMessage(message.committeeId, message.id).subscribe({
      next: () => {
        this.actioning.delete(message.id);
        this.removeFromQueue(message.id); // drop it immediately; the row can't linger if the re-sync fails
        this.snackBar.open(`Approved — message from ${sender} will be forwarded.`, 'Dismiss', {
          duration: 5000,
          politeness: 'polite',
        });
        this.refresh();
      },
      error: (err: unknown) => {
        this.actioning.delete(message.id);
        this.snackBar.open(this.actionErrorMessage(err, `Failed to approve the message from ${sender}.`), 'Dismiss', { duration: 6000 });
        this.refresh();
      },
    });
  }

  reject(message: PendingHeldMessage): void {
    this.actioning.add(message.id);
    const sender = this.senderLabel(message);
    this.committeeService.rejectHeldMessage(message.committeeId, message.id).subscribe({
      next: () => {
        this.actioning.delete(message.id);
        this.removeFromQueue(message.id);
        this.snackBar.open(`Rejected — message from ${sender} was discarded.`, 'Dismiss', {
          duration: 5000,
          politeness: 'polite',
        });
        this.refresh();
      },
      error: (err: unknown) => {
        this.actioning.delete(message.id);
        this.snackBar.open(this.actionErrorMessage(err, `Failed to reject the message from ${sender}.`), 'Dismiss', { duration: 6000 });
        this.refresh();
      },
    });
  }

  private senderLabel(message: PendingHeldMessage): string {
    return message.senderName || message.senderEmail || 'unknown sender';
  }

  /**
   * Prefers the backend's specific reason (both approve/reject guards return `{ error: string }` — e.g.
   * "Message is already Approved." on the 400 status guard, or "Message was already actioned by another
   * administrator." on a 409 ETag race) so the moderator can tell "already handled" from a transient
   * failure. Falls back to the generic message when there's no structured body (network error, etc.).
   */
  private actionErrorMessage(err: unknown, fallback: string): string {
    const body = err instanceof HttpErrorResponse ? err.error : null;
    if (body && typeof body === 'object' && typeof (body as { error?: unknown }).error === 'string') {
      const reason = (body as { error: string }).error.trim();
      if (reason) return reason;
    }
    return fallback;
  }

  /** Optimistically drops a just-actioned row and its cached state. Bumps the generation so a list
   *  fetch already in flight can't re-add it from a pre-action snapshot. */
  private removeFromQueue(messageId: string): void {
    this.loadGeneration++;
    this.pending = this.pending.filter(m => m.id !== messageId);
    this.bodies.delete(messageId);
    if (this.highlightedId === messageId) {
      this.highlightedId = null;
    }
  }

  /**
   * Re-syncs the queue from the server after every action so rows another moderator concurrently handled
   * drop too. No spinner and never destructive on error (a failed re-sync keeps the optimistic state).
   * The held queue is low-volume, so a fetch per action is a fair price for accurate state. Generation-
   * guarded so an out-of-order response (or one predating an optimistic removal) can't apply stale data.
   */
  private refresh(): void {
    const generation = ++this.loadGeneration;
    this.committeeService.getPendingHeldMessages().subscribe({
      next: messages => {
        if (generation !== this.loadGeneration) return;
        this.reconcilePending(messages ?? []);
      },
      error: () => {
        // Keep the current list; the next action or a manual reload reconciles.
      },
    });
  }

  /** Replaces the queue (clearing any stale error, pruning gone rows' state), then resolves a deep link. */
  private reconcilePending(next: PendingHeldMessage[]): void {
    this.error = ''; // a successful fetch clears a prior load-failure banner
    const liveIds = new Set(next.map(m => m.id));
    for (const id of Array.from(this.bodies.keys())) {
      if (!liveIds.has(id)) {
        this.bodies.delete(id);
      }
    }
    if (this.highlightedId && !liveIds.has(this.highlightedId)) {
      this.highlightedId = null;
    }
    this.pending = next;
    // A waiting deep link resolves against whichever fetch brings its target in (or confirms it gone).
    this.resolveDeepLink();
  }
}
