import { Pipe, PipeTransform } from '@angular/core';

/**
 * Coarse relative age for timestamps ("just now", "5m ago", "2h ago", "3d ago"), falling back to a
 * plain date beyond a month. Impure: the input string never changes, so a pure pipe would freeze at
 * the label computed on first render even though the notification menu can now stay open across many
 * interactions. Re-evaluating on change detection keeps the label honest, and the cost is a few
 * arithmetic operations over a handful of rows.
 */
@Pipe({
  name: 'timeAgo',
  standalone: false,
  pure: false,
})
export class TimeAgoPipe implements PipeTransform {
  transform(value: string | Date | null | undefined): string {
    if (!value) {
      return '';
    }
    const then = new Date(value).getTime();
    if (isNaN(then)) {
      return '';
    }
    const seconds = Math.max(0, Math.floor((Date.now() - then) / 1000));
    if (seconds < 60) {
      return 'just now';
    }
    const minutes = Math.floor(seconds / 60);
    if (minutes < 60) {
      return `${minutes}m ago`;
    }
    const hours = Math.floor(minutes / 60);
    if (hours < 24) {
      return `${hours}h ago`;
    }
    const days = Math.floor(hours / 24);
    if (days < 7) {
      return `${days}d ago`;
    }
    const weeks = Math.floor(days / 7);
    if (weeks < 5) {
      return `${weeks}w ago`;
    }
    return new Date(value).toLocaleDateString();
  }
}
