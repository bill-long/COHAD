import { Component, OnInit } from '@angular/core';

@Component({
    selector: 'app-home',
    templateUrl: './home.component.html',
    styleUrls: ['./home.component.css'],
    standalone: false
})
export class HomeComponent implements OnInit {

  title = "COHAD";
  subtitles = ["Canyon Oaks Homeowners Assssociation", "Denton"];

  constructor() { }

  ngOnInit() {
  }

}
