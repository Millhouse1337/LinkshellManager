import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, ElementRef, inject, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  ActivityCreateTodInput,
  ActivityLootStructure,
  DiscordActivityService
} from '../../discord/discord-activity.service';
import {
  createEmptyTodLootRow,
  parseLocalDateTime,
  toDateTimeLocalValue
} from '../activity-home.helpers';
import {
  LONG_WINDOW_TOD_MONSTERS,
  TOD_COOLDOWN_OPTIONS,
  TOD_INTERVAL_OPTIONS,
  TOD_MONSTER_OPTIONS
} from '../activity-home.types';

// Standalone, reusable "Log ToD" form rendered in a native <dialog>. Extracted from
// TodsTabComponent so it can ALSO be opened from the Events tab's "Post ToD" button on
// an HNM signup-board card (HNM events are never started — posting a ToD is how the
// recurring board re-posts). Open with openCreate(linkshellId, prefillMonster?) or
// openEdit(tod); on submit it calls the SAME DiscordActivityService.createTod/updateTod
// the ToDs tab uses, so the overview (and the tracker list) refresh reactively — no
// extra wiring needed for the recurring-board reposter to pick the new ToD up.
@Component({
  selector: 'app-tod-form',
  imports: [CommonModule, FormsModule],
  templateUrl: './tod-form.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class TodFormComponent {
  protected readonly activity = inject(DiscordActivityService);

  // The form is rendered inside a native <dialog> opened with showModal() so it floats
  // above whichever tab embeds it. Always present in the DOM (shown on demand) so the
  // viewChild resolves immediately.
  private readonly logTodDialog = viewChild<ElementRef<HTMLDialogElement>>('logTodDialog');

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

  // "Board mode" = opened from an HNM signup board's "Post ToD" button (vs the full ToDs
  // tab form). The monster is LOCKED to the event's monster (read-only) and the screenshot
  // + loot fields are hidden — recording the kill time is all that's needed to drive the
  // board re-post. boardEventId is the HNM event whose board this ToD belongs to.
  protected boardMode = false;
  protected boardEventId: number | null = null;
  protected boardMonsterDisplay = '';

  // ----- Public API -----

  // Open the form to log a NEW ToD for the given linkshell, optionally pre-filling the
  // monster (used by the HNM board's "Post ToD" button so the officer doesn't re-pick it).
  public openCreate(linkshellId: number, prefillMonster?: string | null): void {
    this.boardMode = false;
    this.boardEventId = null;
    this.editingTodId = null;
    this.resetTodDraft(linkshellId);
    const monster = (prefillMonster ?? '').trim();
    if (monster) {
      const presets = TOD_MONSTER_OPTIONS as readonly string[];
      if (presets.includes(monster)) {
        // Sets monsterName + the matching cooldown/interval defaults + recomputes repop.
        this.onTodMonsterChange(monster);
      } else {
        this.todDraft.monsterName = 'Other';
        this.todCustomMonsterName = monster;
        this.updateTodRepopTime();
      }
    }
    this.openLogTodDialog();
  }

  // Open the form to log a ToD for an HNM signup board (the card's "Post ToD" button).
  // The monster is locked to the board's monster; screenshot/loot are hidden. boardEventId
  // ties the ToD to the event so submit can drive the board (handled server-side).
  public openForBoard(linkshellId: number, monster: string, eventId: number): void {
    this.boardMode = true;
    this.boardEventId = eventId;
    this.editingTodId = null;
    this.resetTodDraft(linkshellId);
    const m = (monster ?? '').trim();
    this.boardMonsterDisplay = m;
    const presets = TOD_MONSTER_OPTIONS as readonly string[];
    if (presets.includes(m)) {
      this.onTodMonsterChange(m);
    } else {
      this.todDraft.monsterName = 'Other';
      this.todCustomMonsterName = m;
      this.updateTodRepopTime();
    }
    this.openLogTodDialog();
  }

  // Open the board form pre-filled with the board's existing ToD (the card's "Edit ToD"
  // button). Submitting re-posts to the same board endpoint, which updates both the ToD and
  // the event's StartTime.
  public openEditForBoard(tod: any, eventId: number, monster: string): void {
    this.boardMode = true;
    this.boardEventId = eventId;
    this.boardMonsterDisplay = (monster ?? '').trim();
    this.beginEditTod(tod);
  }

  // Open the form to edit an existing ToD (used by the ToDs tab's per-row Edit buttons).
  public openEdit(tod: any): void {
    this.boardMode = false;
    this.boardEventId = null;
    this.beginEditTod(tod);
  }

  // ----- Dialog open/close -----

  private openLogTodDialog(): void {
    // Defer one tick so the dialog element is laid out before showModal().
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

  // Backdrop clicks register with target === the dialog element itself.
  protected onLogTodDialogClick(event: MouseEvent): void {
    if (event.target === this.logTodDialog()?.nativeElement) {
      this.closeLogTodDialog();
      this.cancelTodEdit();
    }
  }

  // Cancel button — wipe edit/draft state and close in one go.
  protected cancelLogTodForm(): void {
    this.cancelTodEdit();
    this.closeLogTodDialog();
  }

  // ----- Image helpers (mirror the ToDs tab so the preview resolves through Discord) -----

  protected displayImagePath(path: string | null | undefined): string | null {
    if (!path) return null;
    if (path.startsWith('/uploads/tods/')) {
      return '/api/activity/uploads/tods/' + path.substring('/uploads/tods/'.length);
    }
    return path;
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

  // ----- Linkshell-scoped reads (keyed to the draft's linkshell, not a global selection) -----

  private linkshellSettingsFor(linkshellId: number) {
    return this.activity.overview()?.linkshells?.find(l => l.id === linkshellId)?.settings ?? null;
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

  protected linkshellName(): string | null {
    return this.activity.overview()?.linkshells?.find(l => l.id === this.todDraft.linkshellId)?.name ?? null;
  }

  // Loot-winner suggestions come from the draft linkshell's roster (only the primary
  // linkshell ships a full member list in the overview, same as the ToDs tab).
  protected todCharacterNames(): string[] {
    const primary = this.activity.overview()?.primaryLinkshell;
    const members = primary && primary.id === this.todDraft.linkshellId ? (primary.members ?? []) : [];
    return [...new Set(members.map(member => member.characterName).filter(name => name.trim().length > 0))]
      .sort((left, right) => left.localeCompare(right));
  }

  // ----- Form field handlers -----

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

  // Board-mode submit: posts the ToD to the HNM board endpoint, which also moves the event
  // StartTime to the repop, wipes signups, marks the board defeated, and updates Discord.
  private async submitBoardTod(): Promise<void> {
    const eventId = this.boardEventId;
    if (!eventId) {
      return;
    }
    if (!this.todTimeLocalValue || !this.todDraft.timeLocal.trim()) {
      this.activity.actionError.set('Time of Death is required.');
      this.activity.actionMessage.set(null);
      return;
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

    try {
      await this.activity.postBoardTod(eventId, {
        timeLocal: this.todDraft.timeLocal,
        cooldown: cooldown ?? null,
        interval: interval ?? null,
        dayNumber: dayNumber ?? null,
        claim: this.todDraft.claim ?? null
      });
      this.closeLogTodDialog();
    } catch {
      // Service already exposes the action error state.
    }
  }

  protected async submitTod(): Promise<void> {
    if (this.boardMode) {
      await this.submitBoardTod();
      return;
    }

    const linkshellId = this.todDraft.linkshellId;
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

  protected beginEditTod(tod: any): void {
    const linkshellId = tod.linkshellId ?? this.todDraft.linkshellId;
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

    this.openLogTodDialog();
  }

  protected cancelTodEdit(): void {
    this.editingTodId = null;
    this.resetTodDraft();
  }

  private resetTodDraft(linkshellId: number = this.todDraft.linkshellId): void {
    this.todDraft.linkshellId = linkshellId;
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
