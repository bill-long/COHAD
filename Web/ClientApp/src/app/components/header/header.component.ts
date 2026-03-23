import { Component, OnInit, Input } from '@angular/core';

@Component({
    selector: 'app-header',
    styleUrls: ['./header.component.css'],
    template: `
  <div class="header" [class.no-bottom-gap]="noBottomGap" [class.fill-viewport]="fillViewport">
    <img class="header-bg" [src]="imagePath" alt="" loading="eager" [attr.fetchpriority]="imageFetchPriority || null" />
    <div class="header-content">
      <h1 class="mat-headline-1 font-weight-bold">{{title}}</h1>
      <h1 class="mat-headline-3" *ngFor="let subtitle of subtitles || []">{{subtitle}}</h1>
    </div>
    <div class="header-overlay-slot">
      <ng-content></ng-content>
    </div>
  </div>
  `,
    standalone: false
})
export class HeaderComponent implements OnInit {

  @Input() title!: string;
  @Input() subtitles!: string[];
  @Input() imagePath!: string;
  @Input() noBottomGap = false;
  @Input() fillViewport = false;
  @Input() imageFetchPriority?: 'high' | 'low' | 'auto';

  constructor() { }

  ngOnInit() {
  }

}
