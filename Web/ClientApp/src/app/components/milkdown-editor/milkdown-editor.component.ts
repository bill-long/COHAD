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

  private async recreateEditor(newValue: string): Promise<void> {
    if (this.crepe) {
      await this.crepe.destroy().catch(() => {});
    }
    this.ready = false;
    await this.createEditor(newValue);
    this.suppressNextEmit = false;
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
   * Uploads every image file in a paste/drop payload concurrently and returns the resulting image
   * nodes for the plugin to insert at the placeholder position. Files that fail to upload are dropped
   * and surfaced with a snackbar; non-image files are ignored.
   */
  private async uploadImages(files: FileList, schema: Schema, uploadFn: MilkdownImageUploader): Promise<ProseNode[]> {
    const imageType = schema.nodes['image'];
    if (!imageType) {
      return [];
    }

    // Trust an image MIME type or an image extension: some drag sources report an image file with an
    // empty or generic type (e.g. application/octet-stream), and the server validates the bytes anyway.
    const images = Array.from(files).filter(f => f.type.startsWith('image/') || this.hasImageExtension(f.name));
    if (images.length === 0) {
      // The plugin shows an "Upload in progress" placeholder for any file drop; if the user dropped
      // only non-image files, tell them why nothing was inserted rather than silently removing it.
      if (files.length > 0) {
        this.ngZone.run(() => {
          this.snackBar.open('Only image files can be added to a post.', 'Dismiss', { duration: 4000 });
        });
      }
      return [];
    }

    let anyFailed = false;
    const results = await Promise.all(
      images.map(async file => {
        try {
          return { src: await uploadFn(file), alt: this.altFromFileName(file.name) };
        } catch (error) {
          // Don't swallow silently: surface it to the console so App Insights / the console captures
          // a real upload outage, beyond the user-facing snackbar below.
          console.error('Blog image upload failed', error);
          anyFailed = true;
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
        anyFailed = true;
      }
    }

    if (anyFailed) {
      this.ngZone.run(() => {
        this.snackBar.open('Some images could not be added. Please try again.', 'Dismiss', { duration: 5000 });
      });
    }

    return nodes;
  }

  private static readonly IMAGE_EXTENSIONS = /\.(png|jpe?g|gif|webp|bmp|svg|avif|heic|heif)$/i;

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
