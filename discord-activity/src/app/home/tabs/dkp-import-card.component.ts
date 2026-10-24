import { DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, ElementRef, inject, input, signal, viewChild } from '@angular/core';

import { DiscordActivityService } from '../../discord/discord-activity.service';
import { DkpImportService } from '../../discord/dkp-import.service';
import { DownloadLinkService } from '../../discord/download-link.service';
import type { ActivityDkpImportPreviewRow } from '../../discord/discord-activity.types';

// One member row of the sample template.
interface SampleMember {
  name: string;
  alt1: string;
  alt2: string;
  current: number;
  biddable: number;
  total: number;
  spent: number;
}

// The members in the sample template "Download template" serves. A copy of Members in
// Services/SampleDkpTemplateWorkbook.cs, so the page can show the file before anyone opens it;
// keep the two in step.
const SAMPLE_MEMBERS: readonly SampleMember[] = [
  { name: 'Member1', alt1: 'Member1Alt1', alt2: 'Member1Alt2', current: 1250.5, biddable: 1000.5, total: 5400, spent: 4149.5 },
  { name: 'Member2', alt1: 'Member2Alt1', alt2: 'Member2Alt2', current: 875, biddable: 875, total: 3200, spent: 2325 },
  { name: 'Member3', alt1: 'Member3Alt1', alt2: 'Member3Alt2', current: 1500.25, biddable: 1500.25, total: 6000, spent: 4499.75 },
];

// The DKP tab's "Import DKP" view (/dkp/import), on the website and in Discord (the file goes up
// as base64 JSON; inside Discord the template downloads through a one-time link opened in the
// member's browser). Choose a file → preview every row → confirm. Mirrors the
// Customize page's import card and the DkpImportPreview page it led to. Laid out like the Game
// Addon tab (the shared showcase-* styles), with a picture of the sample template at the bottom.
@Component({
  selector: 'app-dkp-import-card',
  imports: [DecimalPipe],
  templateUrl: './dkp-import-card.component.html',
  styleUrl: './dkp-import-card.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class DkpImportCardComponent {
  readonly linkshellId = input.required<number>();

  // The sample template, cell for cell: its members, the summary row's totals (summed, as the file
  // does), and the five ✦ rows it leaves to fill in.
  protected readonly sampleMembers = SAMPLE_MEMBERS;
  protected readonly sampleTotals = {
    total: SAMPLE_MEMBERS.reduce((sum, m) => sum + m.total, 0),
    biddable: SAMPLE_MEMBERS.reduce((sum, m) => sum + m.biddable, 0),
    spent: SAMPLE_MEMBERS.reduce((sum, m) => sum + m.spent, 0),
  };
  protected readonly sampleBlankRows = [1, 2, 3, 4, 5];
  protected readonly sheetColumns = ['A', 'B', 'C', 'D', 'E', 'F', 'G'];

  protected readonly dkpImport = inject(DkpImportService);
  protected readonly activity = inject(DiscordActivityService);
  protected readonly downloads = inject(DownloadLinkService);
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

  // A file was picked but it isn't a kind the importer can read.
  protected fileRejected(): boolean {
    return !!this.fileName() && !this.file;
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
