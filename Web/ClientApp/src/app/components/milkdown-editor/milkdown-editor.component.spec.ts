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

  const uploadImages = (
    files: FileList,
    schema: import('@milkdown/kit/prose/model').Schema,
    uploadFn: (f: File) => Promise<string>,
  ): Promise<Array<{ attrs: Record<string, unknown> }>> => priv().uploadImages(files, schema, uploadFn);

  it('uploads each image file and maps the returned URL onto src with empty alt', async () => {
    const nodes = await uploadImages(
      fileListOf(makeFile('a.png', 'image/png'), makeFile('b.jpg', 'image/jpeg')),
      fakeSchema,
      (f: File) => Promise.resolve(`https://cdn.example/${f.name}`),
    );

    expect(nodes.map(n => n.attrs['src'])).toEqual(['https://cdn.example/a.png', 'https://cdn.example/b.jpg']);
    // Alt is intentionally empty: a file name (often the blob-storage GUID) is not meaningful
    // description, and the public renderer forces alt="" for blog images.
    expect(nodes.map(n => n.attrs['alt'])).toEqual(['', '']);
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

  it('inserts supported images, skips a non-image file, and warns about the skip', async () => {
    const nodes = await uploadImages(
      fileListOf(makeFile('a.png', 'image/png'), makeFile('notes.txt', 'text/plain')),
      fakeSchema,
      (f: File) => Promise.resolve(`https://cdn.example/${f.name}`),
    );

    expect(nodes.length).toBe(1);
    expect(nodes[0].attrs['src']).toBe('https://cdn.example/a.png');
    expect(snackBar.open).toHaveBeenCalled();
  });

  it('rejects an image type the backend does not accept (by extension), even with an image MIME', async () => {
    const nodes = await uploadImages(
      fileListOf(makeFile('logo.svg', 'image/svg+xml')),
      fakeSchema,
      (f: File) => Promise.resolve(`https://cdn.example/${f.name}`),
    );

    // Not uploaded (would be rejected server-side); user is told which formats are supported.
    expect(nodes).toEqual([]);
    expect(snackBar.open).toHaveBeenCalled();
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

// Exercises the editor's rebuild lifecycle (issue #263) without mounting Crepe: createEditor is
// stubbed with a fake instance so the reconcile loop, teardown handling, and emit-echo guard can be
// driven deterministically.
describe('MilkdownEditorComponent lifecycle reconcile', () => {
  let component: MilkdownEditorComponent;

  // The lifecycle helpers under test are private; `any` is the only way to reach them without mounting
  // the component (Crepe needs a live DOM).
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  const priv = (): any => component as any;

  interface FakeCrepe {
    markdown: string;
    getMarkdown: () => string;
    destroy: jasmine.Spy;
  }

  const makeFakeCrepe = (markdown: string): FakeCrepe => {
    const crepe: FakeCrepe = {
      markdown,
      getMarkdown: () => crepe.markdown,
      destroy: jasmine.createSpy('destroy').and.returnValue(Promise.resolve()),
    };
    return crepe;
  };

  const newGate = (): { promise: Promise<void>; resolve: () => void } => {
    let resolve!: () => void;
    const promise = new Promise<void>(r => (resolve = r));
    return { promise, resolve };
  };

  let builtValues: string[];
  let builtCrepes: FakeCrepe[];
  // When set, the next createEditor call blocks on this gate before completing (simulates a build
  // still in flight). Read at await time, so clearing it makes later builds resolve immediately.
  let gate: { promise: Promise<void>; resolve: () => void } | null;
  // Number of leading createEditor calls that should reject (simulates a transient build failure).
  let failuresRemaining: number;

  beforeEach(() => {
    const blog = jasmine.createSpyObj<BlogService>('BlogService', ['uploadImage']);
    blog.uploadImage.and.returnValue(of({ url: 'https://cdn.example/img/x.png' }));
    const snackBar = jasmine.createSpyObj<MatSnackBar>('MatSnackBar', ['open']);
    const zone = new NgZone({ enableLongStackTrace: false });
    component = new MilkdownEditorComponent(zone, blog, snackBar);

    builtValues = [];
    builtCrepes = [];
    gate = null;
    failuresRemaining = 0;

    // Mirror the real createEditor's observable effects (lastAppliedValue, suppressLoadEcho, crepe,
    // ready) so the loop and echo guard behave as in production, minus the live editor.
    spyOn(priv(), 'createEditor').and.callFake(async (value: string) => {
      builtValues.push(value);
      priv().suppressLoadEcho = true;
      if (gate) {
        await gate.promise;
      }
      if (failuresRemaining > 0) {
        failuresRemaining--;
        throw new Error('simulated build failure');
      }
      const crepe = makeFakeCrepe(value);
      builtCrepes.push(crepe);
      priv().crepe = crepe;
      priv().ready = true;
      // Mirror the real createEditor: baseline the echo guard on the editor's own serialization.
      priv().lastAppliedValue = crepe.getMarkdown();
      return crepe;
    });
  });

  it('builds the initial value through the reconcile loop', async () => {
    priv().pendingValue = 'hello';
    await priv().reconcile();

    expect(builtValues).toEqual(['hello']);
    expect(priv().crepe.getMarkdown()).toBe('hello');
  });

  it('is single-flight: a change during a build does not start an overlapping build', async () => {
    const gateA = (gate = newGate());
    priv().pendingValue = 'a';
    const first = priv().reconcile(); // enters loop, blocks in createEditor('a')
    await Promise.resolve();
    expect(builtValues).toEqual(['a']);

    // A second value arrives mid-build; a concurrent reconcile must not start another build.
    priv().pendingValue = 'b';
    await priv().reconcile();
    expect(builtValues).toEqual(['a']);

    gate = null; // let the queued 'b' build resolve immediately
    gateA.resolve();
    await first;

    expect(builtValues).toEqual(['a', 'b']);
    expect(priv().crepe.getMarkdown()).toBe('b');
    expect(builtCrepes[0].destroy).toHaveBeenCalledTimes(1); // 'a' torn down once, not double
  });

  it('collapses rapid intermediate changes to the latest value', async () => {
    const gateA = (gate = newGate());
    priv().pendingValue = 'a';
    const run = priv().reconcile();
    await Promise.resolve();

    // Two more values queue during the 'a' build; only the last should be built next.
    priv().pendingValue = 'b';
    priv().pendingValue = 'c';

    gate = null;
    gateA.resolve();
    await run;

    expect(builtValues).toEqual(['a', 'c']);
  });

  it('skips a rebuild when the live editor already shows the requested value', async () => {
    priv().pendingValue = 'a';
    await priv().reconcile();
    expect(builtValues).toEqual(['a']);

    priv().pendingValue = 'a';
    await priv().reconcile();
    expect(builtValues).toEqual(['a']); // no second build
  });

  it('rebuilds when reverting to a previously-applied value the user has since edited away from', async () => {
    priv().pendingValue = 'a';
    await priv().reconcile();

    priv().crepe.markdown = 'edited by user'; // live content diverged from what we applied

    priv().pendingValue = 'a'; // parent re-binds the original value
    await priv().reconcile();

    expect(builtValues).toEqual(['a', 'a']); // rebuilt to restore 'a'
  });

  it('disposes the instance and does not double-destroy when torn down mid-build', async () => {
    const gateA = (gate = newGate());
    priv().pendingValue = 'x';
    const run = priv().reconcile();
    await Promise.resolve();

    component.ngOnDestroy(); // reconciling is true -> defers teardown to the loop

    gate = null;
    gateA.resolve();
    await run;

    expect(builtCrepes[0].destroy).toHaveBeenCalledTimes(1);
    expect(priv().crepe).toBeNull();
    expect(priv().ready).toBeFalse();
  });

  it('disposes a still-live editor when the loop exits after teardown without a rebuild', async () => {
    priv().pendingValue = 'a';
    await priv().reconcile(); // builds 'a'; this.crepe is live
    const live = priv().crepe;
    expect(live).not.toBeNull();

    // ngOnDestroy deferred teardown to the loop (reconciling was true); the loop is then re-entered
    // while destroyed, so the while-condition short-circuits and no rebuild runs - the finally must
    // still dispose the published, still-live instance.
    priv().destroyed = true;
    await priv().reconcile();

    expect(live.destroy).toHaveBeenCalledTimes(1);
    expect(priv().crepe).toBeNull();
    expect(priv().ready).toBeFalse();
  });

  it('retries a transient build failure and succeeds on the next attempt', async () => {
    spyOn(console, 'warn'); // the first-attempt retry warning is expected
    failuresRemaining = 1; // first build attempt rejects, second succeeds
    priv().pendingValue = 'a';
    await priv().reconcile();

    expect(builtValues).toEqual(['a', 'a']); // one failed attempt, then a successful retry
    expect(priv().crepe).not.toBeNull();
    expect(priv().crepe.getMarkdown()).toBe('a');
    expect(priv().ready).toBeTrue();
  });

  it('gives up after the retry budget is exhausted and logs, leaving no editor', async () => {
    const error = spyOn(console, 'error');
    spyOn(console, 'warn'); // the first-attempt retry warning is expected
    failuresRemaining = 99; // every attempt rejects
    priv().pendingValue = 'a';
    await priv().reconcile();

    expect(builtValues.length).toBe(2); // MAX_BUILD_ATTEMPTS
    expect(priv().crepe).toBeNull();
    expect(priv().ready).toBeFalse();
    expect(error).toHaveBeenCalled();
  });

  it('does not log a build error when a failed build is superseded by a newer value', async () => {
    const error = spyOn(console, 'error');
    const gateA = (gate = newGate());
    failuresRemaining = 1; // the 'a' build fails
    priv().pendingValue = 'a';
    const run = priv().reconcile();
    await Promise.resolve(); // loop enters, createEditor('a') blocks on the gate

    priv().pendingValue = 'b'; // a newer value arrives before 'a' finishes failing
    gate = null;
    gateA.resolve();
    await run;

    // 'a' failed but was superseded by 'b', which builds successfully - a self-recovering supersede,
    // not an error to alarm on.
    expect(error).not.toHaveBeenCalled();
    expect(priv().crepe.getMarkdown()).toBe('b');
  });

  it('rebuilds (rather than dropping the value) when the live view throws from getMarkdown', async () => {
    priv().pendingValue = 'a';
    await priv().reconcile();
    expect(builtValues).toEqual(['a']);

    // A view caught mid-teardown throws from getMarkdown; the skip-check must treat it as not-showing.
    builtCrepes[0].getMarkdown = () => {
      throw new Error('view destroyed');
    };

    priv().pendingValue = 'a'; // same value re-bound
    await priv().reconcile();

    expect(builtValues).toEqual(['a', 'a']); // rebuilt instead of skipping or losing the value
  });

  // Stand-in for the current editor instance; onMarkdownUpdated ignores events from any other source.
  const currentSource = (): unknown => (priv().crepe ??= {});
  // Drives onMarkdownUpdated as the CURRENT editor instance would.
  const emitFromCurrent = (markdown: string, prev: string): void =>
    priv().onMarkdownUpdated(currentSource(), markdown, prev);

  it('emits a user edit after an empty-string load even though no load echo fired', () => {
    // The exact regression #263 guards against: loading '' fires no markdownUpdated, so a one-shot
    // suppression armed by the build would otherwise stay armed and swallow the next real edit.
    priv().lastAppliedValue = '';
    priv().suppressLoadEcho = true;
    const emit = spyOn(component.valueChange, 'emit');

    emitFromCurrent('hello', '');

    expect(emit).toHaveBeenCalledOnceWith('hello');
  });

  it('swallows the load normalization echo but emits the following edit', () => {
    priv().lastAppliedValue = 'x';
    priv().suppressLoadEcho = true;
    const emit = spyOn(component.valueChange, 'emit');

    emitFromCurrent('x', 'old'); // echo equal to the loaded value
    expect(emit).not.toHaveBeenCalled();

    emitFromCurrent('x edited', 'x'); // real edit
    expect(emit).toHaveBeenCalledOnceWith('x edited');
  });

  it('absorbs multiple load echoes and only emits once the content actually differs', () => {
    priv().lastAppliedValue = 'x';
    priv().suppressLoadEcho = true;
    const emit = spyOn(component.valueChange, 'emit');

    // Several normalization passes can each fire an echo carrying the loaded value; all are absorbed.
    // (prevMarkdown differs each time so the markdown!==prevMarkdown top guard does not short-circuit.)
    emitFromCurrent('x', 'old');
    emitFromCurrent('x', 'older');
    expect(emit).not.toHaveBeenCalled();

    emitFromCurrent('x edited', 'x'); // first differing content = real edit
    expect(emit).toHaveBeenCalledOnceWith('x edited');
  });

  it('ignores a no-op update whose markdown equals prevMarkdown', () => {
    const emit = spyOn(component.valueChange, 'emit');
    emitFromCurrent('same', 'same');
    expect(emit).not.toHaveBeenCalled();
  });

  it('ignores a stale event from a superseded editor instance', () => {
    // A debounced markdownUpdated from a just-destroyed editor (plugin-listener does not cancel its
    // 200ms timer on destroy) must not be emitted as an edit of the current editor.
    priv().crepe = {}; // the current instance
    priv().ready = true;
    priv().lastAppliedValue = 'current';
    const emit = spyOn(component.valueChange, 'emit');

    priv().onMarkdownUpdated({} /* a different, superseded instance */, 'stale content', 'prev');

    expect(emit).not.toHaveBeenCalled();
  });
});
