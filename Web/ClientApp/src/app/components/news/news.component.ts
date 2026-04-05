import { Component, OnInit } from '@angular/core';

@Component({
  selector: 'app-news',
  templateUrl: './news.component.html',
  styleUrls: ['./news.component.css'],
  standalone: false,
})
export class NewsComponent implements OnInit {
  date = new Date('2019-02-26');

  constructor() {}

  ngOnInit() {}
}
