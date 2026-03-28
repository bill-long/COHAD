import { Component, ElementRef, Inject, ViewChild } from '@angular/core';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { finalize } from 'rxjs/operators';
import { EventDetail, EventSignupMode, EventsService } from 'src/app/services/events.service';
import { httpErrorMessage } from 'src/app/utils/http-error-message';

export interface EventEditorDialogData {
  event: EventDetail | null;
}

@Component({
  selector: 'app-event-editor-dialog',
  templateUrl: './event-editor-dialog.component.html',
  styleUrls: ['./event-editor-dialog.component.css'],
  standalone: false
})
export class EventEditorDialogComponent {
  @ViewChild('fileInput') fileInput?: ElementRef<HTMLInputElement>;

  editingEventId: string | null = null;
  /** Snapshot of title when opening edit dialog (shown as subtitle). */
  private readonly initialEventTitle: string | null;

  title = '';
  description = '';
  /** Local calendar date for Material datepicker (time applied separately). */
  startDate: Date | null = null;
  /** `HH:mm` for native time input (local). */
  startTime = '';
  allowSignups = true;
  signupMode: EventSignupMode = 'AdultsAndChildren';
  removePromoMedia = false;
  selectedFile: File | null = null;
  existingPromoFileName = '';
  saving = false;
  error = '';

  constructor(
    private readonly eventsService: EventsService,
    public readonly dialogRef: MatDialogRef<EventEditorDialogComponent, boolean>,
    @Inject(MAT_DIALOG_DATA) public readonly data: EventEditorDialogData
  ) {
    const ev = data.event;
    if (ev != null) {
      this.editingEventId = ev.id;
      this.initialEventTitle = ev.title;
      this.title = ev.title;
      this.description = ev.description ?? '';
      const start = this.utcToStartFields(ev.startUtc);
      this.startDate = start?.date ?? null;
      this.startTime = start?.time ?? '';
      this.allowSignups = ev.allowSignups;
      this.signupMode = ev.signupMode ?? 'AdultsAndChildren';
      this.existingPromoFileName = ev.promoMediaDisplayName ?? '';
    } else {
      this.editingEventId = null;
      this.initialEventTitle = null;
      this.title = '';
      this.description = '';
      this.startDate = null;
      this.startTime = '';
      this.allowSignups = true;
      this.signupMode = 'AdultsAndChildren';
      this.existingPromoFileName = '';
    }
    this.removePromoMedia = false;
    this.selectedFile = null;
  }

  get dialogTitle(): string {
    return this.editingEventId == null ? 'Create event' : 'Edit event';
  }

  get editingSubtitle(): string | null {
    return this.initialEventTitle;
  }

  get showPromoPreview(): boolean {
    return (
      this.editingEventId != null &&
      this.data.event != null &&
      this.data.event.hasPromoMedia &&
      !this.removePromoMedia &&
      this.selectedFile == null &&
      this.data.event.promoMediaDownloadUrl != null
    );
  }

  cancel(): void {
    this.dialogRef.close(false);
  }

  save(): void {
    if (this.saving) {
      return;
    }

    const startUtc = this.toUtcIsoString(this.mergeStartDateTime());
    if (this.title.trim().length === 0 || startUtc == null) {
      this.error = 'Title and valid date/time are required.';
      return;
    }

    this.error = '';
    this.saving = true;
    this.dialogRef.disableClose = true;

    this.eventsService
      .upsertManage(
        {
          id: this.editingEventId,
          title: this.title.trim(),
          description: this.description.trim(),
          startUtc,
          allowSignups: this.allowSignups,
          signupMode: this.signupMode,
          removePromoMedia: this.removePromoMedia
        },
        this.selectedFile
      )
      .pipe(
        finalize(() => {
          this.saving = false;
          this.dialogRef.disableClose = false;
        })
      )
      .subscribe({
        next: () => {
          this.dialogRef.close(true);
        },
        error: (err) => {
          this.error = httpErrorMessage(err, 'Failed to save event.');
        }
      });
  }

  onFileSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    this.selectedFile = input.files?.item(0) ?? null;
    if (this.selectedFile != null) {
      this.removePromoMedia = false;
    }
  }

  clearSelectedFile(): void {
    this.selectedFile = null;
    this.resetFileInput();
  }

  private resetFileInput(): void {
    if (this.fileInput?.nativeElement) {
      this.fileInput.nativeElement.value = '';
    }
  }

  private toUtcIsoString(start: Date | null): string | null {
    if (start == null || Number.isNaN(start.getTime())) {
      return null;
    }
    return start.toISOString();
  }

  /** Combines date + `HH:mm` into a single local `Date`, or null if invalid. */
  private mergeStartDateTime(): Date | null {
    if (this.startDate == null) {
      return null;
    }
    const trimmed = this.startTime.trim();
    if (!trimmed) {
      return null;
    }
    const match = /^(\d{1,2}):(\d{2})$/.exec(trimmed);
    if (match == null) {
      return null;
    }
    const hours = Number(match[1]);
    const minutes = Number(match[2]);
    if (
      Number.isNaN(hours) ||
      Number.isNaN(minutes) ||
      hours < 0 ||
      hours > 23 ||
      minutes < 0 ||
      minutes > 59
    ) {
      return null;
    }
    const d = new Date(this.startDate);
    d.setHours(hours, minutes, 0, 0);
    return d;
  }

  private utcToStartFields(utcDateTime: string): { date: Date; time: string } | null {
    const date = new Date(utcDateTime);
    if (Number.isNaN(date.getTime())) {
      return null;
    }
    const dateOnly = new Date(date.getFullYear(), date.getMonth(), date.getDate());
    const time = `${String(date.getHours()).padStart(2, '0')}:${String(date.getMinutes()).padStart(2, '0')}`;
    return { date: dateOnly, time };
  }
}
