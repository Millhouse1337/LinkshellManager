import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, Input, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  ActivityItem,
  ActivityItemInput,
  ActivityLinkshellRole,
  ActivityRevenueEntry,
  ActivityRevenueInput,
  DiscordActivityService
} from '../../discord/discord-activity.service';
import { ActivitySidebarPanelComponent } from '../activity-sidebar-panel.component';
import { formatAlts } from '../activity-home.helpers';

@Component({
  selector: 'app-linkshell-tab',
  imports: [CommonModule, FormsModule, ActivitySidebarPanelComponent],
  templateUrl: './linkshell-tab.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class LinkshellTabComponent {
  protected readonly activity = inject(DiscordActivityService);
  protected readonly formatAlts = formatAlts;

  // Persists across the dashboard <-> linkshell hop, since both tabs render
  // a roster search that we want to feel like the same control. Parent owns
  // the model, child binds via these getters.
  @Input({ required: true }) rosterSearchValue!: string;
  @Input({ required: true }) rosterSearchChange!: (value: string) => void;

  protected get rosterSearch(): string { return this.rosterSearchValue; }
  protected set rosterSearch(value: string) { this.rosterSearchChange(value); }

  // ----- Re-implemented small reads via this.activity -----

  protected initials(value: string | null | undefined): string {
    const name = (value ?? '').trim();
    if (!name) return '??';
    const parts = name.split(/\s+/).filter(Boolean);
    if (parts.length === 1) return parts[0].slice(0, 2).toUpperCase();
    return (parts[0][0] + parts[1][0]).toUpperCase();
  }

  protected memberAvatarClass(name?: string | null): string {
    const trimmed = (name ?? '').trim();
    if (!trimmed) return 'a';
    let hash = 0;
    for (let i = 0; i < trimmed.length; i += 1) {
      hash = (hash * 31 + trimmed.charCodeAt(i)) >>> 0;
    }
    return ['a', 'b', 'c', 'd', 'e'][hash % 5];
  }

  protected memberStatusClass(status?: string | null): string {
    const normalized = (status ?? 'Active').toLowerCase();
    if (normalized === 'active') return 'success';
    if (normalized === 'pending') return 'warning';
    return 'default';
  }

  protected primaryLinkshell() {
    return this.activity.overview()?.primaryLinkshell ?? null;
  }

  protected canManageLinkshell(linkshellId: number): boolean {
    const membership = (this.activity.overview()?.linkshells ?? []).find(link => link.id === linkshellId);
    const rank = (membership?.rank ?? '').toLowerCase();
    return rank === 'leader' || rank === 'officer';
  }

  protected selectedDashboardLinkshellId(): number {
    return (
      this.activity.overview()?.appUser?.primaryLinkshellId ??
      this.primaryLinkshell()?.id ??
      (this.activity.overview()?.linkshells ?? [])[0]?.id ??
      0
    );
  }

  protected selectedDashboardLinkshell() {
    const selectedId = this.selectedDashboardLinkshellId();
    return (this.activity.overview()?.linkshells ?? []).find(linkshell => linkshell.id === selectedId) ?? null;
  }

  protected selectedDashboardMembers() {
    const selectedId = this.selectedDashboardLinkshellId();
    if (this.primaryLinkshell()?.id !== selectedId) {
      return [];
    }

    return [...(this.primaryLinkshell()?.members ?? [])].sort((left, right) =>
      left.characterName.localeCompare(right.characterName)
    );
  }

  protected filteredDashboardMembers() {
    const term = this.rosterSearch.trim().toLowerCase();
    const members = this.selectedDashboardMembers();
    if (!term) return members;
    return members.filter(member =>
      (member.characterName ?? '').toLowerCase().includes(term) ||
      (member.rank ?? '').toLowerCase().includes(term)
    );
  }

  protected canManageSelectedDashboard(): boolean {
    return this.canManageLinkshell(this.selectedDashboardLinkshellId());
  }

  // ----- Rank editing UI (only shown in this tab) -----

  protected editingRankMemberId = signal<number | null>(null);
  protected editingRankValue = '';

  // Roles are linkshell-specific (custom roles are persisted server-side via
  // createLinkshellRole). We load on demand and cache per-linkshell so the
  // dropdown reflects the server's current set instead of a hardcoded list.
  private readonly rolesByLinkshell = signal<Record<number, ActivityLinkshellRole[]>>({});
  private readonly fallbackRoleNames = ['Leader', 'Officer', 'Member'] as const;

  // Returns the rank options for the dropdown as { id, name } pairs. While the
  // server is loading we surface the system defaults so the inline edit is
  // never empty.
  protected rankOptions(): { id: number; name: string }[] {
    const id = this.selectedDashboardLinkshellId();
    const loaded = this.rolesByLinkshell()[id];
    if (loaded && loaded.length > 0) {
      return loaded.map(role => ({ id: role.id, name: role.name }));
    }
    return this.fallbackRoleNames.map((name, index) => ({ id: -(index + 1), name }));
  }

  protected async beginEditRank(memberId: number, currentRank: string | null | undefined): Promise<void> {
    this.editingRankMemberId.set(memberId);
    this.editingRankValue = currentRank || 'Member';
    const id = this.selectedDashboardLinkshellId();
    if (id && !this.rolesByLinkshell()[id]) {
      const data = await this.activity.loadLinkshellRoles(id);
      if (data) {
        this.rolesByLinkshell.update(map => ({ ...map, [id]: data.roles }));
      }
    }
  }

  protected cancelEditRank(): void {
    this.editingRankMemberId.set(null);
    this.editingRankValue = '';
  }

  protected async saveEditRank(linkshellId: number, memberId: number): Promise<void> {
    const newRank = this.editingRankValue;
    if (!newRank) return;
    const characterName = this.selectedDashboardMembers().find(m => m.id === memberId)?.characterName ?? null;
    await this.activity.updateLinkshellMemberRole(linkshellId, memberId, newRank, characterName);
    this.editingRankMemberId.set(null);
    this.editingRankValue = '';
  }

  protected canEditRosterRank(memberAppUserId: string | null | undefined): boolean {
    if (!this.canManageSelectedDashboard()) return false;
    if (!memberAppUserId) return false;
    return memberAppUserId !== this.activity.overview()?.appUser?.id;
  }

  // ----- Inventory & revenue (formerly the "configurations tab" leftovers
  // that actually appear under the Management tab) -----

  protected configLinkshellId = signal<number | null>(null);

  protected selectedConfigLinkshellId(): number | null {
    const explicit = this.configLinkshellId();
    if (explicit !== null) return explicit;
    return (
      this.activity.overview()?.appUser?.primaryLinkshellId ??
      this.primaryLinkshell()?.id ??
      this.activity.overview()?.linkshells?.[0]?.id ??
      null
    );
  }

  protected canManageConfigLinkshell(): boolean {
    const id = this.selectedConfigLinkshellId();
    return id !== null && this.canManageLinkshell(id);
  }

  protected configItems(): ActivityItem[] {
    const id = this.selectedConfigLinkshellId();
    if (this.primaryLinkshell()?.id !== id) return [];
    return this.primaryLinkshell()?.items ?? [];
  }

  protected configRevenue(): ActivityRevenueEntry[] {
    const id = this.selectedConfigLinkshellId();
    if (this.primaryLinkshell()?.id !== id) return [];
    return this.primaryLinkshell()?.revenueEntries ?? [];
  }

  protected configIncomeTotal(): number {
    return this.configRevenue()
      .filter(entry => entry.entryType === 'Income')
      .reduce((sum, entry) => sum + (entry.value ?? 0), 0);
  }

  protected configExpenseTotal(): number {
    return this.configRevenue()
      .filter(entry => entry.entryType === 'Expense')
      .reduce((sum, entry) => sum + (entry.value ?? 0), 0);
  }

  protected configNetTotal(): number {
    return this.configIncomeTotal() - this.configExpenseTotal();
  }

  protected configTotalItemQuantity(): number {
    return this.configItems().reduce((sum, item) => sum + (item.quantity ?? 0), 0);
  }

  protected showItemForm = signal(false);
  protected itemName = '';
  protected itemType = '';
  protected itemQuantity = 1;
  protected itemNotes = '';
  protected editingItemId = signal<number | null>(null);

  protected toggleItemForm(): void {
    this.showItemForm.update(value => !value);
    if (!this.showItemForm()) {
      this.resetItemForm();
    }
  }

  protected resetItemForm(): void {
    this.itemName = '';
    this.itemType = '';
    this.itemQuantity = 1;
    this.itemNotes = '';
    this.editingItemId.set(null);
  }

  protected beginEditItem(item: ActivityItem): void {
    this.editingItemId.set(item.id);
    this.itemName = item.itemName;
    this.itemType = item.itemType ?? '';
    this.itemQuantity = item.quantity;
    this.itemNotes = item.notes ?? '';
    this.showItemForm.set(true);
  }

  protected async submitItem(): Promise<void> {
    const linkshellId = this.selectedConfigLinkshellId();
    if (!linkshellId) return;
    const name = this.itemName.trim();
    if (!name) return;
    const input: ActivityItemInput = {
      itemName: name,
      itemType: this.itemType.trim() || null,
      quantity: Math.max(0, Math.floor(this.itemQuantity || 0)),
      notes: this.itemNotes.trim() || null
    };
    try {
      const editingId = this.editingItemId();
      if (editingId !== null) {
        await this.activity.updateItem(editingId, input);
      } else {
        await this.activity.createItem(linkshellId, input);
      }
      this.resetItemForm();
      this.showItemForm.set(false);
    } catch {
      // surfaced
    }
  }

  protected async deleteItem(itemId: number): Promise<void> {
    try { await this.activity.deleteItem(itemId); } catch { /* surfaced */ }
  }

  protected showRevenueForm = signal(false);
  protected revenueType: 'Income' | 'Expense' = 'Income';
  protected revenueCategory = '';
  protected revenueValue = 0;
  protected revenueDetails = '';

  protected toggleRevenueForm(): void {
    this.showRevenueForm.update(value => !value);
    if (!this.showRevenueForm()) {
      this.resetRevenueForm();
    }
  }

  protected resetRevenueForm(): void {
    this.revenueType = 'Income';
    this.revenueCategory = '';
    this.revenueValue = 0;
    this.revenueDetails = '';
  }

  protected async submitRevenue(): Promise<void> {
    const linkshellId = this.selectedConfigLinkshellId();
    if (!linkshellId) return;
    const value = Math.max(0, Math.floor(this.revenueValue || 0));
    if (value <= 0) return;
    const input: ActivityRevenueInput = {
      entryType: this.revenueType,
      category: this.revenueCategory.trim() || null,
      value,
      details: this.revenueDetails.trim() || null,
      occurredAt: null
    };
    try {
      await this.activity.createRevenueEntry(linkshellId, input);
      this.resetRevenueForm();
      this.showRevenueForm.set(false);
    } catch {
      // surfaced
    }
  }

  protected async deleteRevenue(entryId: number): Promise<void> {
    try { await this.activity.deleteRevenueEntry(entryId); } catch { /* surfaced */ }
  }
}
