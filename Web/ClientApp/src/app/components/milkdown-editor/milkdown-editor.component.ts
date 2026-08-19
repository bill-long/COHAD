import {
  AfterViewInit,
  Component,
  ElementRef,
  EventEmitter,
  Input,
  NgZone,
  OnChanges,
  OnDestroy,
  Output,
  SimpleChanges,
  ViewChild,
} from '@angular/core';
import { MatSnackBar } from '@angular/material/snack-bar';
import { Crepe } from '@milkdown/crepe';
import { editorViewCtx } from '@milkdown/kit/core';
import { upload, uploadConfig } from '@milkdown/kit/plugin/upload';
import { Slice } from '@milkdown/kit/prose/model';
import type { Node as ProseNode, Schema } from '@milkdown/kit/prose/model';
import type { EditorView } from '@milkdown/kit/prose/view';
import { BlogService } from 'src/app/services/blog.service';
import { firstValueFrom } from 'rxjs';

/**
 * ProseMirror plugin props we reach for when routing a file drop or paste to plugin-upload's handlers.
 * Crepe orders its own drop-indicator plugin (for drops) and clipboard plugin (for pastes) ahead of
 * plugin-upload, and both consume the event first (their handlers return true), so plugin-upload never
 * runs on its own - we invoke its handlers directly. plugin-upload is the only plugin exposing BOTH
 * handleDrop and handlePaste, which is how we identify it.
 */
interface UploadHandlerProps {
  handleDrop?: (view: EditorView, event: DragEvent, slice: Slice, moved: boolean) => boolean;
  handlePaste?: (view: EditorView, event: ClipboardEvent, slice: Slice) => boolean;
}

export type MilkdownImageUploader = (file: File) => Promise<string>;

@Component({
  selector: 'app-milkdown-editor',
  templateUrl: './milkdown-editor.component.html',
  styleUrls: ['./milkdown-editor.component.css'],
  standalone: false,
})
export class MilkdownEditorComponent implements AfterViewInit, OnChanges, OnDestroy {
  @ViewChild('editorContainer', { static: true }) container!: ElementRef<HTMLDivElement>;

  @Input() value = '';
  @Input() placeholder = 'Write your post…';
  @Input() ariaLabel = '';
  /** Pass a custom upload handler, or false to disable image uploads entirely. */
  @Input() imageUploader: MilkdownImageUploader | false | null = null;
  @Output() valueChange = new EventEmitter<string>();

  private crepe: Crepe | null = null;
  private ready = false;
  /** Set once the component is torn down; the reconcile loop checks it after every await so a build
   *  in flight during destroy neither continues nor leaks (and never double-destroys). */
  private destroyed = false;
  /**
   * Latest bound `value` we still need to load, or null when the live editor is already in sync. The
   * reconcile loop drains this; a change arriving mid-build is captured here rather than starting an
   * overlapping rebuild.
   */
  private pendingValue: string | null = null;
  /** True while {@link reconcile} is draining {@link pendingValue}; makes the loop single-flight. */
  private reconciling = false;
  /** The markdown most recently loaded into the editor (initial build or a rebuild). Used to
   *  recognise the programmatic-load echo in {@link onMarkdownUpdated}. */
  private lastAppliedValue = '';
  /**
   * Armed at the start of each build and cleared on the first `markdownUpdated` after it. Guards the
   * one optional post-load normalization echo from being emitted as a user edit. Unlike a bare
   * one-shot, it is paired with a content check against {@link lastAppliedValue}, so if a build
   * produces no echo at all (the common case - `markdownUpdated` never fires for a build's initial
   * content) and it lingers, it can only ever swallow an event identical to the loaded content, never
   * a real edit.
   */
  private suppressLoadEcho = false;
  /** Capture-phase drop listener that routes file drops to plugin-upload ahead of Crepe's handlers. */
  private dropInterceptor: ((event: DragEvent) => void) | null = null;
  /** Capture-phase paste listener that routes image pastes to plugin-upload ahead of Crepe's handlers. */
  private pasteInterceptor: ((event: ClipboardEvent) => void) | null = null;

