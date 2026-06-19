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

  /** Committees section — its own page flow (a forced page break separates it from the directory). */
  @ViewChild('committeesBlock', { static: true, read: ElementRef })
  committeesBlock?: ElementRef<HTMLElement>;

  /** Resident directory section — starts on a fresh page after the committees section. */
  @ViewChild('directoryBlock', { static: true })
  directoryBlock?: ElementRef<HTMLElement>;

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
        // Committee photos load asynchronously; wait for them so scrollHeight
        // reflects the final laid-out content before computing page parity.
        this.whenImagesReady().then(() => setTimeout(() => this.updateTrailingBlanks(), 0));
      });
    });
  }

  /** Resolves once all images in the print root have loaded (or after a safety timeout). */
  private whenImagesReady(): Promise<void> {
    const el = this.directoryPrintRoot?.nativeElement;
    if (!el) {
      return Promise.resolve();
    }

    const images = Array.from(el.querySelectorAll('img'));
    const pending = images
      .filter(img => !img.complete)
      .map(
        img =>
          new Promise<void>(resolve => {
            // Re-check inside the promise: the image may have finished loading in
            // the gap after the filter, in which case the load event won't fire again.
            if (img.complete) {
              resolve();
              return;
            }
            const done = () => resolve();
            img.addEventListener('load', done, { once: true });
            img.addEventListener('error', done, { once: true });
          }),
      );

    if (pending.length === 0) {
      return Promise.resolve();
    }

    // Never block printing indefinitely if an image stalls.
    const timeout = new Promise<void>(resolve => setTimeout(resolve, 2000));
    return Promise.race([Promise.all(pending).then(() => undefined), timeout]);
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

    // A forced page break separates the committees section from the directory, so each
    // is its own page flow. Sum their individual page counts rather than measuring the
    // combined scrollHeight — otherwise the whitespace at the bottom of the committees'
    // last page is ignored and the parity (and back-cover side) can be off by one.
    const committeesEl = this.committeesBlock?.nativeElement;
    const directoryEl = this.directoryBlock?.nativeElement;
    let contentPages: number;
    if (committeesEl && directoryEl) {
      contentPages =
        Math.ceil(committeesEl.scrollHeight / pageBodyPx) + Math.ceil(directoryEl.scrollHeight / pageBodyPx);
    } else {
      contentPages = Math.ceil(el.scrollHeight / pageBodyPx);
    }
    contentPages = Math.max(1, contentPages);

    // Total = 3 + contentPages + b must be even => b ≡ contentPages + 1 (mod 2)
    this.trailingBlankCount = contentPages % 2 === 0 ? 1 : 2;
    this.cdr.detectChanges();
  }
}
