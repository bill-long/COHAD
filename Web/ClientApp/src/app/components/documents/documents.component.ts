import { Component, OnInit } from '@angular/core';
import { DocumentsService, ResidentDocument } from 'src/app/services/documents.service';
import { DocumentFolderService, DocumentFolder } from 'src/app/services/document-folder.service';
import { ApplicationInsightsService } from 'src/app/services/application-insights.service';
import { formatFileSize, getFileIconName, getFileTypeChipLabel } from 'src/app/utils/document-display.utils';

interface FolderGroup {
  folder: DocumentFolder;
  documents: ResidentDocument[];
  collapsed: boolean;
}

@Component({
  selector: 'app-documents',
  templateUrl: './documents.component.html',
  styleUrls: ['./documents.component.css'],
  standalone: false,
})
export class DocumentsComponent implements OnInit {
  folderGroups: FolderGroup[] = [];
  unfiledDocuments: ResidentDocument[] = [];
  loading = false;
  error = '';

  constructor(
    private readonly documentsService: DocumentsService,
    private readonly folderService: DocumentFolderService,
    private readonly telemetry: ApplicationInsightsService,
  ) {}

  ngOnInit(): void {
    this.loadDocuments();
  }

  toggleFolder(group: FolderGroup): void {
    group.collapsed = !group.collapsed;
  }

  downloadFile(doc: ResidentDocument): void {
    this.documentsService.download(doc.id).subscribe(blob => {
      this.telemetry.trackEvent('DocumentDownloaded', { documentName: doc.displayName });
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

  get hasDocuments(): boolean {
    return this.folderGroups.some(g => g.documents.length > 0) || this.unfiledDocuments.length > 0;
  }

  private loadDocuments(): void {
    this.loading = true;
    this.folderService.getAll().subscribe({
      next: folders => {
        this.documentsService.getAll().subscribe({
          next: docs => {
            this.buildGroups(folders, docs ?? []);
            this.loading = false;
          },
          error: () => {
            this.loading = false;
            this.error = 'Failed to load documents.';
          },
        });
      },
      error: () => {
        this.loading = false;
        this.error = 'Failed to load documents.';
      },
    });
  }

  private buildGroups(folders: DocumentFolder[], docs: ResidentDocument[]): void {
    const byFolder = new Map<string, ResidentDocument[]>();
    const unfiled: ResidentDocument[] = [];

    for (const doc of docs) {
      if (doc.folderId) {
        const list = byFolder.get(doc.folderId) ?? [];
        list.push(doc);
        byFolder.set(doc.folderId, list);
      } else {
        unfiled.push(doc);
      }
    }

    this.folderGroups = folders
      .filter(f => (byFolder.get(f.id)?.length ?? 0) > 0)
      .map(f => ({
        folder: f,
        documents: byFolder.get(f.id) ?? [],
        collapsed: false,
      }));

    this.unfiledDocuments = unfiled;
  }
}