  constructor(
    private readonly ngZone: NgZone,
    private readonly blogService: BlogService,
    private readonly snackBar: MatSnackBar,
  ) {}

  ngAfterViewInit(): void {
    // Seed the initial value and drive it through the same reconcile loop as later changes, so the
    // first build and any rebuild share one single-flight code path (no initial build racing a
    // reconcile). Crepe has no imperative set-content API, so loading a value means (re)building.
    this.pendingValue = this.value || '';
    this.ngZone.runOutsideAngular(() => {
      this.reconcile();
    });
  }

  ngOnChanges(changes: SimpleChanges): void {
    // Record the desired value and let the reconcile loop decide whether a rebuild is needed
    // (comparing against the live content). Capturing it here - even mid-build, when `ready` is
    // false - is what makes a change that arrives during a rebuild survive rather than be dropped.
    if (changes['value'] && !changes['value'].firstChange) {
      this.pendingValue = changes['value'].currentValue ?? '';
      this.ngZone.runOutsideAngular(() => {
        this.reconcile();
      });
    }

    // The accessible name is written straight onto the editable node rather than
    // bound in the template (see labelEditableElement), so it has to be
    // re-applied by hand when a caller changes it.
    if ((changes['ariaLabel'] || changes['placeholder']) && this.ready) {
      this.labelEditableElement();
    }
  }

  ngOnDestroy(): void {
    this.destroyed = true;
    if (this.dropInterceptor) {
      this.container.nativeElement.removeEventListener('drop', this.dropInterceptor, true);
      this.dropInterceptor = null;
    }
    if (this.pasteInterceptor) {
      this.container.nativeElement.removeEventListener('paste', this.pasteInterceptor, true);
      this.pasteInterceptor = null;
    }
    // If a build is in flight, let the reconcile loop dispose the instance it created (it observes
    // `destroyed` after its awaits, and its finally disposes any still-live instance) rather than
    // racing it here - that keeps teardown single-destroy. Otherwise we own the live instance.
    if (this.reconciling) {
      this.ready = false;
    } else {
      this.disposeCrepe();
    }
  }

  /**
   * Disposes the live editor instance, if any - the single teardown idiom shared by {@link ngOnDestroy},
   * {@link rebuild}'s mid-build cleanup, and {@link reconcile}'s finally. Clears `crepe`/`ready` before
   * destroying so a concurrent path (or a stale debounced event) sees no current instance; the destroy
   * is fire-and-forget with errors swallowed.
   */
  private disposeCrepe(): void {
    const crepe = this.crepe;
    this.crepe = null;
    this.ready = false;
    crepe?.destroy().catch(() => {});
  }

  /**
   * Single-flight loop that brings the live editor in sync with {@link pendingValue}. Only one runs at
   * a time ({@link reconciling}); a concurrent caller just leaves its value in `pendingValue` for the
   * running loop to drain, so rapid `value` changes collapse into sequential rebuilds rather than
   * overlapping ones. A rebuild is skipped when the live editor already shows the requested content,
   * and the loop stops as soon as the component is destroyed.
   */
  private async reconcile(): Promise<void> {
    if (this.reconciling) {
      return;
    }
    this.reconciling = true;
    try {
      while (!this.destroyed && this.pendingValue !== null) {
        const next = this.pendingValue;
        this.pendingValue = null;
        // Skip a rebuild only when the live editor already shows this exact content. A broken view
        // (getMarkdown throwing) is treated as not-showing, so we rebuild rather than drop the value.
        if (this.isEditorShowing(next)) {
          continue;
        }
        try {
          await this.buildWithRetry(next);
        } catch (error) {
          // A build failure that exhausts the retries must not become an unhandled rejection -
          // reconcile() is invoked fire-and-forget from the lifecycle hooks. Log it; the editor stays
          // empty (ready=false) until the component is recreated (e.g. the dialog is reopened), since
          // Angular does not re-fire ngOnChanges for an unchanged bound value.
          console.error('Milkdown editor build failed', error);
        }
      }
    } finally {
      this.reconciling = false;
      // ngOnDestroy defers disposal to this loop while it owns the reconcile (reconciling === true),
      // but the loop only disposes an instance inside a rebuild. If we exit while destroyed without a
      // rebuild having done so (e.g. the loop skipped the rebuild, or was torn down between builds),
      // the published editor would leak - and its debounced markdownUpdated could still pass the
      // source === this.crepe guard. Dispose it here so teardown is always complete.
      if (this.destroyed) {
        this.disposeCrepe();
      }
    }
  }

