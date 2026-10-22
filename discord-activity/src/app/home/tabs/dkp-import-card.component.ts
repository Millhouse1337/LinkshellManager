import { DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, ElementRef, inject, input, signal, viewChild } from '@angular/core';

import { DkpImportService } from '../../discord/dkp-import.service';
import type { ActivityDkpImportPreviewRow } from '../../discord/discord-activity.types';

// The Configurations tab's "Import DKP" card, website only (ConfigurationsTabComponent keeps the
// "use the web" hint inside Discord). Choose a file → preview every row → confirm. Mirrors the
// Customize page's import card and the DkpImportPreview page it led to.
@Component({
  selector: 'app-dkp-import-card',
  imports: [DecimalPipe],
  templateUrl: './dkp-import-card.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class DkpImportCardComponent {
  readonly linkshellId = input.required<number>();

  protected readonly dkpImport = inject(DkpImportService);
  protected readonly fileName = signal<string | null>(null);
  private readonly fileInput = viewChild.required<ElementRef<HTMLInputElement>>('fileInput');
  private file: File | null = null;

  // Only .xlsx and .csv can be read; anything else never offers "Preview import".
  protected onFileChange(event: Event): void {
    const chosen = (event.target as HTMLInputElement).files?.[0] ?? null;
    this.file = chosen && /\.(xlsx|csv)$/i.test(chosen.name) ? chosen : null;
    this.fileName.set(chosen ? chosen.name : null);
  }

  protected canPreview(): boolean {
    return !!this.file && !this.dkpImport.busy();
  }

  protected preview(): void {
    if (this.file) {
      void this.dkpImport.loadPreview(this.linkshellId(), this.file);
    }
  }

  protected commit(): void {
    void this.dkpImport.commit().then(applied => {
      if (applied) {
        this.reset();
      }
    });
  }

  protected cancel(): void {
    this.dkpImport.cancel();
    this.reset();
  }

  protected kindLabel(kind: ActivityDkpImportPreviewRow['kind']): string {
    return kind === 'Create' ? 'New' : kind === 'Update' ? 'Update' : 'Skipped';
  }

  protected kindTag(kind: ActivityDkpImportPreviewRow['kind']): string {
    return kind === 'Create' ? 'success' : kind === 'Update' ? 'accent' : 'warning';
  }

  private reset(): void {
    this.file = null;
    this.fileName.set(null);
    this.fileInput().nativeElement.value = '';
  }
}
