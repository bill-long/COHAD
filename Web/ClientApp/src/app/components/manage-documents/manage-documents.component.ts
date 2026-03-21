import { Component, ElementRef, OnInit, ViewChild } from '@angular/core';
import { HttpEventType } from '@angular/common/http';
import { MatSnackBar } from '@angular/material/snack-bar';
import { DocumentsService, ResidentDocument } from 'src/app/services/documents.service';

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
    private readonly snackBar: MatSnackBar) { }

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
    if (!confirm(`Delete '${doc.displayName}'?`)) {
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
  }

  formatSize(sizeBytes: number): string {
    if (sizeBytes < 1024) {
      return `${sizeBytes} B`;
    }

    if (sizeBytes < 1024 * 1024) {
      return `${(sizeBytes / 1024).toFixed(1)} KB`;
    }

    return `${(sizeBytes / (1024 * 1024)).toFixed(1)} MB`;
  }

  getFileTypeChip(doc: ResidentDocument): string {
    const extension = this.getExtension(doc.displayName);
    if (extension) {
      return extension.toUpperCase();
    }

    if (doc.contentType?.includes('pdf')) {
      return 'PDF';
    }

    return 'FILE';
  }

  getFileIcon(doc: ResidentDocument): string {
    const extension = this.getExtension(doc.displayName);
    if (extension === 'pdf') {
      return 'picture_as_pdf';
    }

    if (extension === 'doc' || extension === 'docx') {
      return 'description';
    }

    if (extension === 'xls' || extension === 'xlsx') {
      return 'table_chart';
    }

    if (extension === 'txt') {
      return 'article';
    }

    return 'insert_drive_file';
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

  private getExtension(fileName: string): string {
    const lastDot = fileName.lastIndexOf('.');
    if (lastDot < 0 || lastDot === fileName.length - 1) {
      return '';
    }

    return fileName.substring(lastDot + 1).toLowerCase();
  }

  private resetFileInput(): void {
    if (this.fileInput?.nativeElement) {
      this.fileInput.nativeElement.value = '';
    }
  }
}
