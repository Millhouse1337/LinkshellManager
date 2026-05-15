import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, effect, inject } from '@angular/core';
import { FormsModule } from '@angular/forms';

import { DiscordActivityService } from '../../discord/discord-activity.service';
import { WindowEventService } from '../../discord/window-event.service';
import type {
  ActivityWindowCombinedMember,
  ActivityWindowEvent,
  ActivityWindowSnapshot,
  ActivityWindowSnapshotEntry
} from '../../discord/discord-activity.types';

@Component({
  selector: 'app-window-events-tab',
  imports: [CommonModule, FormsModule],
  templateUrl: './window-events-tab.component.html',
  styleUrl: './window-events-tab.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class WindowEventsTabComponent {
  protected readonly activity = inject(DiscordActivityService);
  protected readonly windows = inject(WindowEventService);
  protected readonly attachNames: Record<number, string> = {};
  protected readonly renameDrafts: Record<number, string> = {};

  constructor() {
    effect(() => {
      const id = this.primaryLinkshellId();
      if (id) queueMicrotask(() => void this.windows.load(id));
    });
  }

  protected primaryLinkshellId(): number {
    return this.activity.overview()?.primaryLinkshell?.id ?? this.activity.overview()?.appUser?.primaryLinkshellId ?? 0;
  }

  protected data() {
    return this.windows.data();
  }

  protected formatDate(value?: string | null): string {
    if (!value) return '-';
    return new Intl.DateTimeFormat([], {
      month: 'short',
      day: 'numeric',
      year: 'numeric',
      hour: 'numeric',
      minute: '2-digit'
    }).format(new Date(value));
  }

  protected jobs(row: ActivityWindowSnapshotEntry | ActivityWindowCombinedMember): string {
    const main = row.mainJob ? `${row.mainJob}${row.mainJobLevel ?? ''}` : '-';
    const sub = row.subJob ? `${row.subJob}${row.subJobLevel ?? ''}` : '-';
    return `${main}/${sub}`;
  }

  protected renameDraft(event: ActivityWindowEvent): string {
    this.renameDrafts[event.id] ??= event.name ?? '';
    return this.renameDrafts[event.id];
  }

  protected setRenameDraft(eventId: number, value: string): void {
    this.renameDrafts[eventId] = value;
  }

  protected attachName(snapshot: ActivityWindowSnapshot): string {
    this.attachNames[snapshot.id] ??= snapshot.name ?? '';
    return this.attachNames[snapshot.id];
  }

  protected setAttachName(snapshotId: number, value: string): void {
    this.attachNames[snapshotId] = value;
  }

  protected async rename(event: ActivityWindowEvent): Promise<void> {
    const id = this.primaryLinkshellId();
    const name = (this.renameDrafts[event.id] ?? event.name ?? '').trim();
    if (!id || !name) return;
    await this.windows.rename(event.id, name, id);
  }

  protected async close(event: ActivityWindowEvent): Promise<void> {
    const id = this.primaryLinkshellId();
    if (id) await this.windows.close(event.id, id);
  }

  protected async reopen(event: ActivityWindowEvent): Promise<void> {
    const id = this.primaryLinkshellId();
    if (id) await this.windows.reopen(event.id, id);
  }

  protected async attachByName(snapshot: ActivityWindowSnapshot): Promise<void> {
    const id = this.primaryLinkshellId();
    const name = this.attachName(snapshot).trim();
    if (!id || !name) return;
    await this.windows.attachSnapshot(snapshot.id, id, { name });
  }

  protected async attachExisting(snapshot: ActivityWindowSnapshot, windowEventId: number): Promise<void> {
    const id = this.primaryLinkshellId();
    if (!id || !windowEventId) return;
    await this.windows.attachSnapshot(snapshot.id, id, { windowEventId });
  }

  protected async setSnapshotStatus(snapshot: ActivityWindowSnapshot, status: string): Promise<void> {
    const id = this.primaryLinkshellId();
    if (id) await this.windows.setSnapshotStatus(snapshot.id, id, status);
  }
}
