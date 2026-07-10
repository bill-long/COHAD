import { Component, ElementRef, Inject, OnDestroy, OnInit, SecurityContext, ViewChild } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import { Location } from '@angular/common';
import { DomSanitizer, SafeHtml, Title } from '@angular/platform-browser';
import { Observable, Observer } from 'rxjs';
import { map } from 'rxjs/operators';
import { marked, Renderer } from 'marked';
import { ApiUser } from 'src/app/models';
import { BlogComment, BlogPostDetail, BlogService } from 'src/app/services/blog.service';
import { Login, Action, dispatcher, applicationState, ApplicationState } from 'src/app/state';
import { renderMarkdownToHtml } from 'src/app/utils/markdown';

@Component({
  selector: 'app-blog-detail',
  templateUrl: './blog-detail.component.html',
  styleUrls: ['./blog-detail.component.css'],
  standalone: false,
})
export class BlogDetailComponent implements OnInit, OnDestroy {
  post: BlogPostDetail | null = null;
  renderedHtml: SafeHtml = '';
  loading = false;
  error = '';

  comments: BlogComment[] = [];
  commentsLoading = false;
  commentContent = '';
  commentSubmitting = false;
  commentError = '';

  @ViewChild('commentsSection') commentsSection: ElementRef | undefined;

  private currentSlug = '';
  private postLoaded = false;
  private commentsLoaded = false;
  private commentHtmlCache = new Map<string, SafeHtml>();
  private destroyed = false;

  constructor(
    private readonly route: ActivatedRoute,
    private readonly location: Location,
    private readonly blogService: BlogService,
    private readonly sanitizer: DomSanitizer,
    private readonly titleService: Title,
    @Inject(applicationState) private appState: Observable<ApplicationState>,
    @Inject(dispatcher) private dispatcher: Observer<Action>,
  ) {}

  get apiUser$(): Observable<ApiUser | null> {
    return this.appState.pipe(map(s => s.apiUser));
  }

  get isSignedIn$(): Observable<boolean> {
    return this.apiUser$.pipe(map(u => u != null));
  }

  ngOnInit(): void {
    this.route.paramMap.subscribe(params => {
      const slug = params.get('slug');
      if (slug == null) {
        this.error = 'Post not found.';
        return;
      }
      this.currentSlug = slug;
      this.loadPost(slug);
    });
  }

  ngOnDestroy(): void {
    // The component's subscriptions are not torn down; this flag lets late callbacks skip mutating
    // the now-shared browser tab title after the user has navigated away.
    this.destroyed = true;
  }

  logIn(): void {
    const redirectTo = this.currentSlug ? `/news/${this.currentSlug}#comments` : '/news';
    this.dispatcher.next(new Login(redirectTo));
  }

  submitComment(): void {
    const content = this.commentContent.trim();
    if (!content || this.commentSubmitting) {
      return;
    }

    this.commentSubmitting = true;
    this.commentError = '';

    this.blogService.addComment(this.currentSlug, content).subscribe({
      next: comment => {
        this.comments.push(comment);
        this.commentContent = '';
        this.commentSubmitting = false;
      },
      error: () => {
        this.commentSubmitting = false;
        this.commentError = 'Failed to post comment.';
      },
    });
  }

  deleteComment(commentId: string): void {
    this.blogService.deleteComment(this.currentSlug, commentId).subscribe({
      next: () => {
        this.comments = this.comments.filter(c => c.id !== commentId);
        this.commentHtmlCache.delete(commentId);
      },
    });
  }

  renderCommentHtml(comment: BlogComment): SafeHtml {
    const cached = this.commentHtmlCache.get(comment.id);
    if (cached) {
      return cached;
    }

    const renderer = new Renderer();
    renderer.heading = ({ text }) => `<p><strong>${text}</strong></p>`;
    renderer.image = () => '';
    renderer.code = ({ text }) => `<p>${text}</p>`;
    renderer.codespan = ({ text }) => text;
    renderer.hr = () => '';
    renderer.table = () => '';
    renderer.tablerow = () => '';
    renderer.tablecell = () => '';
    renderer.html = () => '';

    const rawHtml = marked.parse(comment.content ?? '', { async: false, renderer }) as string;
    const sanitized = this.sanitizer.sanitize(SecurityContext.HTML, rawHtml) ?? '';
    const result = this.sanitizer.bypassSecurityTrustHtml(sanitized);
    this.commentHtmlCache.set(comment.id, result);
    return result;
  }

  private loadPost(slug: string): void {
    this.loading = true;
    this.error = '';
    this.postLoaded = false;
    this.commentsLoaded = false;

    this.blogService.getBySlug(slug).subscribe({
      next: post => {
        this.post = post;
        // Set the browser tab title to the post title - the route TitleStrategy only sets the generic
        // "COHAD | News". This drives the tab label and the display name Edge uses when the link is
        // copied. (Link-preview crawlers are served separately, server-side, by BlogDeepLinkOpenGraph.)
        // Guard against a late response arriving after the user navigated away, which would otherwise
        // clobber the new page's title - the component's subscriptions are not torn down.
        if (!this.destroyed) {
          this.titleService.setTitle(post.title ? `COHAD | ${post.title}` : 'COHAD | News');
        }
        this.renderedHtml = this.renderMarkdown(post.content ?? '');
        this.loading = false;
        this.postLoaded = true;
        if (post.publicSlug && post.publicSlug !== slug) {
          this.currentSlug = post.publicSlug;
          const snapshot = this.route.snapshot;
          const params = new URLSearchParams();
          snapshot.queryParamMap.keys.forEach(k => (snapshot.queryParamMap.getAll(k) ?? []).forEach(v => params.append(k, v ?? '')));
          const query = params.toString();
          const fragment = snapshot.fragment;
          let newUrl = '/news/' + post.publicSlug;
          if (query) {
            newUrl += '?' + query;
          }
          if (fragment) {
            newUrl += '#' + fragment;
          }
          this.location.replaceState(newUrl);
        }
        this.loadComments(this.currentSlug);
      },
      error: () => {
        this.post = null;
        this.renderedHtml = '';
        this.loading = false;
        this.error = 'Failed to load post.';
        if (!this.destroyed) {
          this.titleService.setTitle('COHAD | News');
        }
      },
    });
  }

  private loadComments(slug: string): void {
    this.commentsLoading = true;
    this.commentHtmlCache.clear();

    this.blogService.getComments(slug).subscribe({
      next: comments => {
        this.comments = comments;
        this.commentsLoading = false;
        this.commentsLoaded = true;
        this.scrollToCommentsIfNeeded();
      },
      error: () => {
        this.comments = [];
        this.commentsLoading = false;
        this.commentsLoaded = true;
      },
    });
  }

  private scrollToCommentsIfNeeded(): void {
    if (this.postLoaded && this.commentsLoaded && this.route.snapshot.fragment === 'comments') {
      setTimeout(() => {
        this.commentsSection?.nativeElement?.scrollIntoView({ behavior: 'smooth' });
      });
    }
  }

  private renderMarkdown(markdown: string): SafeHtml {
    return renderMarkdownToHtml(markdown, this.sanitizer, { dropCap: true });
  }
}
