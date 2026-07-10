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
 * ProseMirror plugin props we reach for when routing a file drop to plugin-upload's handler. Crepe's
 * drop-indicator plugin is ordered ahead of plugin-upload and consumes file drops (its handleDrop
 * returns true), so plugin-upload never runs on its own - we invoke its handler directly.
 */
interface DropHandlerProps {
  handleDrop?: (view: EditorView, event: DragEvent, slice: Slice, moved: boolean) => boolean;
  handlePaste?: unknown;
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
  /** Prevents feedback loop when we programmatically set content. */
  private suppressNextEmit = false;
  /** Capture-phase drop listener that routes file drops to plugin-upload ahead of Crepe's handlers. */
  private dropInterceptor: ((event: DragEvent) => void) | null = null;

  constructor(
    private readonly ngZone: NgZone,
    private readonly blogService: BlogService,
    private readonly snackBar: MatSnackBar,
  ) {}

  ngAfterViewInit(): void {
    this.ngZone.runOutsideAngular(() => {
      this.createEditor(this.value || '');
    });
  }

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['value'] && !changes['value'].firstChange && this.ready && this.crepe) {
      const newVal = changes['value'].currentValue ?? '';
      const current = this.crepe.getMarkdown();
      if (newVal !== current) {
        this.suppressNextEmit = true;
        // Destroy and recreate to load new markdown content
        this.ngZone.runOutsideAngular(() => {
          this.recreateEditor(newVal);
        });
      }
    }
  }

  ngOnDestroy(): void {
    if (this.dropInterceptor) {
      this.container.nativeElement.removeEventListener('drop', this.dropInterceptor, true);
      this.dropInterceptor = null;
    }
    if (this.crepe) {
      this.crepe.destroy().catch(() => {});
      this.crepe = null;
    }
    this.ready = false;
  }

  private async recreateEditor(newValue: string): Promise<void> {
    if (this.crepe) {
      await this.crepe.destroy().catch(() => {});
    }
    this.ready = false;
    await this.createEditor(newValue);
    this.suppressNextEmit = false;
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

  private async createEditor(value: string): Promise<void> {
    // Resolve the upload function once so the Crepe ImageBlock feature and the paste/drop upload plugin
    // share a single instance rather than each allocating its own default closure.
    const uploadFn = this.getUploadFn();
    this.crepe = new Crepe(this.buildCrepeConfig(value, uploadFn));
    this.registerImageUploadPlugin(this.crepe, uploadFn);

    this.crepe.on(listener => {
      listener.markdownUpdated((_ctx, markdown, prevMarkdown) => {
        if (markdown !== prevMarkdown) {
          if (this.suppressNextEmit) {
            this.suppressNextEmit = false;
            return;
          }
          this.ngZone.run(() => {
            this.valueChange.emit(markdown);
          });
        }
      });
    });

    await this.crepe.create();
    this.ready = true;

    // Installed once for the component's lifetime; the listener resolves the current view dynamically,
    // so it keeps working across editor rebuilds and re-checks whether uploads are enabled per drop.
    if (!this.dropInterceptor) {
      this.installDropInterceptor();
    }
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
    const listener = (event: DragEvent): void => {
      const files = event.dataTransfer?.files;
      if (!files || files.length === 0 || this.imageUploader === false) {
        return;
      }
      const view = this.getCurrentView();
      const handleDrop = view ? this.findUploadHandleDrop(view) : undefined;
      if (!view || !handleDrop) {
        return;
      }
      // Take over the drop before Crepe's drop-indicator consumes it.
      event.preventDefault();
      event.stopImmediatePropagation();
      handleDrop(view, event, Slice.empty, false);
    };
    this.container.nativeElement.addEventListener('drop', listener, true);
    this.dropInterceptor = listener;
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

  /** plugin-upload's drop handler, identified as the only plugin exposing both handleDrop and handlePaste. */
  private findUploadHandleDrop(view: EditorView): DropHandlerProps['handleDrop'] | undefined {
    const plugin = view.state.plugins.find(p => {
      const props = p.props as DropHandlerProps | undefined;
      return !!props?.handleDrop && !!props?.handlePaste;
    });
    return (plugin?.props as DropHandlerProps | undefined)?.handleDrop;
  }

  /**
   * Wires the official `@milkdown/plugin-upload`: it uploads a dropped image to blob storage (via our
   * uploader, {@link uploadImages}), renders an "Upload in progress" placeholder, and inserts the
   * stored URL at the drop point. No-op when uploads are disabled (`imageUploader === false`).
   *
   * The plugin's own `handleDrop` never fires, though: Crepe registers a drop-indicator plugin ahead of
   * anything we add, and it consumes file drops (its `handleDrop` returns true). {@link
   * installDropInterceptor} works around that by catching the drop in the capture phase and invoking
   * this plugin's handler directly. `enableHtmlFileUploader` is set so the handler uploads rather than
   * defers when the payload also carries `text/html`.
   *
   * Paste is a separate, still-open case (tracked follow-up): Milkdown's clipboard plugin handles a
   * `text/html` paste first and embeds the source `<img>` (an origin-scoped `blob:` URL, dead
   * elsewhere).
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
          return { src: await uploadFn(file), alt: this.altFromFileName(file.name) };
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

  /**
   * Default alt text derived from a file name: drop the trailing extension and strip characters that
   * would corrupt the serialized `![alt](url)` markdown (brackets, newlines).
   */
  private altFromFileName(fileName: string): string {
    // Strip a single trailing ".ext" (including a bare trailing dot) so an extension-only name reduces
    // to an empty label rather than keeping the raw dotted string.
    const withoutExtension = fileName.trim().replace(/\.[^.]*$/, '');
    return withoutExtension.replace(/[[\]\r\n]/g, '');
  }
}
