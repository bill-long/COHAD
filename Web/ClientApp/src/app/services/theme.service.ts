import { Injectable } from '@angular/core';
import { BehaviorSubject, Observable } from 'rxjs';

type ThemePreference = 'light' | 'dark' | 'system';

@Injectable({
  providedIn: 'root',
})
export class ThemeService {
  private readonly themeStorageKey = 'cohad.themePreference';
  private readonly darkThemeClass = 'dark-theme';
  private readonly isDarkThemeSubject = new BehaviorSubject<boolean>(false);

  get isDarkTheme$(): Observable<boolean> {
    return this.isDarkThemeSubject.asObservable();
  }

  initializeTheme(): void {
    const preference = this.getStoredPreference();
    const prefersDark = window.matchMedia('(prefers-color-scheme: dark)').matches;
    const shouldUseDark = preference === 'dark' || (preference !== 'light' && prefersDark);
    this.applyDarkMode(shouldUseDark);
  }

  toggleTheme(): void {
    const nextIsDark = !this.isDarkThemeSubject.value;
    this.applyDarkMode(nextIsDark);
    localStorage.setItem(this.themeStorageKey, nextIsDark ? 'dark' : 'light');
  }

  private applyDarkMode(isDark: boolean): void {
    document.body.classList.toggle(this.darkThemeClass, isDark);
    this.isDarkThemeSubject.next(isDark);
  }

  private getStoredPreference(): ThemePreference {
    const value = localStorage.getItem(this.themeStorageKey);
    if (value === 'dark' || value === 'light' || value === 'system') {
      return value;
    }

    return 'system';
  }
}
