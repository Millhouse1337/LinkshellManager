import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, effect, inject, signal, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';

import { DiscordActivityService } from '../../discord/discord-activity.service';
import { PartySetupService } from '../../discord/party-setup.service';
import { PartySetupEditorComponent } from './party-setup-editor.component';
import { PartySetupPanelComponent } from './party-setup-panel.component';

// List of raid-composition setups + the selected setup's interactive tree
// (sign-up / withdraw) via the shared <app-party-setup-panel>, plus the officer
// editor (create/edit/delete/assign) gated on CanManageParties.
@Component({
  selector: 'app-party-setup-tab',
  imports: [CommonModule, FormsModule, PartySetupPanelComponent, PartySetupEditorComponent],
  templateUrl: './party-setup-tab.component.html',
  styleUrl: './party-setup-tab.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class PartySetupTabComponent {
  protected readonly activity = inject(DiscordActivityService);
  protected readonly partySetup = inject(PartySetupService);

  private readonly editor = viewChild<PartySetupEditorComponent>('editor');

  protected readonly selectedId = signal<number | null>(null);
  protected readonly confirmDeleteId = signal<number | null>(null);
  protected readonly assignDraft = signal<string>('');

  constructor() {
    effect(() => {
      const id = this.primaryLinkshellId();
      if (id) queueMicrotask(() => void this.partySetup.loadList(id));
    });
  }

  protected primaryLinkshellId(): number {
    return this.activity.overview()?.primaryLinkshell?.id ?? this.activity.overview()?.appUser?.primaryLinkshellId ?? 0;
  }

  protected list() {
    return this.partySetup.list();
  }

  protected canManage(): boolean {
    return this.list()?.canManage ?? false;
  }

  protected selectedRow() {
    const id = this.selectedId();
    return id ? this.list()?.items.find(item => item.id === id) ?? null : null;
  }

  protected select(setupId: number): void {
    this.selectedId.set(setupId);
    this.confirmDeleteId.set(null);
    this.assignDraft.set(this.list()?.items.find(item => item.id === setupId)?.assignedMonsterName ?? '');
  }

  protected clearSelection(): void {
    this.selectedId.set(null);
    this.confirmDeleteId.set(null);
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

  // ----- Officer actions -----

  protected openCreate(): void {
    this.editor()?.openForCreate();
  }

  protected openEdit(setupId: number): void {
    void this.editor()?.openForEdit(setupId);
  }

  protected onSaved(setupId: number): void {
    this.select(setupId);
  }

  protected async applyAssign(setupId: number): Promise<void> {
    const monster = this.assignDraft().trim();
    await this.partySetup.assign(setupId, monster || null, this.primaryLinkshellId());
  }

  protected async confirmDelete(setupId: number): Promise<void> {
    const ok = await this.partySetup.remove(setupId, this.primaryLinkshellId());
    if (ok) {
      this.confirmDeleteId.set(null);
      if (this.selectedId() === setupId) {
        this.clearSelection();
      }
    }
  }
}
