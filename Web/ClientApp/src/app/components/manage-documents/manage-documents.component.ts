import { Component, ElementRef, OnInit, ViewChild } from '@angular/core';
import { HttpEventType } from '@angular/common/http';
import { MatDialog } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { DocumentsService, ResidentDocument } from 'src/app/services/documents.service';
import { ConfirmDialogComponent } from '../confirm-dialog/confirm-dialog.component';
import {
  formatFileSize,
  getFileIconName,
  getFileTypeChipLabel
} from 'src/app/utils/document-display.utils';

@Component({
  selector: 'app-manage-documents',
  templateUrl: './manage-documents.component.html',
  styleUrls: ['./manage-documents.component.css'],
  standalone: false
})
export class ManageDocumentsComponent implements OnInit {
  @ViewChild('fileInput') fileInput?: ElementRef<HTMLInputElement>;

  documents: ResidentDocument[] = [];
  loading = false;
  uploadInProgress = false;
  uploadProgress = 0;
  deletingId: string | null = null;
  selectedFile: File | null = null;
  dragActive = false;
  error = '';

  constructor(
    private readonly documentsService: DocumentsService,
    private readonly snackBar: MatSnackBar,
    private readonly dialog: MatDialog) { }

  ngOnInit(): void {
    this.loadDocuments();
  }

  downloadFile(doc: ResidentDocument): void {
    this.documentsService.download(doc.id).subscribe(blob => {
      const url = window.URL.createObjectURL(blob);
      const downloadLink = window.document.createElement('a');
      downloadLink.href = url;
      downloadLink.setAttribute('download', doc.displayName);
      window.document.body.appendChild(downloadLink);
      downloadLink.click();
      downloadLink.remove();
      window.URL.revokeObjectURL(url);
    });
  }

  onFileSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    this.selectedFile = input.files?.item(0) ?? null;
    this.error = '';
  }

  triggerFilePicker(): void {
    if (!this.uploadInProgress) {
      this.fileInput?.nativeElement.click();
    }
  }

  onDragOver(event: DragEvent): void {
    event.preventDefault();
    this.dragActive = true;
  }

  onDragLeave(event: DragEvent): void {
    event.preventDefault();
    this.dragActive = false;
  }

  onDrop(event: DragEvent): void {
    event.preventDefault();
    this.dragActive = false;
    const file = event.dataTransfer?.files?.item(0);
    if (file) {
      this.selectedFile = file;
      this.error = '';
    }
  }

  clearSelectedFile(): void {
    this.selectedFile = null;
    this.resetFileInput();
  }

  uploadDocument(): void {
    if (!this.selectedFile) {
      this.error = 'Select a file to upload.';
      return;
    }

    this.error = '';
    this.uploadInProgress = true;
    this.uploadProgress = 0;
    this.documentsService.upload(this.selectedFile).subscribe({
      next: event => {
        if (event.type === HttpEventType.UploadProgress && event.total) {
          this.uploadProgress = Math.round((event.loaded / event.total) * 100);
        }

        if (event.type === HttpEventType.Response) {
          this.uploadInProgress = false;
          this.uploadProgress = 100;
          this.selectedFile = null;
          this.resetFileInput();
          this.snackBar.open('Document uploaded.', 'Dismiss', { duration: 2500 });
          this.loadDocuments();
        }
      },
      error: () => {
        this.uploadInProgress = false;
        this.uploadProgress = 0;
        this.error = 'Upload failed.';
      }
    });
  }

  deleteDocument(doc: ResidentDocument): void {
    const ref = this.dialog.open(ConfirmDialogComponent, {
      data: {
        title: 'Delete document?',
        body: `This will permanently remove "${doc.displayName}".\n\nYou can’t undo this.`,
        confirmText: 'Delete',
        cancelText: 'Cancel',
        confirmColor: 'warn'
      }
    });

    ref.afterClosed().subscribe(confirmed => {
      if (confirmed !== true) {
        return;
      }

      this.error = '';
      this.deletingId = doc.id;
      this.documentsService.delete(doc.id).subscribe({
        next: () => {
          this.deletingId = null;
          this.snackBar.open('Document deleted.', 'Dismiss', { duration: 2500 });
          this.loadDocuments();
        },
        error: () => {
          this.deletingId = null;
          this.error = 'Delete failed.';
        }
      });
    });
  }

  formatSize(sizeBytes: number): string {
    return formatFileSize(sizeBytes);
  }

  getFileTypeChip(doc: ResidentDocument): string {
    return getFileTypeChipLabel(doc);
  }

  getFileIcon(doc: ResidentDocument): string {
    return getFileIconName(doc);
  }

  private loadDocuments(): void {
    this.loading = true;
    this.documentsService.getAll().subscribe({
      next: docs => {
        this.documents = docs ?? [];
        this.loading = false;
      },
      error: () => {
        this.documents = [];
        this.loading = false;
        this.error = 'Failed to load documents.';
      }
    });
  }

  private resetFileInput(): void {
    if (this.fileInput?.nativeElement) {
      this.fileInput.nativeElement.value = '';
    }
  }
}
