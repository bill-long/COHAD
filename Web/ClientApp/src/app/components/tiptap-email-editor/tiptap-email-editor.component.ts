import { Component, forwardRef, Input, OnDestroy, OnInit, ViewEncapsulation } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule, NG_VALUE_ACCESSOR, ControlValueAccessor } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatMenuModule } from '@angular/material/menu';
import { Editor } from '@tiptap/core';
import { StarterKit } from '@tiptap/starter-kit';
import { Underline } from '@tiptap/extension-underline';
import { TextStyle, FontSize } from '@tiptap/extension-text-style';
import { Color } from '@tiptap/extension-color';
import { FontFamily } from '@tiptap/extension-font-family';
import { TextAlign } from '@tiptap/extension-text-align';
import { Highlight } from '@tiptap/extension-highlight';
import { Image } from '@tiptap/extension-image';
import { Link } from '@tiptap/extension-link';
import { Table } from '@tiptap/extension-table';
import { TableRow } from '@tiptap/extension-table-row';
import { TableCell } from '@tiptap/extension-table-cell';
import { TableHeader } from '@tiptap/extension-table-header';
import { TiptapEditorDirective } from 'ngx-tiptap';

const FONT_SIZES = ['12px', '14px', '16px', '18px', '20px', '24px', '30px', '36px'];
const FONT_FAMILIES = ['Sans-serif', 'Serif', 'Monospace', 'Arial', 'Georgia', 'Verdana', 'Times New Roman', 'Courier New'];

const TEXT_COLORS = [
  '#000000',
  '#434343',
  '#666666',
  '#999999',
  '#cccccc',
  '#ffffff',
  '#e60000',
  '#ff9900',
  '#ffff00',
  '#008a00',
  '#0066cc',
  '#9933ff',
  '#ff0066',
  '#ff6633',
  '#ccff00',
  '#00cccc',
  '#3366ff',
  '#cc33ff',
];

const BG_COLORS = [
  'transparent',
  '#fef3cd',
  '#d4edda',
  '#cce5ff',
  '#f8d7da',
  '#e2e3e5',
  '#fff3cd',
  '#d1ecf1',
  '#f5c6cb',
  '#c3e6cb',
  '#b8daff',
  '#d6d8db',
];

@Component({
  selector: 'app-tiptap-email-editor',
  standalone: true,
  imports: [CommonModule, FormsModule, MatButtonModule, MatIconModule, MatTooltipModule, MatMenuModule, TiptapEditorDirective],
  templateUrl: './tiptap-email-editor.component.html',
  styleUrls: ['./tiptap-email-editor.component.css'],
  encapsulation: ViewEncapsulation.None,
  providers: [
    {
      provide: NG_VALUE_ACCESSOR,
      useExisting: forwardRef(() => TiptapEmailEditorComponent),
      multi: true,
    },
  ],
})
export class TiptapEmailEditorComponent implements OnInit, OnDestroy, ControlValueAccessor {
  private _readOnly = false;

  @Input()
  set readOnly(val: boolean) {
    this._readOnly = val;
    this.editor?.setEditable(!val);
  }
  get readOnly(): boolean {
    return this._readOnly;
  }

  editor!: Editor;
  value = '';

  fontSizes = FONT_SIZES;
  fontFamilies = FONT_FAMILIES;
  textColors = TEXT_COLORS;
  bgColors = BG_COLORS;

  private onChange: (val: string) => void = () => {};
  private onTouched: () => void = () => {};

  ngOnInit(): void {
    this.editor = new Editor({
      extensions: [
        StarterKit.configure({
          heading: { levels: [1, 2, 3, 4] },
        }),
        Underline,
        TextStyle,
        FontSize,
        FontFamily,
        Color,
        Highlight.configure({ multicolor: true }),
        TextAlign.configure({ types: ['heading', 'paragraph'] }),
        Image.configure({ allowBase64: true, inline: true }),
        Link.configure({ openOnClick: false, protocols: ['http', 'https', 'mailto'] }),
        Table.configure({ resizable: false }),
        TableRow,
        TableCell,
        TableHeader,
      ],
      content: this.value,
      editable: !this._readOnly,
      onUpdate: ({ editor }) => {
        const html = editor.getHTML();
        this.value = html;
        this.onChange(html);
      },
      onBlur: () => {
        this.onTouched();
      },
    });
  }

