import { Component, OnInit } from '@angular/core';

@Component({
    selector: 'app-home',
    templateUrl: './home.component.html',
    styleUrls: ['./home.component.css'],
    standalone: false
})
export class HomeComponent implements OnInit {
  private static readonly homeHeroImageUrl = 'assets/trees1.jpg';

  title = "COHAD";
  subtitles = ["Canyon Oaks Homeowners Assssociation", "Denton"];

  constructor() { }

  ngOnInit() {
    this.warmHeaderHeroImages();
  }

  private warmHeaderHeroImages(): void {
    const img = new Image();
    img.onload = () => {
      void img.decode?.().catch(() => { /* ignore decode errors */ });
    };
    img.src = HomeComponent.homeHeroImageUrl;
  }

}
