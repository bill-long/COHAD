import { Component, ElementRef, OnDestroy, ViewChild } from '@angular/core';
import { BlockScrollStrategy, ScrollStrategyOptions } from '@angular/cdk/overlay';
import { SafeHtml } from '@angular/platform-browser';
import { Router } from '@angular/router';
import { EMPTY, Observable, Subject, Subscription } from 'rxjs';
import { map, switchMap } from 'rxjs/operators';
import { HelpService } from 'src/app/services/help.service';
import { HelpTopic, helpTopics } from 'src/app/help/help-topics';

interface HelpSection {
  name: string;
  topics: HelpTopic[];
}

/**
 * Slide-in help panel. Opens to the topic matching the current route (falling back to the index),
 * and always offers the index of every topic the signed-in user may see. Rendered as a
 * fixed-position overlay rather than a MatSidenav so the app's root layout stays untouched.
 */
@Component({
  selector: 'app-help-drawer',
  templateUrl: './help-drawer.component.html',
  styleUrls: ['./help-drawer.component.css'],
  standalone: false,
})
export class HelpDrawerComponent implements OnDestroy {
  @ViewChild('drawerTitle') private drawerTitle?: ElementRef<HTMLElement>;

  readonly open$: Observable<boolean>;
  readonly sections$: Observable<HelpSection[]>;

  view: 'topic' | 'index' = 'index';
  activeTopic: HelpTopic | null = null;
  topicHtml: SafeHtml | null = null;
  topicLoadFailed = false;

  private readonly subscriptions = new Subscription();
  /**
   * Doc requests; null drops the current subscription so a late response cannot mutate drawer
   * state. The underlying HTTP request deliberately survives (HelpService caches via shareReplay
   * with refCount: false), so an abandoned load still warms the doc cache for the next open.
   */
  private readonly topicRequests = new Subject<HelpTopic | null>();
  /**
   * CDK's scroll blocker - the same mechanism Material dialogs use - instead of hand-managed
   * body inline styles. It composes with concurrent overlay locks by construction: enable() no-ops
   * when another lock already holds the page, and disable() only releases this instance's own lock,
   * so the drawer can neither freeze the page permanently nor release someone else's block.
   */
  private readonly scrollBlock: BlockScrollStrategy;

  constructor(
    private readonly helpService: HelpService,
    private readonly router: Router,
    scrollStrategyOptions: ScrollStrategyOptions,
  ) {
    this.scrollBlock = scrollStrategyOptions.block();
    this.open$ = helpService.open$;
    this.sections$ = helpService.visibleTopics$.pipe(map(groupBySection));
    this.subscriptions.add(
      this.topicRequests
        .pipe(switchMap(topic => (topic === null ? EMPTY : this.helpService.getTopicHtml(topic.id))))
        .subscribe(html => {
          this.topicHtml = html;
          this.topicLoadFailed = html === null;
        }),
    );
    // Each open starts from the current screen's topic; the previous session's view is stale context.
    // While open, the drawer is modal (backdrop + focus trap), so the page behind it must not
    // scroll out from under the contextual topic.
    this.subscriptions.add(
      helpService.open$.subscribe(open => {
        if (open) {
          this.scrollBlock.enable();
          const contextual = helpService.currentTopic;
          if (contextual) {
            this.showTopic(contextual, false);
          } else {
            this.showIndex(false);
          }
        } else {
          this.scrollBlock.disable();
          // Drop any in-flight doc load so a late response cannot mutate state behind a closed
          // drawer; reopening always issues a fresh request anyway.
          this.topicRequests.next(null);
        }
      }),
    );
  }

  ngOnDestroy(): void {
    this.subscriptions.unsubscribe();
    this.scrollBlock.disable();
  }

  close(): void {
    this.helpService.close();
  }

  showIndex(moveFocus = true): void {
    this.view = 'index';
    this.activeTopic = null;
    this.topicHtml = null;
    this.topicLoadFailed = false;
    this.topicRequests.next(null);
    if (moveFocus) {
      this.focusTitle();
    }
  }

  /**
   * Swapping between the index and a topic replaces the whole drawer body,
   * destroying whatever the user just activated - focus would otherwise fall to
   * the document. Moving it to the drawer's heading keeps focus inside the trap
   * and makes a screen reader announce what is now on screen.
   *
   * The open$ subscription passes moveFocus: false when it sets up the initial
   * view, because cdkTrapFocusAutoCapture owns focus at that point.
   */
  private focusTitle(): void {
    if (!this.drawerTitle) {
      return;
    }
    // setTimeout, not queueMicrotask: zone.js patches queueMicrotask into a Zone
    // microtask, and microtasks drain *before* onMicrotaskEmpty triggers
    // ApplicationRef.tick(). Focusing there would land on the heading while it
    // still shows the previous view's title, so a screen reader would announce
    // the view the user just left - the exact thing this focus move prevents.
    // A macrotask runs after that tick, with the new title rendered.
    setTimeout(() => this.drawerTitle?.nativeElement.focus());
  }

  showTopic(topic: HelpTopic, moveFocus = true): void {
    this.view = 'topic';
    this.activeTopic = topic;
    this.topicHtml = null;
    this.topicLoadFailed = false;
    this.topicRequests.next(topic);
    if (moveFocus) {
      this.focusTitle();
    }
  }

  /**
   * Doc content is sanitized innerHTML, so its anchors bypass the Angular router. Intercept the two
   * internal link shapes docs may use: `#topic:{id}` cross-references another help topic in place,
   * and an app-absolute path (e.g. `/manage/suppressions`) navigates and closes the drawer.
   * Everything else (external links) keeps default browser behavior, as do modified clicks
   * (ctrl/cmd/shift/middle-click) so "open in new tab" still works.
   */
  onContentClick(event: MouseEvent): void {
    if (event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) {
      return;
    }
    // event.target can be a Text node in some browsers; walk up to its element so a click on an
    // anchor's text is still intercepted rather than falling through to a full page load.
    const target = event.target instanceof Element ? event.target : (event.target as Node | null)?.parentElement;
    const anchor = target?.closest('a') ?? null;
    if (!anchor) {
      return;
    }
    const href = anchor.getAttribute('href') ?? '';
    if (href.startsWith('#topic:')) {
      event.preventDefault();
      const topicId = href.substring('#topic:'.length);
      // Resolved against the full registry, not the user's index: a doc we already showed the user
      // may cross-reference a topic outside their index (e.g. Send Email links the Administrator-only
      // Suppressions doc), and following the reference is reading documentation, not gaining access.
      // The help-topics spec locks every #topic: reference in the shipped docs to a registry id.
      const topic = helpTopics.find(t => t.id === topicId);
      if (topic) {
        this.showTopic(topic);
      }
      return;
    }
    if (href.startsWith('/')) {
      event.preventDefault();
      this.close();
      this.router.navigateByUrl(href);
    }
  }
}

function groupBySection(topics: HelpTopic[]): HelpSection[] {
  const sections: HelpSection[] = [];
  for (const topic of topics) {
    let section = sections.find(s => s.name === topic.section);
    if (!section) {
      section = { name: topic.section, topics: [] };
      sections.push(section);
    }
    section.topics.push(topic);
  }
  return sections;
}
