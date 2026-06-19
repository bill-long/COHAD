import { TestBed } from '@angular/core/testing';
import { ThemeService } from './theme.service';

describe('ThemeService', () => {
  let service: ThemeService;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [ThemeService] });
    service = TestBed.inject(ThemeService);
    localStorage.clear();
    document.body.classList.remove('dark-theme');
  });

  afterEach(() => {
    localStorage.clear();
    document.body.classList.remove('dark-theme');
  });

  describe('forceLightTheme', () => {
    it('removes the dark-theme class and updates the observable', () => {
      service.toggleTheme(); // -> dark
      expect(document.body.classList.contains('dark-theme')).toBe(true);

      service.forceLightTheme();

      expect(document.body.classList.contains('dark-theme')).toBe(false);
      let isDark = true;
      service.isDarkTheme$.subscribe(v => (isDark = v)).unsubscribe();
      expect(isDark).toBe(false);
    });

    it('restore callback re-applies the prior dark theme', () => {
      service.toggleTheme(); // -> dark

      const restore = service.forceLightTheme();
      expect(document.body.classList.contains('dark-theme')).toBe(false);

      restore();
      expect(document.body.classList.contains('dark-theme')).toBe(true);
    });

    it('restore callback keeps light mode when it was already light', () => {
      const restore = service.forceLightTheme();
      restore();

      expect(document.body.classList.contains('dark-theme')).toBe(false);
    });

    it('does not change the stored preference', () => {
      service.toggleTheme(); // persists 'dark'
      expect(localStorage.getItem('cohad.themePreference')).toBe('dark');

      service.forceLightTheme();

      expect(localStorage.getItem('cohad.themePreference')).toBe('dark');
    });
  });
});
