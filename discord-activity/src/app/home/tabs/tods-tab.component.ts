import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, ElementRef, Input, inject, signal, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  ActivityCreateTodInput,
  ActivityLinkshellSettings,
  ActivityLootStructure,
  DiscordActivityService
} from '../../discord/discord-activity.service';
import {
  createEmptyTodLootRow,
  formatElapsed,
  parseDate,
  parseLocalDateTime,
  toDateTimeLocalValue
} from '../activity-home.helpers';
import {
  LONG_WINDOW_TOD_MONSTERS,
  TOD_COOLDOWN_OPTIONS,
  TOD_INTERVAL_OPTIONS,
  TOD_MONSTER_OPTIONS
} from '../activity-home.types';

@Component({
  selector: 'app-tods-tab',
  imports: [CommonModule, FormsModule],
  templateUrl: './tods-tab.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class TodsTabComponent {
  protected readonly activity = inject(DiscordActivityService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly now = signal(Date.now());

  // The single-instance ToD delete confirmation modal lives in the parent so
  // it remains mounted regardless of which tab is active.
  @Input({ required: true }) deleteTodFn!: (todId: number, monsterName: string) => void;

  // The Log ToD form is rendered inside a native <dialog> opened with
  // showModal() so it floats above the rest of the tab content. Triggered
  // from the "Log ToD" button in the section header (new) or by clicking
  // Edit on an existing ToD row (re-uses the same form).
  private readonly logTodDialog = viewChild<ElementRef<HTMLDialogElement>>('logTodDialog');

  protected openLogTodForm(): void {
    this.cancelTodEdit();   // resets draft + clears editingTodId
    this.openLogTodDialog();
  }

  private openLogTodDialog(): void {
    // Defer until Angular has rendered the @if'd dialog element.
    setTimeout(() => {
      const dialog = this.logTodDialog()?.nativeElement;
      if (dialog && !dialog.open) {
        dialog.showModal();
      }
    });
  }

  protected closeLogTodDialog(): void {
    const dialog = this.logTodDialog()?.nativeElement;
    if (dialog && dialog.open) {
      dialog.close();
    }
  }

  // Native <dialog> click events fire on both the dialog box and its
  // ::backdrop. The backdrop hits register with target === the dialog
  // element itself (not a child), so a click outside the form area = close.
  protected onLogTodDialogClick(event: MouseEvent): void {
    if (event.target === this.logTodDialog()?.nativeElement) {
      this.closeLogTodDialog();
      this.cancelTodEdit();
    }
  }

  protected readonly todMonsterOptions = [...TOD_MONSTER_OPTIONS];
  protected readonly todCooldownOptions = [...TOD_COOLDOWN_OPTIONS];
  protected readonly todIntervalOptions = [...TOD_INTERVAL_OPTIONS];
  protected readonly todDraft: ActivityCreateTodInput = {
    linkshellId: 0,
    monsterName: TOD_MONSTER_OPTIONS[0],
    dayNumber: null,
    claim: true,
    timeLocal: '',
    cooldown: '22 Hour',
    interval: '10 Min',
    noLoot: true,
    lootDetails: [{ itemName: '', itemWinner: '', winningDkpSpent: null }]
  };
  protected todTimeLocalValue = '';
  protected todRepopLocalValue = '';
  protected todCustomMonsterName = '';
  protected todClaimChoice: 'Yes' | 'No' | 'NotSpecified' = 'Yes';
  protected todDayNumberNotSpecified = false;
  protected todCustomCooldownHours: number | null = null;
  protected todImagePath: string | null = null;
  protected isUploadingTodImage = false;
  protected editingTodId: number | null = null;

  public constructor() {
    const intervalId = window.setInterval(() => this.now.set(Date.now()), 1000);
    this.destroyRef.onDestroy(() => window.clearInterval(intervalId));
    this.resetTodDraft();
  }

  // ----- Re-implemented small reads -----

  protected primaryLinkshell() {
    return this.activity.overview()?.primaryLinkshell ?? null;
  }

  protected dashboardLinkshells() {
    return this.activity.overview()?.linkshells ?? [];
  }

  protected selectedDashboardLinkshellId(): number {
    return (
      this.activity.overview()?.appUser?.primaryLinkshellId ??
      this.primaryLinkshell()?.id ??
      this.dashboardLinkshells()[0]?.id ??
      0
    );
  }

  protected selectedDashboardLinkshell() {
    const selectedId = this.selectedDashboardLinkshellId();
    return this.dashboardLinkshells().find(linkshell => linkshell.id === selectedId) ?? null;
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

  protected selectedDashboardTods() {
    const selectedId = this.selectedDashboardLinkshellId();
    // Per-linkshell hidden-mob list, configured in the Customize form.
    // Names are compared case-insensitively after trimming so the user
    // doesn't have to match casing exactly when typing them in.
    const hidden = new Set(
      (this.linkshellSettingsFor(selectedId)?.hiddenTodMonsters ?? [])
        .map(name => name.trim().toLowerCase())
    );
    return [...(this.activity.overview()?.recentTods ?? [])]
      .filter(tod => tod.linkshellId === selectedId)
      .filter(tod => !hidden.has((tod.monsterName ?? '').trim().toLowerCase()))
      .sort((left, right) => {
        const leftTime = left.time ? new Date(left.time).getTime() : 0;
        const rightTime = right.time ? new Date(right.time).getTime() : 0;
        return rightTime - leftTime;
      });
  }

  private linkshellSettingsFor(linkshellId: number): ActivityLinkshellSettings | null {
    const link = this.activity.overview()?.linkshells?.find(l => l.id === linkshellId);
    return link?.settings ?? null;
  }

  protected lootStructureFor(linkshellId: number): ActivityLootStructure {
    return this.linkshellSettingsFor(linkshellId)?.lootStructure ?? 'Dkp';
  }

  protected lootInputPlaceholder(linkshellId: number): string {
    return this.lootStructureFor(linkshellId) === 'Hybrid' ? 'Deduction %' : 'DKP spent';
  }

  protected lootInputMax(linkshellId: number): number | null {
    return this.lootStructureFor(linkshellId) === 'Hybrid' ? 100 : null;
  }

  // ----- ToD list rendering helpers -----

  protected readonly expandedTodGroups = signal<Set<string>>(new Set());

  protected groupedDashboardTods(): { key: string; latest: any; history: any[] }[] {
    const groups = new Map<string, any[]>();
    for (const tod of this.selectedDashboardTods()) {
      const key = (tod.monsterName ?? '').trim().toLowerCase() || `__${tod.id}`;
      if (!groups.has(key)) groups.set(key, []);
      groups.get(key)!.push(tod);
    }
    const result = Array.from(groups.entries()).map(([key, entries]) => ({
      key,
      latest: entries[0],
      history: entries.slice(1, 10)
    }));
    // Order the Tracked Windows list by next repop ascending so the mob
    // closest to popping (or already Ready, since their repop time is in
    // the past) sits at the top. ToDs without a repop time fall to the
    // bottom rather than the top.
    result.sort((a, b) => {
      const aRepop = a.latest.repopTime ? new Date(a.latest.repopTime).getTime() : Number.POSITIVE_INFINITY;
      const bRepop = b.latest.repopTime ? new Date(b.latest.repopTime).getTime() : Number.POSITIVE_INFINITY;
      return aRepop - bRepop;
    });
    return result;
  }

  protected isTodGroupExpanded(key: string): boolean {
    return this.expandedTodGroups().has(key);
  }

  protected toggleTodGroup(key: string): void {
    const next = new Set(this.expandedTodGroups());
    if (next.has(key)) next.delete(key); else next.add(key);
    this.expandedTodGroups.set(next);
  }

  protected readonly expandedTodLoot = signal<Set<number>>(new Set());

  protected isTodLootExpanded(todId: number): boolean {
    return this.expandedTodLoot().has(todId);
  }

  protected toggleTodLoot(todId: number): void {
    const next = new Set(this.expandedTodLoot());
    if (next.has(todId)) next.delete(todId); else next.add(todId);
    this.expandedTodLoot.set(next);
  }

  protected todCountdownLabel(tod: { repopTime?: string | null }): string {
    const remainingMilliseconds = this.remainingMs(tod.repopTime);
    return remainingMilliseconds <= 0 ? 'Ready' : formatElapsed(remainingMilliseconds);
  }

  protected isTodReady(tod: { repopTime?: string | null }): boolean {
    return this.remainingMs(tod.repopTime) <= 0;
  }

  private remainingMs(targetValue?: string | null): number {
    const targetTime = parseDate(targetValue);
    if (!targetTime) {
      return 0;
    }

    return Math.max(0, targetTime - this.now());
  }

  protected deleteTod(todId: number, monsterName: string): void {
    this.deleteTodFn(todId, monsterName);
  }

  // ----- ToD form (creation / edit) -----

  protected todCharacterNames() {
    return [...new Set(this.selectedDashboardMembers().map(member => member.characterName).filter(name => name.trim().length > 0))]
      .sort((left, right) => left.localeCompare(right));
  }

  protected onTodMonsterChange(monsterName: string): void {
    this.todDraft.monsterName = monsterName;
    if (monsterName !== 'Other') {
      this.todCustomMonsterName = '';
      const usesLongWindow = LONG_WINDOW_TOD_MONSTERS.has(monsterName.trim());
      this.todDraft.cooldown = usesLongWindow ? '72 Hour' : '22 Hour';
      this.todDraft.interval = usesLongWindow ? '1 Hour' : '10 Min';
    }
    this.updateTodRepopTime();
  }

  protected isTodMonsterOther(): boolean {
    return this.todDraft.monsterName === 'Other';
  }

  protected onTodClaimChange(claim: boolean): void {
    this.todDraft.claim = claim;
    if (!claim) {
      this.todDraft.noLoot = true;
      this.todDraft.lootDetails = [createEmptyTodLootRow()];
    }
  }

  protected onTodClaimChoiceChange(value: 'Yes' | 'No' | 'NotSpecified'): void {
    this.todClaimChoice = value;
    this.onTodClaimChange(value === 'Yes');
  }

  protected onTodDayNumberNotSpecifiedChange(enabled: boolean): void {
    this.todDayNumberNotSpecified = enabled;
    if (enabled) {
      this.todDraft.dayNumber = null;
    }
  }

  protected isTodCooldownOther(): boolean {
    return this.todDraft.cooldown === 'Other';
  }

  protected onTodCustomCooldownChange(hours: number | null): void {
    this.todCustomCooldownHours = hours;
    this.updateTodRepopTime();
  }

  protected todCooldownLabelForSummary(): string {
    const cooldown = this.todDraft.cooldown;
    if (cooldown === 'Other') {
      const hours = this.todCustomCooldownHours;
      return hours && hours > 0 ? `${hours} Hour` : 'Custom';
    }
    return cooldown || '22 Hour';
  }

  private resolveCooldownHours(): number {
    if (this.todDraft.cooldown === '72 Hour') {
      return 72;
    }
    if (this.todDraft.cooldown === '2 Hour') {
      return 2;
    }
    if (this.todDraft.cooldown === '5 Min') {
      return 5 / 60;
    }
    if (this.todDraft.cooldown === 'Other') {
      return Math.max(0, this.todCustomCooldownHours ?? 0);
    }
    return 22;
  }

  protected onTodTimeLocalChange(value: string): void {
    this.todTimeLocalValue = value;
    this.updateTodRepopTime();
  }

  protected onTodNoLootChange(noLoot: boolean): void {
    this.todDraft.noLoot = noLoot;
    if (noLoot) {
      this.todDraft.lootDetails = [createEmptyTodLootRow()];
    }
  }

  protected updateTodRepopTime(): void {
    this.todDraft.timeLocal = this.todTimeLocalValue;
    if (!this.todDraft.timeLocal) {
      this.todRepopLocalValue = '';
      return;
    }

    const todLocalTime = parseLocalDateTime(this.todDraft.timeLocal);
    if (!todLocalTime) {
      this.todRepopLocalValue = '';
      return;
    }

    const cooldownHours = this.resolveCooldownHours();
    if (cooldownHours <= 0) {
      this.todRepopLocalValue = '';
      return;
    }
    todLocalTime.setHours(todLocalTime.getHours() + cooldownHours);
    this.todRepopLocalValue = toDateTimeLocalValue(todLocalTime);
  }

  protected todRepopSummary(): string {
    if (!this.todRepopLocalValue) {
      return 'Pick a date and time to calculate the next repop window.';
    }

    return this.activity.formatDateTimeWithSeconds(this.todRepopLocalValue) ?? this.todRepopLocalValue;
  }

  protected addTodLootRow(): void {
    this.todDraft.lootDetails = [...this.todDraft.lootDetails, createEmptyTodLootRow()];
  }

  protected removeTodLootRow(index: number): void {
    if (this.todDraft.lootDetails.length === 1) {
      this.todDraft.lootDetails = [createEmptyTodLootRow()];
      return;
    }

    this.todDraft.lootDetails = this.todDraft.lootDetails.filter((_, lootIndex) => lootIndex !== index);
  }

  protected async submitTod(): Promise<void> {
    const linkshellId = this.selectedDashboardLinkshellId();
    if (!linkshellId) {
      this.activity.actionError.set('Create or join a linkshell before logging ToD entries.');
      this.activity.actionMessage.set(null);
      return;
    }

    if (!this.todTimeLocalValue || !this.todDraft.timeLocal.trim()) {
      this.activity.actionError.set('Time of Death is required.');
      this.activity.actionMessage.set(null);
      return;
    }

    let monsterName = this.todDraft.monsterName;
    if (monsterName === 'Other') {
      const custom = this.todCustomMonsterName.trim();
      if (!custom) {
        this.activity.actionError.set('Enter the custom monster name.');
        this.activity.actionMessage.set(null);
        return;
      }
      monsterName = custom;
    }

    let cooldown = this.todDraft.cooldown;
    if (cooldown === 'Other') {
      const hours = this.todCustomCooldownHours;
      if (!hours || hours <= 0) {
        this.activity.actionError.set('Enter a positive cooldown in hours.');
        this.activity.actionMessage.set(null);
        return;
      }
      cooldown = `${hours} Hour`;
    }

    const dayNumber = this.todDayNumberNotSpecified ? null : this.todDraft.dayNumber;
    const interval = this.todDraft.interval === 'Not specified' ? null : this.todDraft.interval;
    const lootStructure = this.lootStructureFor(linkshellId);
    const shouldIncludeLoot = this.todDraft.claim && !this.todDraft.noLoot && lootStructure !== 'LootCouncil';
    const lootDetails = shouldIncludeLoot
      ? this.todDraft.lootDetails.map(detail => ({
          itemName: detail.itemName?.trim() || null,
          itemWinner: detail.itemWinner?.trim() || null,
          winningDkpSpent: detail.winningDkpSpent ?? null
        }))
      : [];
    if (shouldIncludeLoot && lootStructure === 'Hybrid') {
      for (const detail of lootDetails) {
        const pct = Number(detail.winningDkpSpent ?? 0);
        if (detail.itemName && (!Number.isFinite(pct) || pct < 0 || pct > 100)) {
          this.activity.actionError.set('Deduction % must be between 0 and 100 on every loot row.');
          this.activity.actionMessage.set(null);
          return;
        }
      }
    }

    try {
      if (this.editingTodId !== null) {
        await this.activity.updateTod({
          todId: this.editingTodId,
          monsterName,
          dayNumber,
          claim: this.todDraft.claim,
          timeLocal: this.todDraft.timeLocal,
          cooldown,
          interval,
          noLoot: this.todDraft.noLoot,
          lootDetails,
          imagePath: this.todImagePath
        });
      } else {
        await this.activity.createTod({
          linkshellId,
          monsterName,
          dayNumber,
          claim: this.todDraft.claim,
          timeLocal: this.todDraft.timeLocal,
          cooldown,
          interval,
          noLoot: this.todDraft.noLoot,
          lootDetails,
          imagePath: this.todImagePath
        });
      }
      this.editingTodId = null;
      this.resetTodDraft(linkshellId);
      this.closeLogTodDialog();
    } catch {
      // Service already exposes the action error state.
    }
  }

  protected async onTodImageFileChange(event: Event): Promise<void> {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    if (!file) {
      return;
    }

    this.isUploadingTodImage = true;
    try {
      const path = await this.activity.uploadTodImage(file);
      if (path) {
        this.todImagePath = path;
      }
    } finally {
      this.isUploadingTodImage = false;
      input.value = '';
    }
  }

  protected removeTodImage(): void {
    this.todImagePath = null;
  }

  protected beginEditTod(tod: any): void {
    const linkshellId = tod.linkshellId ?? this.selectedDashboardLinkshellId();
    this.editingTodId = tod.id;
    this.todDraft.linkshellId = linkshellId;
    this.todDraft.dayNumber = tod.dayNumber ?? null;
    this.todDraft.claim = !!tod.claim;
    this.todClaimChoice = tod.claim ? 'Yes' : 'No';
    this.todDayNumberNotSpecified = tod.dayNumber == null;

    const monsterName: string = tod.monsterName ?? '';
    const presets = TOD_MONSTER_OPTIONS as readonly string[];
    if (presets.includes(monsterName)) {
      this.todDraft.monsterName = monsterName;
      this.todCustomMonsterName = '';
    } else {
      this.todDraft.monsterName = 'Other';
      this.todCustomMonsterName = monsterName;
    }

    const cooldown: string = tod.cooldown ?? '22 Hour';
    const cooldownPresets = TOD_COOLDOWN_OPTIONS as readonly string[];
    if (cooldownPresets.includes(cooldown)) {
      this.todDraft.cooldown = cooldown;
      this.todCustomCooldownHours = null;
    } else {
      const match = /^\s*(\d+(?:\.\d+)?)\s*(?:hours?|hr|h)?\s*$/i.exec(cooldown);
      this.todDraft.cooldown = 'Other';
      this.todCustomCooldownHours = match ? parseFloat(match[1]) : null;
    }

    this.todDraft.interval = tod.interval || 'Not specified';
    this.todDraft.noLoot = !(tod.lootDetails && tod.lootDetails.length > 0);
    this.todDraft.lootDetails = (tod.lootDetails && tod.lootDetails.length > 0
      ? tod.lootDetails.map((loot: any) => ({
          itemName: loot.itemName ?? '',
          itemWinner: loot.itemWinner ?? '',
          winningDkpSpent: loot.winningDkpSpent ?? null
        }))
      : [createEmptyTodLootRow()]);

    if (tod.time) {
      const d = new Date(tod.time);
      if (!Number.isNaN(d.getTime())) {
        this.todTimeLocalValue = toDateTimeLocalValue(d);
      } else {
        this.todTimeLocalValue = '';
      }
    } else {
      this.todTimeLocalValue = '';
    }
    this.todDraft.timeLocal = this.todTimeLocalValue;

    this.todImagePath = tod.imagePath ?? null;
    this.updateTodRepopTime();

    // Open the modal instead of scrolling to the (no-longer-inline) form.
    this.openLogTodDialog();
  }

  protected cancelTodEdit(): void {
    this.editingTodId = null;
    this.resetTodDraft();
  }

  // Cancel button inside the dialog — wipes the edit/draft state and
  // closes the modal in one go.
  protected cancelLogTodForm(): void {
    this.cancelTodEdit();
    this.closeLogTodDialog();
  }

  private resetTodDraft(selectedLinkshellId = this.selectedDashboardLinkshellId()): void {
    this.todDraft.linkshellId = selectedLinkshellId;
    this.todDraft.monsterName = TOD_MONSTER_OPTIONS[0];
    this.todDraft.dayNumber = null;
    this.todDraft.claim = true;
    this.todDraft.timeLocal = '';
    this.todDraft.cooldown = '22 Hour';
    this.todDraft.interval = '10 Min';
    this.todDraft.noLoot = true;
    this.todDraft.lootDetails = [createEmptyTodLootRow()];
    this.todTimeLocalValue = '';
    this.todRepopLocalValue = '';
    this.todCustomMonsterName = '';
    this.todClaimChoice = 'Yes';
    this.todDayNumberNotSpecified = false;
    this.todCustomCooldownHours = null;
    this.todImagePath = null;
    this.isUploadingTodImage = false;
  }
}
