import { Inject, Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';
import { NavigationEnd, Router } from '@angular/router';
import { BehaviorSubject, Observable, of } from 'rxjs';
import { catchError, distinctUntilChanged, filter, map, shareReplay, startWith } from 'rxjs/operators';
import { applicationState, ApplicationState } from '../state';
import { HelpTopic, helpTopics, topicForUrl, userCanSeeTopic } from '../help/help-topics';
import { renderMarkdownToHtml } from '../utils/markdown';

/**
 * State and content for the contextual help drawer: which topic matches the current route, which
 * topics the signed-in user may see in the index, and the rendered markdown for each topic.
 */
@Injectable({ providedIn: 'root' })
export class HelpService {
  private readonly openSubject = new BehaviorSubject<boolean>(false);
  private readonly currentTopicSubject = new BehaviorSubject<HelpTopic | null>(null);
  /** Rendered-doc cache; an entry that errored evicts itself so a later open retries the fetch. */
  private readonly docCache = new Map<string, Observable<SafeHtml | null>>();

  readonly open$: Observable<boolean> = this.openSubject.asObservable();
  readonly currentTopic$: Observable<HelpTopic | null> = this.currentTopicSubject.asObservable();
  /** Topics the signed-in user may see, in registry order. Empty when signed out. */
  readonly visibleTopics$: Observable<HelpTopic[]>;

  constructor(
    private readonly http: HttpClient,
    private readonly sanitizer: DomSanitizer,
    router: Router,
    @Inject(applicationState) appState: Observable<ApplicationState>,
  ) {
    // startWith seeds from the current URL so the contextual topic is correct even if this service
    // is first instantiated after the navigation that would have reported it; NavigationEnd keeps
    // it fresh. One pipeline, so the url-to-topic mapping has a single call site.
    router.events
      .pipe(
        filter((e): e is NavigationEnd => e instanceof NavigationEnd),
        map(e => e.urlAfterRedirects),
        startWith(router.url),
      )
      .subscribe(url => {
        this.currentTopicSubject.next(topicForUrl(helpTopics, url));
      });

    this.visibleTopics$ = appState.pipe(
      map(s => s.apiUser?.roles ?? []),
      distinctUntilChanged((a, b) => a.length === b.length && a.every((r, i) => r === b[i])),
      map(roles => helpTopics.filter(t => userCanSeeTopic(t, roles))),
      shareReplay({ bufferSize: 1, refCount: true }),
    );
  }

  open(): void {
    this.openSubject.next(true);
  }

  close(): void {
    this.openSubject.next(false);
  }

  /** The contextual topic right now (snapshot of currentTopic$). */
  get currentTopic(): HelpTopic | null {
    return this.currentTopicSubject.value;
  }

  /** Rendered HTML for a topic's markdown doc; null when the doc failed to load. */
  getTopicHtml(topicId: string): Observable<SafeHtml | null> {
    const cached = this.docCache.get(topicId);
    if (cached) {
      return cached;
    }
    const doc$ = this.http.get(`assets/help/${topicId}.md`, { responseType: 'text' }).pipe(
      map(markdown => renderMarkdownToHtml(markdown, this.sanitizer)),
      catchError(() => {
        this.docCache.delete(topicId);
        return of(null);
      }),
      shareReplay({ bufferSize: 1, refCount: false }),
    );
    this.docCache.set(topicId, doc$);
    return doc$;
  }
}
