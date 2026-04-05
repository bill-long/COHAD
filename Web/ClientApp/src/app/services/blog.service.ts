import { HttpClient } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';

export interface BlogPostCard {
  id: string;
  publicSlug: string;
  title: string;
  excerpt: string;
  publishUtc: string;
  authorDisplayName: string;
  hasFeaturedImage: boolean;
  featuredImageContentType: string | null;
  featuredImageDownloadUrl: string | null;
}

export interface BlogPostDetail extends BlogPostCard {
  content: string;
  featuredImageDisplayName: string | null;
}

export interface BlogComment {
  id: string;
  authorDisplayName: string;
  content: string;
  createdUtc: string;
  canDelete: boolean;
}

export interface BlogPostUpsertPayload {
  id?: string | null;
  title: string;
  content: string;
  excerpt: string;
  publishUtc: string;
  removeFeaturedImage: boolean;
}

@Injectable({
  providedIn: 'root',
})
export class BlogService {
  constructor(private readonly httpClient: HttpClient) {}

  getPublished(): Observable<BlogPostCard[]> {
    return this.httpClient.get<BlogPostCard[]>('api/blog');
  }

  getBySlug(slug: string): Observable<BlogPostDetail> {
    return this.httpClient.get<BlogPostDetail>(`api/blog/${encodeURIComponent(slug)}`);
  }

  getManage(): Observable<BlogPostDetail[]> {
    return this.httpClient.get<BlogPostDetail[]>('api/blog/manage');
  }

  upsertManage(payload: BlogPostUpsertPayload, featuredImage?: File | null): Observable<BlogPostDetail> {
    const formData = new FormData();
    if (payload.id) {
      formData.append('id', payload.id);
    }
    formData.append('title', payload.title);
    formData.append('content', payload.content);
    formData.append('excerpt', payload.excerpt ?? '');
    formData.append('publishUtc', payload.publishUtc);
    formData.append('removeFeaturedImage', String(payload.removeFeaturedImage));
    if (featuredImage != null) {
      formData.append('featuredImage', featuredImage);
    }

    return this.httpClient.post<BlogPostDetail>('api/blog/manage', formData);
  }

  deleteManage(postId: string): Observable<void> {
    return this.httpClient.delete<void>(`api/blog/manage/${postId}`);
  }

  uploadImage(file: File): Observable<{ url: string }> {
    const formData = new FormData();
    formData.append('file', file);
    return this.httpClient.post<{ url: string }>('api/blog/upload-image', formData);
  }

  getComments(slug: string): Observable<BlogComment[]> {
    return this.httpClient.get<BlogComment[]>(`api/blog/${encodeURIComponent(slug)}/comments`);
  }

  addComment(slug: string, content: string): Observable<BlogComment> {
    return this.httpClient.post<BlogComment>(`api/blog/${encodeURIComponent(slug)}/comments`, { content });
  }

  deleteComment(slug: string, commentId: string): Observable<void> {
    return this.httpClient.delete<void>(`api/blog/${encodeURIComponent(slug)}/comments/${commentId}`);
  }
}
