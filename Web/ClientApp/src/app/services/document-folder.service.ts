import { HttpClient } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';

export interface DocumentFolder {
  id: string;
  name: string;
  sortOrder: number;
  documentCount: number;
}

export interface ReorderItem {
  id: string;
  sortOrder: number;
}

@Injectable({
  providedIn: 'root',
})
export class DocumentFolderService {
  constructor(private readonly httpClient: HttpClient) {}

  getAll(): Observable<DocumentFolder[]> {
    return this.httpClient.get<DocumentFolder[]>('api/document-folder');
  }

  create(name: string): Observable<DocumentFolder> {
    return this.httpClient.post<DocumentFolder>('api/document-folder', { name });
  }

  update(id: string, name: string, sortOrder?: number): Observable<DocumentFolder> {
    return this.httpClient.put<DocumentFolder>(`api/document-folder/${id}`, { name, sortOrder });
  }

  delete(id: string): Observable<void> {
    return this.httpClient.delete<void>(`api/document-folder/${id}`);
  }

  reorder(items: ReorderItem[]): Observable<void> {
    return this.httpClient.put<void>('api/document-folder/reorder', items);
  }
}
