import { Component, ChangeDetectorRef, ElementRef, HostListener, ViewChild } from '@angular/core';

@Component({
  selector: 'app-rendered-printable-directory',
  templateUrl: './rendered-printable-directory.component.html',
  styleUrls: ['./rendered-printable-directory.component.css'],
  standalone: false,
})
export class RenderedPrintableDirectoryComponent {
  @ViewChild('directoryPrintRoot', { static: true })
  directoryPrintRoot?: ElementRef<HTMLElement>;

  /** Same min-height as `.print-blank` / `.print-cover` — used to convert content height to page count. */
  @ViewChild('pageBodyProbe', { static: true })
  pageBodyProbe?: ElementRef<HTMLElement>;

  /** "Month, Year" at print time (refreshed in beforeprint). */
  printMonthYearLine = RenderedPrintableDirectoryComponent.formatMonthYear(new Date());

  /** Trailing blank pages before back cover (1 or 2) for duplex parity. */
  trailingBlankCount = 1;

  constructor(private readonly cdr: ChangeDetectorRef) {}

  private static formatMonthYear(d: Date): string {
    const month = d.toLocaleDateString('en-US', { month: 'long' });
    return `${month}, ${d.getFullYear()}`;
  }

  get trailingBlankIndices(): number[] {
    return Array.from({ length: this.trailingBlankCount }, (_, i) => i);
  }

  @HostListener('window:beforeprint')
  onBeforePrint(): void {
    this.printMonthYearLine = RenderedPrintableDirectoryComponent.formatMonthYear(new Date());
    this.cdr.detectChanges();
    requestAnimationFrame(() => {
      requestAnimationFrame(() => {
        setTimeout(() => this.updateTrailingBlanks(), 0);
      });
    });
  }

  private updateTrailingBlanks(): void {
    const el = this.directoryPrintRoot?.nativeElement;
    const probe = this.pageBodyProbe?.nativeElement;
    if (!el || !probe) {
      return;
    }

    const pageBodyPx = probe.offsetHeight;
    if (pageBodyPx <= 0) {
      return;
    }

    const scrollHeight = el.scrollHeight;
    const contentPages = Math.max(1, Math.ceil(scrollHeight / pageBodyPx));

    // Total = 3 + contentPages + b must be even => b ≡ contentPages + 1 (mod 2)
    this.trailingBlankCount = contentPages % 2 === 0 ? 1 : 2;
    this.cdr.detectChanges();
  }
}