  /** Whether the live editor already shows `value`. A view caught mid-teardown can throw from
   *  getMarkdown(); treat that as not-showing so the caller rebuilds rather than trusting a broken
   *  view (and rather than letting the throw escape the fire-and-forget reconcile as a rejection). */
  private isEditorShowing(value: string): boolean {
    if (!this.ready || !this.crepe) {
      return false;
    }
    try {
      return this.crepe.getMarkdown() === value;
    } catch {
      return false;
    }
  }

  /** Number of times {@link buildWithRetry} attempts a build before giving up, so a transient
   *  create() failure (which Angular would not re-trigger for an unchanged bound value) is retried
   *  inline rather than leaving the editor permanently blank. */
  private static readonly MAX_BUILD_ATTEMPTS = 2;

  /**
   * Builds via {@link rebuild}, retrying a failed attempt up to {@link MAX_BUILD_ATTEMPTS} times.
   * Returns without error if the component is destroyed or a newer value has arrived mid-build - both
   * are normal supersessions the loop resolves by building the newer value, not failures to report.
   * Rethrows the last error only when every attempt genuinely fails, so the caller logs a real outage.
   */
  private async buildWithRetry(next: string): Promise<void> {
    for (let attempt = 1; ; attempt++) {
      try {
        await this.rebuild(next);
        return;
      } catch (error) {
        if (this.destroyed || this.pendingValue !== null) {
          // Superseded (or torn down): not a failure - let the loop build the newer value. Returning
          // here avoids a false "build failed" error for a self-recovering supersede.
          return;
        }
        if (attempt >= MilkdownEditorComponent.MAX_BUILD_ATTEMPTS) {
          throw error;
        }
        console.warn(`Milkdown editor build attempt ${attempt} failed; retrying`, error);
      }
    }
  }

  /**
   * Tears down the current editor (if any) and builds a fresh one loaded with `next`. Guards teardown
   * mid-build: if the component is destroyed while destroy/create is awaiting, it stops without
   * building, and disposes an instance it created after destruction so nothing leaks - `ngOnDestroy`
   * only ever sees `this.crepe` as null in that window, so neither path double-destroys.
   */
  private async rebuild(next: string): Promise<void> {
    this.ready = false;
    const previous = this.crepe;
    this.crepe = null;
    if (previous) {
      await previous.destroy().catch(() => {});
    }
    if (this.destroyed) {
      return;
    }
    await this.createEditor(next);
    if (this.destroyed) {
      // Torn down while this build was in flight: dispose the instance we just created (it is now
      // this.crepe). ngOnDestroy saw this.crepe null during that window, so this is its only destroy.
      this.disposeCrepe();
    }
  }

  private getUploadFn(): MilkdownImageUploader | undefined {
    if (this.imageUploader === false) {
      return undefined;
    }
    if (this.imageUploader) {
      return this.imageUploader;
    }
    // Default: use BlogService upload
    return async (file: File): Promise<string> => {
      const result = await firstValueFrom(this.blogService.uploadImage(file));
      return result.url;
    };
  }