  ngOnDestroy(): void {
    this.editor?.destroy();
  }

  // ControlValueAccessor
  writeValue(val: string): void {
    this.value = val || '';
    if (this.editor && this.editor.getHTML() !== this.value) {
      this.editor.commands.setContent(this.value, { emitUpdate: false });
    }
  }

  registerOnChange(fn: (val: string) => void): void {
    this.onChange = fn;
  }

  registerOnTouched(fn: () => void): void {
    this.onTouched = fn;
  }

  setDisabledState(isDisabled: boolean): void {
    this.readOnly = isDisabled;
  }

  // Toolbar actions
  get currentFontSize(): string {
    return this.editor?.getAttributes('textStyle')?.['fontSize'] || '';
  }

  get currentFontFamily(): string {
    return this.editor?.getAttributes('textStyle')?.['fontFamily'] || '';
  }

  get currentTextColor(): string {
    return this.editor?.getAttributes('textStyle')?.['color'] || '#000000';
  }

  get currentHighlight(): string {
    return this.editor?.getAttributes('highlight')?.['color'] || 'transparent';
  }

  setFontSize(size: string): void {
    if (size) {
      this.editor.chain().focus().setFontSize(size).run();
    } else {
      this.editor.chain().focus().unsetFontSize().run();
    }
  }

  setFontFamily(family: string): void {
    if (family) {
      this.editor.chain().focus().setFontFamily(family).run();
    } else {
      this.editor.chain().focus().unsetFontFamily().run();
    }
  }

  setTextColor(color: string): void {
    this.editor.chain().focus().setColor(color).run();
  }

  setHighlight(color: string): void {
    if (color === 'transparent') {
      this.editor.chain().focus().unsetHighlight().run();
    } else {
      this.editor.chain().focus().setHighlight({ color }).run();
    }
  }

  setHeading(level: number): void {
    this.editor
      .chain()
      .focus()
      .toggleHeading({ level: level as 1 | 2 | 3 | 4 })
      .run();
  }

  clearHeading(): void {
    this.editor.chain().focus().setParagraph().run();
  }

  insertImage(): void {
    const input = document.createElement('input');
    input.type = 'file';
    input.accept = 'image/*';
    input.onchange = () => {
      const file = input.files?.[0];
      if (!file) return;
      const maxSize = 5 * 1024 * 1024; // 5 MB
      if (file.size > maxSize) {
        window.alert('Image must be under 5 MB.');
        return;
      }
      const reader = new FileReader();
      reader.onload = () => {
        const dataUri = reader.result as string;
        this.editor.chain().focus().setImage({ src: dataUri }).run();
      };
      reader.readAsDataURL(file);
    };
    input.click();
  }

  private static readonly ALLOWED_PROTOCOLS = /^(https?:|mailto:)/i;

  insertLink(): void {
    const url = window.prompt('Enter URL:');
    if (!url) return;
    const trimmed = url.trim();
    if (!TiptapEmailEditorComponent.ALLOWED_PROTOCOLS.test(trimmed)) {
      window.alert('Only http, https, and mailto links are allowed.');
      return;
    }
    this.editor.chain().focus().extendMarkRange('link').setLink({ href: trimmed }).run();
  }

  removeLink(): void {
    this.editor.chain().focus().unsetLink().run();
  }

  insertTable(): void {
    this.editor.chain().focus().insertTable({ rows: 3, cols: 3, withHeaderRow: true }).run();
  }

  onColorGridKeydown(event: KeyboardEvent): void {
    const grid = event.currentTarget as HTMLElement;
    const buttons = Array.from(grid.querySelectorAll<HTMLButtonElement>('.color-swatch'));
    const current = grid.querySelector<HTMLButtonElement>('.color-swatch:focus');
    if (!current || buttons.length === 0) return;

    const cols = 6;
    const idx = buttons.indexOf(current);
    let next = -1;

    switch (event.key) {
      case 'ArrowRight':
        next = idx + 1 < buttons.length ? idx + 1 : idx;
        break;
      case 'ArrowLeft':
        next = idx - 1 >= 0 ? idx - 1 : idx;
        break;
      case 'ArrowDown':
        next = idx + cols < buttons.length ? idx + cols : idx;
        break;
      case 'ArrowUp':
        next = idx - cols >= 0 ? idx - cols : idx;
        break;
      default:
        return;
    }

    event.preventDefault();
    buttons[next]?.focus();
  }
}
