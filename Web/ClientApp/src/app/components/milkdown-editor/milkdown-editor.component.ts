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
import { upload, uploadConfig } from '@milkdown/kit/plugin/upload';
import type { Node as ProseNode, Schema } from '@milkdown/kit/prose/model';
import { BlogService } from 'src/app/services/blog.service';
import { firstValueFrom } from 'rxjs';

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
  }

  /**
   * Wires the official `@milkdown/plugin-upload` so an image file dropped into the body uploads to blob
   * storage (via {@link BlogService}) and is inserted as its stored URL, instead of having no handler
   * at all. The plugin renders an "Upload in progress" placeholder and maps the insert position through
   * any edits made while the upload is in flight. No-op when uploads are disabled
   * (`imageUploader === false`).
   *
   * Scope: this reliably fixes drag-and-drop. It does NOT fix pasting an image from an app that puts
   * `text/html` on the clipboard (e.g. Outlook), because Milkdown's own clipboard plugin - registered
   * by Crepe ahead of this one - handles the paste first and embeds the source `<img>` (an
   * origin-scoped `blob:` URL that is dead elsewhere). No ProseMirror plugin added after Crepe's can win
   * that race; making paste upload too needs an interception point ahead of the clipboard plugin,
   * tracked as a follow-up. `enableHtmlFileUploader` is set so this plugin uploads rather than defers
   * on the paths it does see.
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
