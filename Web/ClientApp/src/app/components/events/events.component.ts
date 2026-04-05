import { Component, OnInit } from '@angular/core';
import { EventCard, EventsService } from 'src/app/services/events.service';

@Component({
  selector: 'app-events',
  templateUrl: './events.component.html',
  styleUrls: ['./events.component.css'],
  standalone: false,
})
export class EventsComponent implements OnInit {
  events: EventCard[] = [];
  loading = false;
  error = '';

  constructor(private readonly eventsService: EventsService) {}

  ngOnInit(): void {
    this.loadEvents();
  }

  private loadEvents(): void {
    this.loading = true;
    this.eventsService.getUpcoming().subscribe({
      next: events => {
        this.events = events ?? [];
        this.loading = false;
      },
      error: () => {
        this.events = [];
        this.loading = false;
        this.error = 'Failed to load events.';
      },
    });
  }
}
