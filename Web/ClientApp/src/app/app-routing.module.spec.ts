import { RouterStateSnapshot } from '@angular/router';
import { Title } from '@angular/platform-browser';
import { LiveAnnouncer } from '@angular/cdk/a11y';
import { CohadTitleStrategy } from './app-routing.module';

/**
 * The announcement lives here rather than in a NavigationEnd subscriber because
 * a subscriber races the router and announces the *previous* page's title. These
 * tests lock both halves of that: the title text, and that it is announced only
 * after the initial page load.
 */
describe('CohadTitleStrategy', () => {
  let strategy: CohadTitleStrategy;
  let titles: string[];
  let announced: string[];
  let nextPageTitle: string | undefined;

  /** Drives one navigation, with `buildTitle` resolving to the given route title. */
  function navigateTo(pageTitle: string | undefined): void {
    nextPageTitle = pageTitle;
    strategy.updateTitle({} as RouterStateSnapshot);
  }

  beforeEach(() => {
    titles = [];
    announced = [];
    nextPageTitle = undefined;
    const title = { setTitle: (t: string) => titles.push(t) } as unknown as Title;
    const announcer = {
      announce: (m: string) => {
        announced.push(m);
        return Promise.resolve();
      },
    } as unknown as LiveAnnouncer;
    strategy = new CohadTitleStrategy(title, announcer);
    spyOn(strategy, 'buildTitle').and.callFake(() => nextPageTitle);
  });

  it('prefixes the page title', () => {
    navigateTo('News');
    expect(titles).toEqual(['COHAD | News']);
  });

  it('falls back to the bare app name when a route declares no title', () => {
    navigateTo(undefined);
    expect(titles).toEqual(['COHAD']);
  });

  it('does not announce the initial page load', () => {
    navigateTo('Home');
    expect(announced).toEqual([]);
  });

  it('announces the new title on every subsequent navigation', () => {
    navigateTo('Home');
    navigateTo('News');
    navigateTo('Events');

    // The title announced must be the one just set, not the one it replaced.
    expect(announced).toEqual(['COHAD | News', 'COHAD | Events']);
  });
});
