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
import { Crepe } from '@milkdown/crepe';
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
  ) {}

  ngAfterViewInit(): void {
    this.ngZone.runOutsideAngular(() => {
      this.initEditor();
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

  private getUploadFn(): ((file: File) => Promise<string>) | undefined {
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

  private buildCrepeConfig(defaultValue: string): ConstructorParameters<typeof Crepe>[0] {
    const uploadFn = this.getUploadFn();
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

  private async initEditor(): Promise<void> {
    this.crepe = new Crepe(this.buildCrepeConfig(this.value || ''));

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

  private async recreateEditor(newValue: string): Promise<void> {
    if (this.crepe) {
      await this.crepe.destroy().catch(() => {});
    }
    this.ready = false;

    this.crepe = new Crepe(this.buildCrepeConfig(newValue));

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
    this.suppressNextEmit = false;
  }
}
