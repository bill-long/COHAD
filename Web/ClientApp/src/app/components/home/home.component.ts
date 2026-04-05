import { Component, OnInit } from '@angular/core';
import { EventCard, EventsService } from 'src/app/services/events.service';

@Component({
  selector: 'app-home',
  templateUrl: './home.component.html',
  styleUrls: ['./home.component.css'],
  standalone: false,
})
export class HomeComponent implements OnInit {
  private static readonly homeHeroImageUrl = 'assets/trees1.jpg';

  title = 'COHAD';
  subtitles = ['Canyon Oaks Homeowners Association', 'Denton'];
  nextEvent: EventCard | null = null;

  constructor(private readonly eventsService: EventsService) {}

  ngOnInit() {
    this.warmHeaderHeroImages();
    this.loadNextEvent();
  }

  private warmHeaderHeroImages(): void {
    const img = new Image();
    img.onload = () => {
      void img.decode?.().catch(() => {
        /* ignore decode errors */
      });
    };
    img.src = HomeComponent.homeHeroImageUrl;
  }

  private loadNextEvent(): void {
    this.eventsService.getNextUpcoming().subscribe({
      next: eventItem => (this.nextEvent = eventItem),
      error: () => (this.nextEvent = null),
    });
  }
}
