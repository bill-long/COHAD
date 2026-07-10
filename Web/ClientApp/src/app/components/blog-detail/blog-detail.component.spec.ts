import { of, Subject } from 'rxjs';
import { Title } from '@angular/platform-browser';
import { BlogDetailComponent } from './blog-detail.component';
import { BlogPostDetail } from 'src/app/services/blog.service';

// Focused on the browser-tab title behavior added to loadPost. The component is constructed by hand
// (not mounted) so these stay deterministic; loadPost is driven directly with a controllable source.
describe('BlogDetailComponent tab title', () => {
  let title: jasmine.SpyObj<Title>;

  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  function make(getBySlug: any): BlogDetailComponent {
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const blogService: any = { getBySlug: () => getBySlug, getComments: () => of([]) };
    title = jasmine.createSpyObj<Title>('Title', ['setTitle']);
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const sanitizer: any = { sanitize: (_ctx: unknown, v: string) => v, bypassSecurityTrustHtml: (v: string) => v };
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const route: any = { snapshot: { queryParamMap: { keys: [], getAll: () => [] }, fragment: null } };
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const location: any = { replaceState: () => undefined };
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const comp = new BlogDetailComponent(route, location, blogService, sanitizer, title, of({} as any), { next: () => undefined } as any);
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    (comp as any).currentSlug = 'slug';
    return comp;
  }

  const post = (title: string): BlogPostDetail => ({ title, content: '', publicSlug: 'slug' } as BlogPostDetail);
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  const load = (comp: BlogDetailComponent) => (comp as any).loadPost('slug');

  it('sets the tab title to the post title on load', () => {
    const comp = make(of(post('Spring Garden Tips')));
    load(comp);
    expect(title.setTitle).toHaveBeenCalledWith('COHAD | Spring Garden Tips');
  });

  it('falls back to "COHAD | News" when the post has no title', () => {
    const comp = make(of(post('')));
    load(comp);
    expect(title.setTitle).toHaveBeenCalledWith('COHAD | News');
  });

  it('sets "COHAD | News" when the post fails to load', () => {
    const source = new Subject<BlogPostDetail>();
    const comp = make(source);
    load(comp);
    source.error(new Error('boom'));
    expect(title.setTitle).toHaveBeenCalledWith('COHAD | News');
  });

  it('does not touch the title if the response arrives after the component is destroyed', () => {
    const source = new Subject<BlogPostDetail>();
    const comp = make(source);
    load(comp);
    comp.ngOnDestroy();
    source.next(post('Late Post')); // late callback must not clobber the new page's title
    expect(title.setTitle).not.toHaveBeenCalled();
  });
});
