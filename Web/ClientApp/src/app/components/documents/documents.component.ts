import { Component, OnInit } from '@angular/core';
import { DocumentsService, ResidentDocument } from 'src/app/services/documents.service';
import { ApplicationInsightsService } from 'src/app/services/application-insights.service';
import {
  formatFileSize,
  getFileIconName,
  getFileTypeChipLabel
} from 'src/app/utils/document-display.utils';

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
    private readonly documentsService: DocumentsService,
    private readonly telemetry: ApplicationInsightsService) { }

  ngOnInit(): void {
    this.loadDocuments();
  }

  downloadFile(doc: ResidentDocument): void {
    this.telemetry.trackEvent('DocumentDownloaded', { documentName: doc.displayName });
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

}
