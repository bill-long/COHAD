import { Component, OnInit, Input } from '@angular/core';

@Component({
  selector: 'app-header',
  inputs: ['title', 'subtitles', 'imagePath'],
  styleUrls: ['./header.component.css'],
  template: `
  <div class="header" [ngStyle]="{'background-image': 'url(' + imagePath + ')'}">
    <h1 class="mat-display-4 font-weight-bold">{{title}}</h1>
    <h1 class="mat-display-2" *ngFor="let subtitle of subtitles">{{subtitle}}</h1>
  </div>
  `
})
export class HeaderComponent implements OnInit {

  title!: string;
  subtitles!: string[];
  imagePath!: string;

  constructor() { }

  ngOnInit() {
  }

}