  private buildCrepeConfig(
    defaultValue: string,
    uploadFn: MilkdownImageUploader | undefined,
  ): ConstructorParameters<typeof Crepe>[0] {
    return {
      root: this.container.nativeElement,
      defaultValue,
      featureConfigs: {
        ...(uploadFn
          ? {
              [Crepe.Feature.ImageBlock]: {
                onUpload: uploadFn,
                inlineOnUpload: uploadFn,
                blockOnUpload: uploadFn,
              },
            }
          : {}),
        [Crepe.Feature.Placeholder]: {
          text: this.placeholder,
        },
      },
    };
  }

  /**
   * Names the element the user actually types into. Crepe mounts a
   * contenteditable (`.ProseMirror`) inside the container, and that node - not
   * the wrapper - is what assistive tech exposes as the text box, so the label
   * has to land on it after create() rather than on the host div in the
   * template (WCAG 4.1.2).
   */
  private labelEditableElement(): void {
    const label = this.ariaLabel || this.placeholder;
    if (!label) {
      return;
    }
    const editable = this.container.nativeElement.querySelector('.ProseMirror');
    editable?.setAttribute('aria-label', label);
  }

  /** Builds a fresh Crepe editor loaded with `value`, wires its content listener, and returns the
   *  instance (also stored on {@link crepe}). The return value is used by the real-editor tests, which
   *  drive createEditor directly; {@link rebuild} disposes via {@link crepe} rather than the return. */
  private async createEditor(value: string): Promise<Crepe> {
    // Arm the echo guard before any content event can fire. Nothing this instance emits can escape
    // before it is published (onMarkdownUpdated gates on `source === this.crepe`, and this.crepe is
    // assigned only after create() below), and lastAppliedValue is baselined below from the editor's
    // own serialization - not the raw input - because Crepe may normalize the markdown on load (list
    // markers, spacing, escapes); an echo compared against the raw input would be mistaken for a user
    // edit.
    this.suppressLoadEcho = true;

    // Resolve the upload function once so the Crepe ImageBlock feature and the paste/drop upload plugin
    // share a single instance rather than each allocating its own default closure.
    const uploadFn = this.getUploadFn();
    const crepe = new Crepe(this.buildCrepeConfig(value, uploadFn));
    this.registerImageUploadPlugin(crepe, uploadFn);

    crepe.on(listener => {
      listener.markdownUpdated((_ctx, markdown, prevMarkdown) => {
        this.onMarkdownUpdated(crepe, markdown, prevMarkdown);
      });
    });

    try {
      await crepe.create();
    } catch (error) {
      // create() can partially mount DOM/ProseMirror listeners into the container before rejecting.
      // this.crepe was never assigned, so neither rebuild() nor ngOnDestroy can dispose it - do it here
      // before the error propagates, or a retry would mount a second editor on top of the orphan.
      await crepe.destroy().catch(() => {});
      throw error;
    }
    // Publish the instance only after it is built, so during the create() await this.crepe stays null
    // and a concurrent ngOnDestroy has nothing to destroy - rebuild() owns disposing this instance if
    // the component was torn down mid-build, keeping teardown single-destroy.
    this.crepe = crepe;
    this.ready = true;
    this.labelEditableElement();
    // Baseline the echo guard on the editor's normalized serialization of the loaded content, so a
    // post-load normalization echo (which carries the normalized form, not the raw input) is
    // recognised and swallowed rather than emitted as a phantom user edit. Guard getMarkdown() as
    // elsewhere: a throw here must not fail an already-mounted build - fall back to the raw input.
    try {
      this.lastAppliedValue = crepe.getMarkdown();
    } catch {
      this.lastAppliedValue = value;
    }

    // Skip when torn down mid-build: ngOnDestroy has already run and cleared the interceptor refs, so
    // re-installing here would leak capture-phase listeners it will never remove. rebuild() disposes
    // the instance we just created.
    if (this.destroyed) {
      return crepe;
    }

    // Installed once for the component's lifetime; the listeners resolve the current view dynamically,
    // so they keep working across editor rebuilds and re-check whether uploads are enabled per event.
    if (!this.dropInterceptor) {
      this.installDropInterceptor();
    }
    if (!this.pasteInterceptor) {
      this.installPasteInterceptor();
    }
    return crepe;
  }

