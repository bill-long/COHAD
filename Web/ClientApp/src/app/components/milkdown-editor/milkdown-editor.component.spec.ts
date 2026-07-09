import { NgZone } from '@angular/core';
import { MatSnackBar } from '@angular/material/snack-bar';
import { of } from 'rxjs';
import { MilkdownEditorComponent } from './milkdown-editor.component';
import { BlogService } from 'src/app/services/blog.service';

// These tests exercise the uploader/helper logic the component hands to @milkdown/plugin-upload,
// without mounting the Milkdown (Crepe) editor - the plugin wiring itself is covered by `ng build`
// type-checking and a manual paste/drop pass in the running app. Constructing the component by hand
// keeps the test deterministic and free of the editor's async DOM initialisation.
describe('MilkdownEditorComponent image upload helpers', () => {
  let component: MilkdownEditorComponent;
  let snackBar: jasmine.SpyObj<MatSnackBar>;

  const makeFile = (name: string, type: string) => new File([new Uint8Array([1, 2, 3])], name, { type });

  const fileListOf = (...files: File[]): FileList => {
    const dt = new DataTransfer();
    files.forEach(f => dt.items.add(f));
    return dt.files;
  };

  // A minimal ProseMirror-schema stand-in: enough for the uploader to build image nodes without
  // mounting the real editor.
  const fakeSchema = {
    nodes: {
      image: {
        createAndFill: (attrs: Record<string, unknown>) => ({ type: 'image', attrs }),
      },
    },
  } as unknown as import('@milkdown/kit/prose/model').Schema;

  const emptySchema = { nodes: {} } as unknown as import('@milkdown/kit/prose/model').Schema;

  beforeEach(() => {
    const blog = jasmine.createSpyObj<BlogService>('BlogService', ['uploadImage']);
    blog.uploadImage.and.returnValue(of({ url: 'https://cdn.example/img/x.png' }));
    snackBar = jasmine.createSpyObj<MatSnackBar>('MatSnackBar', ['open']);
    const zone = new NgZone({ enableLongStackTrace: false });
    component = new MilkdownEditorComponent(zone, blog, snackBar);
  });

  // The component cannot be mounted here (Crepe needs a live DOM), so we reach its private helpers
  // directly; `any` is the only way to bypass TypeScript's private-member check for that.
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  const priv = (): any => component as any;

  const altFromFileName = (name: string): string => priv().altFromFileName(name);

  const uploadImages = (
    files: FileList,
    schema: import('@milkdown/kit/prose/model').Schema,
    uploadFn: (f: File) => Promise<string>,
  ): Promise<Array<{ attrs: Record<string, unknown> }>> => priv().uploadImages(files, schema, uploadFn);

  it('strips a single trailing extension', () => {
    expect(altFromFileName('spring-social.png')).toBe('spring-social');
  });

  it('reduces an extension-only name to an empty label', () => {
    expect(altFromFileName('.png')).toBe('');
  });

  it('strips only the final extension from a multi-dot name', () => {
    expect(altFromFileName('photo.final.png')).toBe('photo.final');
  });

  it('drops a bare trailing dot', () => {
    expect(altFromFileName('file.')).toBe('file');
  });

  it('removes markdown-breaking characters from the alt label', () => {
    expect(altFromFileName('photo]v2.png')).toBe('photov2');
  });

  it('returns an empty string for an empty/whitespace name', () => {
    expect(altFromFileName('   ')).toBe('');
    expect(altFromFileName('')).toBe('');
  });

  it('uploads each image file and maps the returned URL onto src (alt from file name)', async () => {
    const nodes = await uploadImages(
      fileListOf(makeFile('a.png', 'image/png'), makeFile('b.jpg', 'image/jpeg')),
      fakeSchema,
      (f: File) => Promise.resolve(`https://cdn.example/${f.name}`),
    );

    expect(nodes.map(n => n.attrs['src'])).toEqual(['https://cdn.example/a.png', 'https://cdn.example/b.jpg']);
    expect(nodes[0].attrs['alt']).toBe('a');
    expect(snackBar.open).not.toHaveBeenCalled();
  });

  it('accepts an image identified by extension when the MIME type is empty or generic', async () => {
    const nodes = await uploadImages(
      fileListOf(makeFile('empty.png', ''), makeFile('generic.jpg', 'application/octet-stream')),
      fakeSchema,
      (f: File) => Promise.resolve(`https://cdn.example/${f.name}`),
    );

    expect(nodes.map(n => n.attrs['src'])).toEqual(['https://cdn.example/empty.png', 'https://cdn.example/generic.jpg']);
    expect(snackBar.open).not.toHaveBeenCalled();
  });

  it('warns when only non-image files are dropped', async () => {
    const nodes = await uploadImages(
      fileListOf(makeFile('doc.pdf', 'application/pdf')),
      fakeSchema,
      (f: File) => Promise.resolve(`https://cdn.example/${f.name}`),
    );

    expect(nodes).toEqual([]);
    expect(snackBar.open).toHaveBeenCalled();
  });

  it('ignores non-image files in the payload', async () => {
    const nodes = await uploadImages(
      fileListOf(makeFile('a.png', 'image/png'), makeFile('notes.txt', 'text/plain')),
      fakeSchema,
      (f: File) => Promise.resolve(`https://cdn.example/${f.name}`),
    );

    expect(nodes.length).toBe(1);
    expect(nodes[0].attrs['src']).toBe('https://cdn.example/a.png');
  });

  it('returns no nodes and shows a snackbar when an upload fails', async () => {
    const nodes = await uploadImages(
      fileListOf(makeFile('a.png', 'image/png')),
      fakeSchema,
      () => Promise.reject(new Error('boom')),
    );

    expect(nodes).toEqual([]);
    expect(snackBar.open).toHaveBeenCalled();
  });

  it('returns [] when the schema has no image node type', async () => {
    const nodes = await uploadImages(
      fileListOf(makeFile('a.png', 'image/png')),
      emptySchema,
      (f: File) => Promise.resolve(`https://cdn.example/${f.name}`),
    );

    expect(nodes).toEqual([]);
  });
});
