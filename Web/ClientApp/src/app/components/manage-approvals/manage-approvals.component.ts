import { Component, OnDestroy, OnInit } from '@angular/core';
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

  /** Id of the message currently being approved/rejected (disables its actions). */
  actioningId: string | null = null;

  /** Id of the message a deep link pointed at, highlighted so the moderator spots it immediately. */
  highlightedId: string | null = null;

  /** Lazily-loaded body preview state per held message id. */
  bodies = new Map<string, HeldBodyState>();

  /** Message id requested via ?message= — opened and scrolled to once the list loads. */
  private deepLinkMessageId: string | null = null;
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
    });
    this.load();
  }

  ngOnDestroy(): void {
    this.routeSub?.unsubscribe();
  }

  load(): void {
    this.loading = true;
    this.error = '';
    this.committeeService.getPendingHeldMessages().subscribe({
      next: messages => {
        this.pending = messages ?? [];
        this.loading = false;
        this.applyDeepLink();
      },
      error: () => {
        this.pending = [];
        this.loading = false;
        this.error = 'Failed to load pending approvals.';
      },
    });
  }

  /** Opens and scrolls to the deep-linked message, or notes that it has already been handled. */
  private applyDeepLink(): void {
    const id = this.deepLinkMessageId;
    if (!id) return;
    // Only act on the deep link once — clear it so a later background refresh doesn't re-scroll.
    this.deepLinkMessageId = null;

    const target = this.pending.find(m => m.id === id);
    if (!target) {
      this.snackBar.open('That email has already been handled.', 'Dismiss', { duration: 6000 });
      return;
    }

    this.highlightedId = id;
    this.toggleBody(target);
    // Defer until the row has rendered, then bring it into view.
    setTimeout(() => document.getElementById('approval-' + id)?.scrollIntoView({ behavior: 'smooth', block: 'center' }));
  }

  getBody(messageId: string): HeldBodyState | undefined {
    return this.bodies.get(messageId);
  }

  /** Expand/collapse the message body preview, lazily fetching it on first open. */
  toggleBody(message: PendingHeldMessage): void {
    const existing = this.bodies.get(message.id);
    if (existing) {
      existing.expanded = !existing.expanded;
      if (existing.expanded && !existing.loaded && !existing.loading) {
        this.fetchBody(message);
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
    this.actioningId = message.id;
    const sender = this.senderLabel(message);
    this.committeeService.approveHeldMessage(message.committeeId, message.id).subscribe({
      next: () => {
        this.actioningId = null;
        this.removeFromQueue(message.id);
        this.snackBar.open(`Approved — message from ${sender} will be forwarded.`, 'Dismiss', { duration: 5000 });
        this.refresh();
      },
      error: () => {
        this.actioningId = null;
        this.snackBar.open(`Failed to approve the message from ${sender}.`, 'Dismiss', { duration: 6000 });
        // Reconcile: if it failed because another moderator already handled it, the refresh drops the row.
        this.refresh();
      },
    });
  }

  reject(message: PendingHeldMessage): void {
    this.actioningId = message.id;
    const sender = this.senderLabel(message);
    this.committeeService.rejectHeldMessage(message.committeeId, message.id).subscribe({
      next: () => {
        this.actioningId = null;
        this.removeFromQueue(message.id);
        this.snackBar.open(`Rejected — message from ${sender} was discarded.`, 'Dismiss', { duration: 5000 });
        this.refresh();
      },
      error: () => {
        this.actioningId = null;
        this.snackBar.open(`Failed to reject the message from ${sender}.`, 'Dismiss', { duration: 6000 });
        this.refresh();
      },
    });
  }

  private senderLabel(message: PendingHeldMessage): string {
    return message.senderName || message.senderEmail || 'unknown sender';
  }

  private removeFromQueue(messageId: string): void {
    this.pending = this.pending.filter(m => m.id !== messageId);
    this.bodies.delete(messageId);
    if (this.highlightedId === messageId) {
      this.highlightedId = null;
    }
  }

  /**
   * Silently re-syncs the queue with the server after an action so rows another moderator handled (or
   * that auto-released) don't linger. Unlike load(), it leaves the spinner alone and never re-triggers
   * the deep-link scroll.
   */
  private refresh(): void {
    this.committeeService.getPendingHeldMessages().subscribe({
      next: messages => {
        this.pending = messages ?? [];
      },
      error: () => {
        // Keep the current list; the next action or a manual reload reconciles.
      },
    });
  }
}