  /**
   * Handles a `markdownUpdated` event, emitting it as a `valueChange` unless it is the programmatic
   * load's echo. `markdownUpdated` never fires for a build's initial content (the listener records it
   * silently), so the only echo to swallow is an optional post-load normalization. While
   * `suppressLoadEcho` is armed, any event whose content equals the loaded serialization
   * (`lastAppliedValue`) is treated as that echo and absorbed - however many passes Crepe emits - and
   * the flag is only cleared by the first event that *differs*, i.e. a genuine user edit. That first
   * differing event, and everything after it, is emitted; so even a build that produces no echo at all
   * (the flag lingers) still forwards the user's first real edit.
   *
   * Ignores an event from a superseded `source` instance: plugin-listener debounces markdownUpdated by
   * 200ms and does not cancel the timer on destroy, so a just-destroyed editor can still fire after a
   * rebuild. Its stale content would otherwise be emitted as a phantom edit and corrupt the two-way
   * binding. `this.crepe` is assigned only after `create()` completes (and cleared on every teardown
   * path), so this identity check also covers the pre-ready window - an echo firing mid-`create()`
   * carries the building instance while `this.crepe` is still null, and is rejected here.
   */
  private onMarkdownUpdated(source: Crepe, markdown: string, prevMarkdown: string): void {
    if (source !== this.crepe || markdown === prevMarkdown) {
      return;
    }
    if (this.suppressLoadEcho) {
      if (markdown === this.lastAppliedValue) {
        return;
      }
      // First content that differs from the loaded value is a real user edit; stop absorbing echoes.
      this.suppressLoadEcho = false;
    }
    this.ngZone.run(() => {
      this.valueChange.emit(markdown);
    });
  }

  /**
   * Routes a file drop to plugin-upload's own drop handler before ProseMirror sees the event.
   *
   * Why this is needed: Crepe's cursor feature adds a block-reorder drop-indicator plugin, ordered
   * ahead of anything we add. ProseMirror's handleDrop is first-match-wins, and that plugin's handler
   * consumes a file drop as a no-op - for an external file ProseMirror hands it an empty slice, it
   * inserts nothing, but still returns true (and calls preventDefault). So plugin-upload's handleDrop,
   * ordered after it, never runs and the drop silently does nothing. Crepe does not expose the plugin
   * order, so we catch the drop in the capture phase (ahead of ProseMirror), stop it when it carries
   * files, and hand it to plugin-upload's handler (which then uploads images and reports unsupported
   * files via {@link uploadImages}).
   */
  private installDropInterceptor(): void {
    this.dropInterceptor = this.installUploadInterceptor<DragEvent>(
      'drop',
      event => event.dataTransfer?.files,
      (props, view, event) => props.handleDrop?.(view, event, Slice.empty, false),
    );
  }

  /**
   * Routes an image paste to plugin-upload's own paste handler before ProseMirror sees the event.
   *
   * Why this is needed: Milkdown's `@milkdown/plugin-clipboard` (registered by Crepe's commonmark
   * preset, ahead of anything we add) has a handlePaste that, for any paste carrying `text/html` - as
   * "Copy Image" from Outlook Web and most rich apps do - parses the source `<img>` and inserts it,
   * returning true. That source URL is an origin-scoped `blob:` URL, dead for every other viewer once
   * the post is saved. ProseMirror's handlePaste is first-match-wins and clipboard is ordered before
   * plugin-upload, so plugin-upload's handlePaste (which would upload the pasted file bytes and insert
   * the stored URL at the caret) never runs. Crepe does not expose the plugin order, so - mirroring
   * {@link installDropInterceptor} - we catch the paste in the capture phase (ahead of ProseMirror),
   * and when it carries file bytes hand it to plugin-upload's handler, which uploads via {@link
   * uploadImages}.
   *
   * A paste that carries only a cross-origin `blob:` `<img>` and no file bytes is unrecoverable
   * client-side (the blob is scoped to the source app's origin); plugin-upload declines it (no files),
   * so we leave it to normal handling rather than intercept. When file bytes ARE present we defer the
   * text/html-vs-upload decision to plugin-upload's own `enableHtmlFileUploader` semantics (which we
   * enable), matching the drop path rather than second-guessing it here.
   */
  private installPasteInterceptor(): void {
    this.pasteInterceptor = this.installUploadInterceptor<ClipboardEvent>(
      'paste',
      event => event.clipboardData?.files,
      (props, view, event) => props.handlePaste?.(view, event, Slice.empty),
    );
  }

