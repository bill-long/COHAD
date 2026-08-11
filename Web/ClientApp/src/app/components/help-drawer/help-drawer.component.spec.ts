import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By, DomSanitizer } from '@angular/platform-browser';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';
import { A11yModule } from '@angular/cdk/a11y';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatListModule } from '@angular/material/list';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatTooltipModule } from '@angular/material/tooltip';
import { Router } from '@angular/router';
import { BehaviorSubject, of } from 'rxjs';

import { HelpDrawerComponent } from './help-drawer.component';
import { HelpService } from 'src/app/services/help.service';
import { HelpTopic } from 'src/app/help/help-topics';

const TOPICS: HelpTopic[] = [
  { id: 'new-administrator', title: 'New Administrator Guide', section: 'Getting started', routePrefix: '/manage', roles: ['Administrator'] },
  { id: 'users', title: 'Users', section: 'Directory', routePrefix: '/manage/users', roles: ['Administrator'] },
  { id: 'suppressions', title: 'Suppressions', section: 'Communications', routePrefix: '/manage/suppressions', roles: ['Administrator'] },
];

describe('HelpDrawerComponent', () => {
  let fixture: ComponentFixture<HelpDrawerComponent>;
  let open$: BehaviorSubject<boolean>;
  let helpService: {
    open$: BehaviorSubject<boolean>;
    visibleTopics$: BehaviorSubject<HelpTopic[]>;
    currentTopic: HelpTopic | null;
    getTopicHtml: jasmine.Spy;
    close: jasmine.Spy;
  };
  let router: jasmine.SpyObj<Router>;

  beforeEach(async () => {
    open$ = new BehaviorSubject<boolean>(false);
    helpService = {
      open$,
      visibleTopics$: new BehaviorSubject<HelpTopic[]>(TOPICS),
      currentTopic: null,
      getTopicHtml: jasmine.createSpy('getTopicHtml'),
      close: jasmine.createSpy('close').and.callFake(() => open$.next(false)),
    };
    router = jasmine.createSpyObj<Router>('Router', ['navigateByUrl']);

    await TestBed.configureTestingModule({
      declarations: [HelpDrawerComponent],
      imports: [
        NoopAnimationsModule,
        A11yModule,
        MatButtonModule,
        MatIconModule,
        MatListModule,
        MatProgressSpinnerModule,
        MatTooltipModule,
      ],
      providers: [
        { provide: HelpService, useValue: helpService },
        { provide: Router, useValue: router },
      ],
    }).compileComponents();

    const sanitizer = TestBed.inject(DomSanitizer);
    helpService.getTopicHtml.and.callFake((id: string) =>
      of(sanitizer.bypassSecurityTrustHtml(`<h1>doc:${id}</h1><p><a href="#topic:suppressions">See suppressions</a><a href="/manage/users">Open users</a></p>`)),
    );

    fixture = TestBed.createComponent(HelpDrawerComponent);
    fixture.detectChanges();
  });

  afterEach(() => {
    fixture.destroy();
  });

  function drawerText(): string {
    return (fixture.debugElement.query(By.css('.help-drawer'))?.nativeElement as HTMLElement | undefined)?.textContent ?? '';
  }

  it('renders nothing while closed', () => {
    expect(fixture.debugElement.query(By.css('.help-drawer'))).toBeNull();
  });

  it('opens to the index, grouped by section, when no contextual topic matches', () => {
    open$.next(true);
    fixture.detectChanges();

    expect(fixture.componentInstance.view).toBe('index');
    const headings = fixture.debugElement
      .queryAll(By.css('.help-section-heading'))
      .map(h => (h.nativeElement as HTMLElement).textContent!.trim());
    expect(headings).toEqual(['Getting started', 'Directory', 'Communications']);
    expect(drawerText()).toContain('New Administrator Guide');
  });

  it('opens directly to the contextual topic for the current screen', () => {
    helpService.currentTopic = TOPICS[1];
    open$.next(true);
    fixture.detectChanges();

    expect(fixture.componentInstance.view).toBe('topic');
    expect(helpService.getTopicHtml).toHaveBeenCalledWith('users');
    expect(drawerText()).toContain('doc:users');
  });

  it('navigates back to the index from a topic', () => {
    helpService.currentTopic = TOPICS[1];
    open$.next(true);
    fixture.detectChanges();

    const back = fixture.debugElement.query(By.css('button[aria-label="All help topics"]'));
    (back.nativeElement as HTMLElement).click();
    fixture.detectChanges();

    expect(fixture.componentInstance.view).toBe('index');
    expect(drawerText()).toContain('Suppressions');
  });

  it('shows a topic when its index entry is clicked', () => {
    open$.next(true);
    fixture.detectChanges();

    const items = fixture.debugElement.queryAll(By.css('.help-topic-list button'));
    const suppressions = items.find(i => (i.nativeElement as HTMLElement).textContent!.includes('Suppressions'))!;
    (suppressions.nativeElement as HTMLElement).click();
    fixture.detectChanges();

    expect(drawerText()).toContain('doc:suppressions');
  });

  it('intercepts #topic: links as in-drawer cross references', () => {
    helpService.currentTopic = TOPICS[1];
    open$.next(true);
    fixture.detectChanges();

    const link = fixture.debugElement.query(By.css('.help-content a[href="#topic:suppressions"]'));
    (link.nativeElement as HTMLElement).click();
    fixture.detectChanges();

    expect(drawerText()).toContain('doc:suppressions');
    expect(router.navigateByUrl).not.toHaveBeenCalled();
  });

  it('routes app-absolute links through the router and closes the drawer', () => {
    helpService.currentTopic = TOPICS[1];
    open$.next(true);
    fixture.detectChanges();

    const link = fixture.debugElement.query(By.css('.help-content a[href="/manage/users"]'));
    (link.nativeElement as HTMLElement).click();

    expect(router.navigateByUrl).toHaveBeenCalledWith('/manage/users');
    expect(helpService.close).toHaveBeenCalled();
  });

  it('leaves modified clicks (e.g. ctrl-click for a new tab) to the browser', () => {
    helpService.currentTopic = TOPICS[1];
    open$.next(true);
    fixture.detectChanges();

    const link = fixture.debugElement.query(By.css('.help-content a[href="/manage/users"]'));
    const event = new MouseEvent('click', { bubbles: true, cancelable: true, ctrlKey: true });
    (link.nativeElement as HTMLElement).dispatchEvent(event);

    expect(event.defaultPrevented).toBeFalse();
    expect(router.navigateByUrl).not.toHaveBeenCalled();
    expect(helpService.close).not.toHaveBeenCalled();
  });

  it('closes on Escape', () => {
    open$.next(true);
    fixture.detectChanges();

    const drawer = fixture.debugElement.query(By.css('.help-drawer'));
    (drawer.nativeElement as HTMLElement).dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));

    expect(helpService.close).toHaveBeenCalled();
  });

  it('blocks page scrolling while open and restores it on close', () => {
    open$.next(true);
    fixture.detectChanges();
    expect(document.body.style.overflow).toBe('hidden');

    open$.next(false);
    fixture.detectChanges();
    expect(document.body.style.overflow).toBe('');
  });

  it('shows the failure message when a doc cannot be loaded', () => {
    helpService.getTopicHtml.and.returnValue(of(null));
    helpService.currentTopic = TOPICS[1];
    open$.next(true);
    fixture.detectChanges();

    expect(drawerText()).toContain('This help topic could not be loaded');
  });
});
