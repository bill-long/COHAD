import { HttpErrorResponse } from '@angular/common/http';

/**
 * Turns HttpClient failures into a short user-facing string.
 * Avoids using `error.error` when it is a ProgressEvent (common for upload/network errors).
 */
export function httpErrorMessage(err: unknown, fallback: string): string {
  if (!(err instanceof HttpErrorResponse)) {
    if (err instanceof Error) {
      return err.message;
    }
    return fallback;
  }

  const body = err.error;
  if (typeof body === 'string' && body.trim().length > 0) {
    return body;
  }
  if (body instanceof ProgressEvent) {
    return err.status === 0
      ? 'Network error. Check your connection and try again.'
      : fallback;
  }
  if (body != null && typeof body === 'object' && !Array.isArray(body)) {
    const o = body as { error?: unknown; message?: unknown; title?: unknown };
    if (typeof o.error === 'string' && o.error.trim().length > 0) {
      return o.error;
    }
    if (typeof o.message === 'string' && o.message.trim().length > 0) {
      return o.message;
    }
    if (typeof o.title === 'string' && o.title.trim().length > 0) {
      return o.title;
    }
  }

  if (err.status === 0) {
    return 'Network error. Check your connection and try again.';
  }
  if (err.status === 413) {
    return 'The file is too large.';
  }

  return err.message || fallback;
}