  /**
   * Shared capture-phase interception protocol behind {@link installDropInterceptor} and {@link
   * installPasteInterceptor}: guard on file bytes and uploads being enabled, resolve the live view,
   * find plugin-upload, and hand it the event ahead of Crepe's own handlers - taking the event over
   * (`preventDefault` + `stopImmediatePropagation`) only if plugin-upload actually handled it. Keeping
   * this in one place stops the drop and paste paths from drifting.
   *
   * The handler call is wrapped: a synchronous throw is logged and the event is left to normal
   * handling rather than escaping the listener uncaught (which would skip `preventDefault` non-
   * deterministically). Returns the listener so the caller can store it for removal on destroy.
   */
  private installUploadInterceptor<E extends Event>(
    type: 'drop' | 'paste',
    getFiles: (event: E) => FileList | undefined,
    invoke: (props: UploadHandlerProps, view: EditorView, event: E) => boolean | undefined,
  ): (event: E) => void {
    const listener = (event: E): void => {
      const files = getFiles(event);
      if (!files || files.length === 0 || this.imageUploader === false) {
        return;
      }
      const view = this.getCurrentView();
      const props = view ? this.findUploadPluginProps(view) : undefined;
      if (!view || !props) {
        return;
      }
      let handled = false;
      try {
        handled = invoke(props, view, event) === true;
      } catch (error) {
        console.error(`Milkdown ${type} upload interception failed`, error);
        return;
      }
      // Only take the event over (ahead of Crepe's handler) if plugin-upload actually handled it; if it
      // declines, leave the event alone so normal drop/paste behavior can still run.
      if (handled) {
        event.preventDefault();
        event.stopImmediatePropagation();
      }
    };
    this.container.nativeElement.addEventListener(type, listener as EventListener, true);
    return listener;
  }

  private getCurrentView(): EditorView | null {
    if (!this.ready || !this.crepe) {
      return null;
    }
    try {
      const view = this.crepe.editor.ctx.get(editorViewCtx);
      return view && !view.isDestroyed ? view : null;
    } catch {
      return null;
    }
  }

  /** plugin-upload's props, identified as the only plugin exposing both handleDrop and handlePaste. */
  private findUploadPluginProps(view: EditorView): UploadHandlerProps | undefined {
    const plugin = view.state.plugins.find(p => {
      const props = p.props as UploadHandlerProps | undefined;
      return !!props?.handleDrop && !!props?.handlePaste;
    });
    return plugin?.props as UploadHandlerProps | undefined;
  }

  /**
   * Wires the official `@milkdown/plugin-upload`: it uploads a dropped image to blob storage (via our
   * uploader, {@link uploadImages}), renders an "Upload in progress" placeholder, and inserts the
   * stored URL at the drop point. No-op when uploads are disabled (`imageUploader === false`).
   *
   * The plugin's own `handleDrop`/`handlePaste` never fire, though: Crepe registers a drop-indicator
   * plugin (for drops) and a clipboard plugin (for pastes) ahead of anything we add, and both consume
   * the event first (their handlers return true). {@link installDropInterceptor} and {@link
   * installPasteInterceptor} work around that by catching the event in the capture phase and invoking
   * this plugin's handler directly. `enableHtmlFileUploader` is set so the handler uploads rather than
   * defers when the payload also carries `text/html` (as an Outlook/rich-app image paste does).
   */
  private registerImageUploadPlugin(crepe: Crepe, uploadFn: MilkdownImageUploader | undefined): void {
    if (!uploadFn) {
      return;
    }

    crepe.editor
      .config(ctx => {
        ctx.update(uploadConfig.key, prev => ({
          ...prev,
          enableHtmlFileUploader: true,
          uploader: (files: FileList, schema: Schema) => this.uploadImages(files, schema, uploadFn),
        }));
      })
      .use(upload);
  }

