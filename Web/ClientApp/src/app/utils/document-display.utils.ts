import { ResidentDocument } from 'src/app/services/documents.service';

/** Human-readable size for directory listings (shared by resident + manage documents views). */
export function formatFileSize(sizeBytes: number): string {
  if (sizeBytes < 1024) {
    return `${sizeBytes} B`;
  }

  if (sizeBytes < 1024 * 1024) {
    return `${(sizeBytes / 1024).toFixed(1)} KB`;
  }

  return `${(sizeBytes / (1024 * 1024)).toFixed(1)} MB`;
}

export function getFileExtension(fileName: string): string {
  const lastDot = fileName.lastIndexOf('.');
  if (lastDot < 0 || lastDot === fileName.length - 1) {
    return '';
  }

  return fileName.substring(lastDot + 1).toLowerCase();
}

export function getFileTypeChipLabel(doc: Pick<ResidentDocument, 'displayName' | 'contentType'>): string {
  const extension = getFileExtension(doc.displayName);
  if (extension) {
    return extension.toUpperCase();
  }

  if (doc.contentType?.includes('pdf')) {
    return 'PDF';
  }

  return 'FILE';
}

export function getFileIconName(doc: Pick<ResidentDocument, 'displayName' | 'contentType'>): string {
  const extension = getFileExtension(doc.displayName);
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
