import { Component, OnInit } from '@angular/core';
import { DocumentsService, ResidentDocument } from 'src/app/services/documents.service';

@Component({
  selector: 'app-documents',
  templateUrl: './documents.component.html',
  styleUrls: ['./documents.component.css'],
  standalone: false
})
export class DocumentsComponent implements OnInit {
  documents: ResidentDocument[] = [];
  loading = false;
  error = '';

  constructor(
    private readonly documentsService: DocumentsService) { }

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
}
