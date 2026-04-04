import { HttpClient, HttpEvent, HttpRequest } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';

export interface ResidentDocument {
  id: string;
  displayName: string;
  contentType: string;
  sizeBytes: number;
  createdUtc: string;
  folderId?: string;
  folderName?: string;
}

@Injectable({
  providedIn: 'root'
})
export class DocumentsService {
  constructor(private readonly httpClient: HttpClient) { }

  getAll(): Observable<ResidentDocument[]> {
    return this.httpClient.get<ResidentDocument[]>('api/document');
  }

  download(documentId: string): Observable<Blob> {
    return this.httpClient.get(`api/document/${documentId}`, { responseType: 'blob' });
  }

  upload(file: File, folderId?: string): Observable<HttpEvent<ResidentDocument>> {
    const formData = new FormData();
    formData.append('file', file);
    if (folderId) {
      formData.append('folderId', folderId);
    }
    const request = new HttpRequest<FormData>('POST', 'api/document', formData, {
      reportProgress: true
    });
    return this.httpClient.request<ResidentDocument>(request);
  }

  delete(documentId: string): Observable<void> {
    return this.httpClient.delete<void>(`api/document/${documentId}`);
  }
}