  /**
   * Uploads every supported image file in a paste/drop payload concurrently and returns the resulting
   * image nodes for the plugin to insert at the placeholder position. Acceptance is keyed on the file
   * extension against the same set the backend allows, so an unsupported file is skipped client-side
   * rather than making a round-trip that always fails. Every user-facing outcome - a skipped file, a
   * failed upload, or an all-non-image drop - is surfaced with a snackbar so nothing is dropped
   * silently. (The `image` node is always present in this editor's schema; its absence would be an
   * internal misconfiguration, logged rather than shown to the user.)
   */
  private async uploadImages(files: FileList, schema: Schema, uploadFn: MilkdownImageUploader): Promise<ProseNode[]> {
    const imageType = schema.nodes['image'];
    if (!imageType) {
      console.error('Milkdown editor schema is missing the "image" node; cannot insert uploaded images.');
      return [];
    }

    // Accept by extension against the same set the backend allows (BlogController validates by
    // extension), so an unsupported file is skipped client-side instead of making a round-trip that
    // always fails.
    const supported = Array.from(files).filter(f => this.hasImageExtension(f.name));

    if (supported.length === 0) {
      // The plugin shows an "Upload in progress" placeholder for any file drop; explain why nothing
      // was inserted rather than silently removing it.
      if (files.length > 0) {
        this.notify(MilkdownEditorComponent.UNSUPPORTED_MESSAGE);
      }
      return [];
    }

    let uploadFailed = false;
    const results = await Promise.all(
      supported.map(async file => {
        try {
          // Insert with empty alt: a drag/paste file name is not meaningful description (it is often
          // the blob-storage GUID), and the public renderer forces alt="" for blog images anyway.
          return { src: await uploadFn(file), alt: '' };
        } catch (error) {
          // Don't swallow silently: surface it to the console so App Insights / the console captures
          // a real upload outage, beyond the user-facing snackbar below.
          console.error('Blog image upload failed', error);
          uploadFailed = true;
          return null;
        }
      }),
    );

    const nodes: ProseNode[] = [];
    for (const result of results) {
      if (!result) {
        continue;
      }
      const node = imageType.createAndFill({ src: result.src, alt: result.alt });
      if (node) {
        nodes.push(node);
      } else {
        uploadFailed = true;
      }
    }

    // Surface every problem independently so nothing is dropped silently: a skipped-file notice must
    // still show when an upload also fails, and its wording must not imply the images that DID insert
    // were rejected.
    const messages: string[] = [];
    if (uploadFailed) {
      messages.push('Some images could not be added. Please try again.');
    }
    if (supported.length < files.length) {
      messages.push('Some files were skipped - only PNG, JPEG, GIF, or WebP images can be added.');
    }
    if (messages.length > 0) {
      this.notify(messages.join(' '));
    }

    return nodes;
  }

  private notify(message: string): void {
    this.ngZone.run(() => {
      this.snackBar.open(message, 'Dismiss', { duration: 5000 });
    });
  }

  // Keep in sync with BlogController.AllowedImageExtensions - accepting an extension the backend
  // rejects would only produce a failed upload and a generic error snackbar.
  private static readonly IMAGE_EXTENSIONS = /\.(png|jpe?g|gif|webp)$/i;
  private static readonly UNSUPPORTED_MESSAGE = 'Only PNG, JPEG, GIF, or WebP images can be added to a post.';

  private hasImageExtension(fileName: string): boolean {
    return MilkdownEditorComponent.IMAGE_EXTENSIONS.test(fileName.trim());
  }
}
