import { Component, OnInit, Input } from '@angular/core';

@Component({
    selector: 'app-header',
    inputs: ['title', 'subtitles', 'imagePath', 'noBottomGap', 'fillViewport', 'imageFetchPriority'],
    styleUrls: ['./header.component.css'],
    template: `
  <div class="header" [class.no-bottom-gap]="noBottomGap" [class.fill-viewport]="fillViewport">
    <img class="header-bg" [src]="imagePath" alt="" loading="eager" [attr.fetchpriority]="imageFetchPriority || null" />
    <div class="header-content">
      <h1 class="mat-headline-1 font-weight-bold">{{title}}</h1>
      <h1 class="mat-headline-3" *ngFor="let subtitle of subtitles || []">{{subtitle}}</h1>
    </div>
  </div>
  `,
    standalone: false
})
export class HeaderComponent implements OnInit {

  title!: string;
  subtitles!: string[];
  imagePath!: string;
  noBottomGap = false;
  fillViewport = false;
  imageFetchPriority?: 'high' | 'low' | 'auto';

  constructor() { }

  ngOnInit() {
  }

}
