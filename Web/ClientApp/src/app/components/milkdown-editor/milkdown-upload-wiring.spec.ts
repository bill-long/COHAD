import { NgZone } from '@angular/core';
import { MatSnackBar } from '@angular/material/snack-bar';
import { of } from 'rxjs';
import { Crepe } from '@milkdown/crepe';
import { editorViewCtx } from '@milkdown/kit/core';
import { uploadConfig } from '@milkdown/kit/plugin/upload';
import { MilkdownEditorComponent } from './milkdown-editor.component';
import { BlogService } from 'src/app/services/blog.service';

// Integration tests over a REAL Crepe editor: they guard the two things that make image drop actually
// work - (1) @milkdown/plugin-upload is installed and configured with our uploader, and (2) the
// capture-phase interceptor routes a file drop to that uploader before Crepe's own drop-indicator
// plugin (ordered ahead of plugin-upload) consumes it. Without the interceptor, plugin-upload's
// handleDrop never runs and drag-and-drop silently does nothing.
describe('Milkdown image drop integration', () => {
  let component: MilkdownEditorComponent;
  let root: HTMLElement;

  beforeEach(() => {
    const blog = jasmine.createSpyObj<BlogService>('BlogService', ['uploadImage']);
    blog.uploadImage.and.returnValue(of({ url: 'https://cdn.example/x.png' }));
    const snackBar = jasmine.createSpyObj<MatSnackBar>('MatSnackBar', ['open']);
    component = new MilkdownEditorComponent(new NgZone({ enableLongStackTrace: false }), blog, snackBar);
    root = document.createElement('div');
    document.body.appendChild(root);
  });

  afterEach(() => root.remove());

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

  it('installs plugin-upload and configures our uploader + enableHtmlFileUploader', async () => {
    const crepe = new Crepe({ root, defaultValue: '' });
    const uploadFn = () => Promise.resolve('https://cdn.example/uploaded.png');
    // Cast to any to call the component's private wiring method against a hand-built editor.
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    (component as any).registerImageUploadPlugin(crepe, uploadFn);
    await crepe.create();

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

    await crepe.destroy();
  });

  it('the capture-phase drop interceptor routes a file drop to the uploader', async () => {
    const crepe = new Crepe({ root, defaultValue: 'hello' });
    const uploadFn = jasmine.createSpy('uploadFn').and.returnValue(Promise.resolve('https://cdn.example/uploaded.png'));
    // Cast to any to wire the component's private state/methods to this hand-built editor.
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const priv = component as any;
    priv.registerImageUploadPlugin(crepe, uploadFn);
    await crepe.create();
    priv.crepe = crepe;
    priv.ready = true;
    priv.container = { nativeElement: root };
    priv.imageUploader = uploadFn; // getUploadFn() returns this
    priv.installDropInterceptor();

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

    await crepe.destroy();
  });
});
