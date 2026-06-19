import { Component, OnDestroy, OnInit } from '@angular/core';
import { ThemeService } from '../../services/theme.service';

@Component({
  selector: 'app-rendered-printable-map',
  templateUrl: './rendered-printable-map.component.html',
  styleUrls: ['./rendered-printable-map.component.css'],
  standalone: false,
})
export class RenderedPrintableMapComponent implements OnInit, OnDestroy {
  /** Restores the user's theme when leaving this light-mode-only print view. */
  private restoreTheme?: () => void;

  constructor(private readonly themeService: ThemeService) {}

  ngOnInit(): void {
    this.restoreTheme = this.themeService.forceLightTheme();
  }

  ngOnDestroy(): void {
    this.restoreTheme?.();
  }
}
