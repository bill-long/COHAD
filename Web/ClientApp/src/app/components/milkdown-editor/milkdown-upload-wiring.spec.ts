import { NgZone } from '@angular/core';
import { MatSnackBar } from '@angular/material/snack-bar';
import { of } from 'rxjs';
import { Crepe } from '@milkdown/crepe';
import { editorViewCtx } from '@milkdown/kit/core';
import { uploadConfig } from '@milkdown/kit/plugin/upload';
import { MilkdownEditorComponent } from './milkdown-editor.component';
import { BlogService } from 'src/app/services/blog.service';

// Integration tests over a REAL Crepe editor: they guard the two things that make image drop/paste
// actually work - (1) @milkdown/plugin-upload is installed and configured with our uploader, and (2)
// the capture-phase interceptors route a file drop/paste to that uploader before Crepe's own drop-
// indicator / clipboard plugins (ordered ahead of plugin-upload) consume it. Without the interceptors,
// plugin-upload's handleDrop/handlePaste never run and drag-and-drop / image paste silently misbehave.
describe('Milkdown image drop/paste integration', () => {
  let component: MilkdownEditorComponent;
  let root: HTMLElement;
  // Set by wireEditor (and the first test); torn down in afterEach so no test leaks a live editor.
  let activeCrepe: Crepe | null = null;

  beforeEach(() => {
    const blog = jasmine.createSpyObj<BlogService>('BlogService', ['uploadImage']);
    blog.uploadImage.and.returnValue(of({ url: 'https://cdn.example/x.png' }));
    const snackBar = jasmine.createSpyObj<MatSnackBar>('MatSnackBar', ['open']);
    component = new MilkdownEditorComponent(new NgZone({ enableLongStackTrace: false }), blog, snackBar);
    root = document.createElement('div');
    document.body.appendChild(root);
    activeCrepe = null;
  });

  afterEach(async () => {
    if (activeCrepe) {
      await activeCrepe.destroy();
      activeCrepe = null;
    }
    root.remove();
  });

  // Poll for a condition instead of sleeping a fixed duration, so the test is neither flaky on slow
  // runs nor artificially slow on fast ones. Throws on timeout so a genuine failure is diagnosable.
  async function waitUntil(condition: () => boolean, timeoutMs = 2000): Promise<void> {
    const start = Date.now();
    while (!condition()) {
      if (Date.now() - start > timeoutMs) {
        throw new Error(`waitUntil: condition not met within ${timeoutMs}ms`);
      }
      await new Promise(r => setTimeout(r, 5));
    }
  }

  // Hand-builds a REAL Crepe editor and wires it into the component's private state exactly as the
  // component does at runtime, then installs both capture-phase interceptors. Returns the upload spy so
  // a test can assert the uploader was (or was not) reached. The editor is torn down in afterEach.
  async function wireEditor(defaultValue: string): Promise<jasmine.Spy> {
    const crepe = new Crepe({ root, defaultValue });
    const uploadFn = jasmine.createSpy('uploadFn').and.returnValue(Promise.resolve('https://cdn.example/uploaded.png'));
    // Cast to any to wire the component's private state/methods to this hand-built editor.
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const priv = component as any;
    priv.registerImageUploadPlugin(crepe, uploadFn);
    await crepe.create();
    activeCrepe = crepe;
    priv.crepe = crepe;
    priv.ready = true;
    priv.container = { nativeElement: root };
    priv.imageUploader = uploadFn; // getUploadFn() returns this
    priv.installDropInterceptor();
    priv.installPasteInterceptor();
    return uploadFn;
  }

  it('installs plugin-upload and configures our uploader + enableHtmlFileUploader', async () => {
    const crepe = new Crepe({ root, defaultValue: '' });
    const uploadFn = () => Promise.resolve('https://cdn.example/uploaded.png');
    // Cast to any to call the component's private wiring method against a hand-built editor.
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    (component as any).registerImageUploadPlugin(crepe, uploadFn);
    await crepe.create();
    activeCrepe = crepe;

    const cfg = crepe.editor.ctx.get(uploadConfig.key);
    // The upload plugin's ctx slice must exist and carry OUR config, not the default uploader.
    expect(cfg).withContext('uploadConfig slice present').toBeTruthy();
    expect(cfg.enableHtmlFileUploader).withContext('enableHtmlFileUploader set').toBe(true);
    expect(typeof cfg.uploader).toBe('function');

    // plugin-upload must be installed in the ProseMirror state. Identify it specifically - as the only
    // plugin exposing BOTH handleDrop and handlePaste - so this does not pass on Crepe's drop-indicator
    // (handleDrop only) if the wiring regresses.
    const view = crepe.editor.ctx.get(editorViewCtx);
    const hasUploadPlugin = view.state.plugins.some(p => {
      const props = p.props as { handleDrop?: unknown; handlePaste?: unknown } | undefined;
      return !!props?.handleDrop && !!props?.handlePaste;
    });
    expect(hasUploadPlugin).withContext('plugin-upload (handleDrop + handlePaste) installed').toBe(true);
  });

  it('the capture-phase drop interceptor routes a file drop to the uploader', async () => {
    const uploadFn = await wireEditor('hello');

    const dt = new DataTransfer();
    dt.items.add(new File([new Uint8Array([1, 2, 3])], 'photo.png', { type: 'image/png' }));
    const dropEvent = new DragEvent('drop', { bubbles: true, cancelable: true });
    Object.defineProperty(dropEvent, 'dataTransfer', { value: dt });

    // Dispatch on the container the capture-phase listener is bound to (a capture listener fires for
    // its own target as well as descendants).
    root.dispatchEvent(dropEvent);
    await waitUntil(() => uploadFn.calls.count() > 0);

    expect(uploadFn).withContext('uploader reached via the capture-phase interceptor').toHaveBeenCalled();
    expect(dropEvent.defaultPrevented).withContext('drop taken over before Crepe').toBe(true);
  });

  it('the capture-phase paste interceptor routes an image paste to the uploader', async () => {
    const uploadFn = await wireEditor('hello');

    // A paste that carries image file bytes AND text/html - the Outlook "Copy Image" shape that
    // Milkdown's clipboard plugin would otherwise handle first, embedding a dead blob: URL.
    const dt = new DataTransfer();
    dt.items.add(new File([new Uint8Array([1, 2, 3])], 'photo.png', { type: 'image/png' }));
    dt.setData('text/html', '<img src="blob:https://outlook.office.com/dead">');
    const pasteEvent = new ClipboardEvent('paste', { bubbles: true, cancelable: true });
    Object.defineProperty(pasteEvent, 'clipboardData', { value: dt });

    // Dispatch on the container the capture-phase listener is bound to (a capture listener fires for
    // its own target as well as descendants).
    root.dispatchEvent(pasteEvent);
    await waitUntil(() => uploadFn.calls.count() > 0);

    expect(uploadFn).withContext('uploader reached via the capture-phase paste interceptor').toHaveBeenCalled();
    expect(pasteEvent.defaultPrevented).withContext('paste taken over before Crepe clipboard plugin').toBe(true);
  });

  it('the paste interceptor leaves a text-only paste (no file bytes) to normal handling', async () => {
    const uploadFn = await wireEditor('hello');

    // A blob-URL-only paste (no file bytes) is unrecoverable client-side; the interceptor must not
    // hijack it, so normal handling still runs.
    const dt = new DataTransfer();
    dt.setData('text/html', '<img src="blob:https://outlook.office.com/dead">');
    const pasteEvent = new ClipboardEvent('paste', { bubbles: true, cancelable: true });
    Object.defineProperty(pasteEvent, 'clipboardData', { value: dt });

    // The interceptor's decision is synchronous - it bails at the files.length guard before ever
    // reaching the uploader - and ProseMirror's own paste handling runs synchronously within
    // dispatchEvent too, so both outcomes are final once dispatch returns. No wait needed (a fixed
    // sleep here would be exactly the flaky anti-pattern waitUntil exists to avoid).
    root.dispatchEvent(pasteEvent);

    expect(uploadFn).withContext('uploader not reached for a bytes-free paste').not.toHaveBeenCalled();
    expect(pasteEvent.defaultPrevented).withContext('bytes-free paste left to our interceptor untouched').toBe(false);
  });
});
