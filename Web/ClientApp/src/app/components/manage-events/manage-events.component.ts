import { Component, OnInit } from '@angular/core';
import { MatDialog } from '@angular/material/dialog';
import { EventDetail, EventsService } from 'src/app/services/events.service';
import { EventEditorDialogComponent, EventEditorDialogData } from 'src/app/components/event-editor-dialog/event-editor-dialog.component';
import { ConfirmDialogComponent } from '../confirm-dialog/confirm-dialog.component';

@Component({
  selector: 'app-manage-events',
  templateUrl: './manage-events.component.html',
  styleUrls: ['./manage-events.component.css'],
  standalone: false,
})
export class ManageEventsComponent implements OnInit {
  upcomingEvents: EventDetail[] = [];
  pastEvents: EventDetail[] = [];
  loading = false;
  deletingId: string | null = null;
  error = '';
  success = '';

  constructor(
    private readonly eventsService: EventsService,
    private readonly dialog: MatDialog,
  ) {}

  ngOnInit(): void {
    this.loadEvents();
  }

  openEditor(eventItem: EventDetail | null): void {
    this.error = '';
    this.success = '';

    const ref = this.dialog.open(EventEditorDialogComponent, {
      data: { event: eventItem } as EventEditorDialogData,
      width: '640px',
      maxWidth: 'calc(100vw - 32px)',
      maxHeight: '90vh',
      panelClass: 'event-editor-dialog-panel',
    });

    ref.afterClosed().subscribe(result => {
      if (result === true) {
        this.success = eventItem == null ? 'Event created.' : 'Event updated.';
        this.loadEvents();
      }
    });
  }

  deleteEvent(eventItem: EventDetail): void {
    const ref = this.dialog.open(ConfirmDialogComponent, {
      data: {
        title: 'Delete event?',
        body: `This will permanently delete "${eventItem.title}".\n\nYou can’t undo this.`,
        confirmText: 'Delete',
        cancelText: 'Cancel',
        confirmColor: 'warn',
      },
    });

    ref.afterClosed().subscribe(confirmed => {
      if (confirmed !== true) {
        return;
      }

      this.error = '';
      this.success = '';
      this.deletingId = eventItem.id;
      this.eventsService.deleteManage(eventItem.id).subscribe({
        next: () => {
          this.deletingId = null;
          this.success = 'Event deleted.';
          this.loadEvents();
        },
        error: () => {
          this.deletingId = null;
          this.error = 'Failed to delete event.';
        },
      });
    });
  }

  /** Show signup block when signups are open, or when there are existing registrations to review. */
  showSignupSection(eventItem: EventDetail): boolean {
    return eventItem.allowSignups || eventItem.totalSignups > 0;
  }

  private loadEvents(): void {
    this.loading = true;
    this.eventsService.getManage().subscribe({
      next: payload => {
        this.upcomingEvents = payload?.upcoming ?? [];
        this.pastEvents = payload?.past ?? [];
        this.loading = false;
      },
      error: () => {
        this.upcomingEvents = [];
        this.pastEvents = [];
        this.loading = false;
        this.error = 'Failed to load events.';
      },
    });
  }
}
