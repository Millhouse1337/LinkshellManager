import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, ChangeDetectorRef, Component, ElementRef, inject, viewChild } from '@angular/core';
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
  hasHqVariant,
  hasSpawnWindowCadence,
  HNM_COMBINED_FROM_DAY
} from '../activity-home.types';
import type { ActivityMonsterSetup } from '../../discord/discord-activity.types';

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
  private readonly cdr = inject(ChangeDetectorRef);

  // The form is rendered inside a native <dialog> opened with showModal() so it floats
  // above whichever tab embeds it. Always present in the DOM (shown on demand) so the
  // viewChild resolves immediately.
  private readonly logTodDialog = viewChild<ElementRef<HTMLDialogElement>>('logTodDialog');

  // Cooldown and interval are entered as a number plus a unit rather than picked from a fixed
  // list: each monster carries its own configured value now, and a preset list can only express
  // the handful of durations that happened to be curated.
  protected readonly todDurationUnits = ['hours', 'mins'] as const;
  protected readonly todDraft: ActivityCreateTodInput = {
    linkshellId: 0,
    monsterName: '',
    dayNumber: null,
    hq: false,
    additionalSeconds: 0,
    claim: true,
    timeLocal: '',
    noLoot: true,
    lootDetails: [{ itemName: '', itemWinner: '', winningDkpSpent: null }]
  };
  protected todTimeLocalValue = '';
  protected todRepopLocalValue = '';
  protected todMonsterSearch = '';
  protected todMonsterPickerOpen = false;
  protected todMonsterActiveIndex = -1;
  protected todCustomMonsterName = '';
  protected todClaimChoice: 'Yes' | 'No' | 'NotSpecified' = 'Yes';
  protected todDayNumberNotSpecified = false;
  protected todCooldownValue: number | null = 22;
  protected todCooldownUnit = 'hours';
  // null = "Not specified" — the interval is optional on a ToD.
  protected todIntervalValue: number | null = null;
  protected todIntervalUnit = 'mins';
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
  // "End Camp" = board mode opened from a LIVE camp's End Camp button (vs a defeated board's
  // Post/Edit ToD). Adds the pop-window + killed inputs that cap credit and drive the Manual Check In bonuses.
  protected boardEndCamp = false;
  protected boardKilledChoice: 'Yes' | 'No' = 'Yes';
  // Which window the monster popped on. End Camp pre-fills it from the live camp and uses it to
  // cap credit; the ToDs tab lets the officer type it in as history on the ToD itself.
  protected todPopWindow: number | null = null;
  // The board's day number (from the event). Shown read-only next to the locked
  // monster, and it gates the HQ toggle: a merge pair's stronger "HQ" monster only
  // appears on day 4+, so on days 1–3 there is no HQ to record and the toggle hides.
  protected boardDayNumber: number | null = null;
  // End Camp only: whether to re-post the sign-up board before the next pop, and how many hours
  // before it. Pre-filled from the event's standing Repeat-on-ToD config.
  protected boardRepost = false;
  protected boardRepostLeadHours: number | null = null;

  // ----- Public API -----

  // Open the form to log a NEW ToD for the given linkshell, optionally pre-filling the
  // monster (used by the HNM board's "Post ToD" button so the officer doesn't re-pick it).
  public openCreate(linkshellId: number, prefillMonster?: string | null): void {
    this.boardMode = false;
    this.boardEventId = null;
    this.boardDayNumber = null;
    this.boardEndCamp = false;
    this.boardRepost = false;
    this.boardRepostLeadHours = null;
    this.editingTodId = null;
    this.resetTodDraft(linkshellId);
    const monster = (prefillMonster ?? '').trim();
    if (monster) {
      // Resolve to the OPTION covering it, so a prefill of "Aspidochelone" lands on the
      // combined "Adamantoise/Aspidochelone" entry the picker actually offers.
      const option = this.todMonsterOptionFor(monster);
      if (option) {
        // Sets monsterName + the matching cooldown/interval defaults + recomputes repop.
        this.onTodMonsterChange(option);
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
  // endCampWindow, when provided, opens this as an "End Camp" for a still-live camp: it shows the
  // pop-window (pre-filled to the current window) + killed inputs. null/omitted = a plain Post ToD.
  public openForBoard(linkshellId: number, monster: string, eventId: number, dayNumber: number | null = null,
    endCampWindow: number | null = null, repeatOnTod = false, repeatLeadHours: number | null = null): void {
    this.boardMode = true;
    this.boardEventId = eventId;
    this.boardDayNumber = dayNumber;
    this.boardEndCamp = endCampWindow != null;
    this.boardKilledChoice = 'Yes';
    this.boardRepost = repeatOnTod;
    this.boardRepostLeadHours = repeatLeadHours;
    this.editingTodId = null;
    this.resetTodDraft(linkshellId);
    // After the reset (which blanks it) — End Camp opens on the camp's current window.
    this.todPopWindow = endCampWindow ?? null;
    const m = (monster ?? '').trim();
    this.boardMonsterDisplay = m;
    const boardOption = this.todMonsterOptionFor(m);
    if (boardOption) {
      this.onTodMonsterChange(boardOption);
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
  public openEditForBoard(tod: any, eventId: number, monster: string, dayNumber: number | null = null,
    repeatOnTod = false, repeatLeadHours: number | null = null): void {
    this.boardMode = true;
    this.boardEventId = eventId;
    this.boardDayNumber = dayNumber;
    this.boardEndCamp = false;
    this.boardRepost = repeatOnTod;
    this.boardRepostLeadHours = repeatLeadHours;
    this.boardMonsterDisplay = (monster ?? '').trim();
    this.beginEditTod(tod);
    // Days 1–3 have no HQ variant, so never carry a stale HQ=true into the form.
    if (!this.hqOptionVisible()) {
      this.todDraft.hq = false;
    }
  }

  // Open the form to edit an existing ToD (used by the ToDs tab's per-row Edit buttons).
  public openEdit(tod: any): void {
    this.boardMode = false;
    this.boardEventId = null;
    this.boardDayNumber = null;
    this.boardEndCamp = false;
    this.boardRepost = false;
    this.boardRepostLeadHours = null;
    this.beginEditTod(tod);
  }

  // HQ = the merge pair's stronger half popped instead of the base. Whenever the form's monster
  // HAS a stronger half the toggle is offered, whatever the day: the officer is recording what
  // actually showed up, and the day number only governs how the board PRINTS the name. For a
  // monster with no known pair we fall back to the day rule (1–3 = base only, nothing to log).
  protected hqOptionVisible(): boolean {
    if (this.monsterHasHqVariant()) {
      return true;
    }
    return !(this.boardDayNumber != null && this.boardDayNumber < HNM_COMBINED_FROM_DAY);
  }

  // "Popped on window N" only means something when the monster HAS windows: the long band's 25
  // and the short band's 7. The Sky NMs, Shikigami Weapon, Bloodsucker, Xolotl, King Vinegarroon and
  // the other untimed NMs pop off a plain repop timer with no grid to number, so the field is
  // hidden for them rather than collecting a figure nothing can interpret.
  //
  // Board mode reads the locked display name; the ToDs tab reads the picked monster -- same
  // split as monsterHasHqVariant, for the same reason.
  protected popWindowVisible(): boolean {
    const raw = (this.boardMode ? this.boardMonsterDisplay : this.todDraft.monsterName) ?? '';
    return hasSpawnWindowCadence(raw);
  }

  // Day counts a monster's POP CYCLE, and only the three NQ/HQ families have one (mirrors
  // HnmConfig.HnmDayCycles: Nidhogg, King Behemoth, Aspidochelone). Asking for a day on a
  // Simurgh or a Sky god invited a number that means nothing and that the board would then
  // print. Same predicate as the HQ toggle beside it -- both questions are only askable of a
  // monster that has two halves.
  protected dayNumberVisible(): boolean {
    return this.monsterHasHqVariant();
  }

  // Is the form's monster part of a merge pair? Board mode reads the locked display name (which
  // may be the combined "Base/Stronger" the event stores); the ToDs tab reads the picked monster.
  private monsterHasHqVariant(): boolean {
    const raw = (this.boardMode ? this.boardMonsterDisplay : this.todDraft.monsterName) ?? '';
    return hasHqVariant(raw);
  }

  // ----- Dialog open/close -----

  private openLogTodDialog(): void {
    // The public open*() methods are invoked imperatively by a parent (via viewChild),
    // which does NOT mark this OnPush component dirty. Without this the view keeps the
    // stale pre-open state — e.g. boardMode still false, so the Monster <select> (default
    // "Adamantoise") renders instead of the read-only board input — until an unrelated
    // event inside the form forces a check. markForCheck() makes the new state render the
    // moment the dialog appears.
    this.cdr.markForCheck();
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
      // The linkshell's rows are stored under the MERGED label, which is exactly what the picker
      // offers, so this is a straight lookup — no per-half matching, and no built-in fallback
      // table on the client: the server sends a complete catalog with every overview.
      const configured = this.monsterSetupFor(monsterName);
      if (configured) {
        this.applyCooldownMinutes(configured.cooldownMinutes);
        this.applyIntervalMinutes(configured.cadenceMinutes);
      }
    }
    this.updateTodRepopTime();
  }

  private monsterSetupFor(monsterName: string): ActivityMonsterSetup | null {
    const wanted = (monsterName ?? '').trim();
    if (!wanted) return null;
    return this.linkshellSettingsFor(this.todDraft.linkshellId)?.monsterSetups
      ?.find(setup => setup.monsterName.localeCompare(wanted, undefined, { sensitivity: 'accent' }) === 0)
      ?? null;
  }

  // Mirrors the server's TodDurationFormat.Split, so a configured "22 Hour" shows as 22 hours and
  // never as 1320 mins.
  private applyCooldownMinutes(minutes: number): void {
    const safe = Math.max(1, Math.round(Number(minutes) || 0));
    const whole = safe % 60 === 0;
    this.todCooldownValue = whole ? safe / 60 : safe;
    this.todCooldownUnit = whole ? 'hours' : 'mins';
  }

  private applyIntervalMinutes(minutes: number | null): void {
    if (minutes === null || !(Number(minutes) > 0)) {
      this.todIntervalValue = null;
      this.todIntervalUnit = 'mins';
      return;
    }
    const safe = Math.round(Number(minutes));
    const whole = safe % 60 === 0;
    this.todIntervalValue = whole ? safe / 60 : safe;
    this.todIntervalUnit = whole ? 'hours' : 'mins';
  }

  // The label form the server stores and every ToD surface prints. Matches
  // TodDurationFormat.Format so the two clients can't write the same duration two ways.
  private formatDurationLabel(value: number | null, unit: string): string | null {
    const amount = Number(value);
    if (!Number.isFinite(amount) || amount <= 0) return null;
    return unit === 'hours' ? `${amount} Hour` : `${amount} Min`;
  }

  protected todCooldownLabel(): string | null {
    return this.formatDurationLabel(this.todCooldownValue, this.todCooldownUnit);
  }

  protected todIntervalLabel(): string | null {
    return this.formatDurationLabel(this.todIntervalValue, this.todIntervalUnit);
  }

  protected onTodCooldownChange(): void {
    this.updateTodRepopTime();
  }

  // Reads a stored "22 Hour" / "45 Min" / "1 Hour 30 Min" label back into the number + unit the
  // form binds to. A bare number means hours for a cooldown and minutes for an interval, matching
  // the server's TodDurationFormat.
  private applyDurationLabel(label: string | null, field: 'cooldown' | 'interval'): void {
    const minutes = this.parseDurationMinutes(label, field === 'cooldown' ? 'hours' : 'mins');
    if (field === 'cooldown') {
      this.applyCooldownMinutes(minutes ?? 22 * 60);
    } else {
      this.applyIntervalMinutes(minutes);
    }
  }

  private parseDurationMinutes(label: string | null, bareUnit: string): number | null {
    const text = (label ?? '').trim();
    if (!text) return null;

    const bare = /^\s*(\d+(?:\.\d+)?)\s*$/.exec(text);
    if (bare) {
      const amount = parseFloat(bare[1]);
      return amount > 0 ? Math.round(bareUnit === 'hours' ? amount * 60 : amount) : null;
    }

    const pair = /^\s*(?:(\d+(?:\.\d+)?)\s*(?:hours?|hrs?|h)\b)?\s*(?:(\d+(?:\.\d+)?)\s*(?:minutes?|mins?|m)\b)?\s*$/i.exec(text);
    if (!pair || (!pair[1] && !pair[2])) return null;
    const total = (pair[1] ? parseFloat(pair[1]) * 60 : 0) + (pair[2] ? parseFloat(pair[2]) : 0);
    return total > 0 ? Math.round(total) : null;
  }

  // "Other" is EXEMPT from the search filter and always sits last. The whole point of it is the
  // monster that isn't in the list, so the moment someone types a name the catalog doesn't have —
  // a Sky NM, a ground NM the linkshell never configured — the filter would otherwise empty the
  // menu and leave no way to log it at all.
  protected filteredTodMonsterOptions(): string[] {
    const search = this.todMonsterSearch.trim().toLocaleLowerCase();
    return [
      ...this.todConfiguredMonsters().filter(monster => monster.toLocaleLowerCase().includes(search)),
      'Other',
    ];
  }

  // The linkshell's configured monsters, in editor order. The three merge pairs are offered as ONE
  // combined "Base/Stronger" entry, not as six separate halves — the same list the create-event
  // monster dropdown shows, and the same form the sign-up board stores. Which half actually popped
  // is the HQ toggle's question, which is exactly why that toggle sits beside this picker.
  private todConfiguredMonsters(): string[] {
    const configured = this.linkshellSettingsFor(this.todDraft.linkshellId)?.monsterSetups ?? [];
    return configured.map(setup => setup.monsterName.trim()).filter(Boolean);
  }

  // The option that COVERS a stored monster name, i.e. the one whose name it is or whose
  // combined label contains it. A ToD saved before the pairs were combined holds a bare half
  // ("Aspidochelone"); without this it would no longer match any option and editing it would
  // drop into the free-text "Other" branch, quietly turning a curated monster into a custom one.
  //
  // Searches the CONFIGURED monsters only — matching the "Other" sentinel here would answer
  // "yes, that's a real monster" for the one entry that means the opposite.
  private todMonsterOptionFor(monsterName: string): string | null {
    const wanted = (monsterName ?? '').trim();
    if (!wanted) { return null; }
    return this.todConfiguredMonsters().find(option =>
      option.localeCompare(wanted, undefined, { sensitivity: 'accent' }) === 0
      || option.split('/').some(half =>
        half.trim().localeCompare(wanted, undefined, { sensitivity: 'accent' }) === 0)) ?? null;
  }

  private isKnownTodMonster(monsterName: string): boolean {
    return this.todMonsterOptionFor(monsterName) !== null;
  }

  protected onTodMonsterSelectionChange(monsterName: string): void {
    this.todMonsterSearch = monsterName;
    this.todMonsterPickerOpen = false;
    this.todMonsterActiveIndex = -1;
    this.onTodMonsterChange(monsterName);
  }

  protected openTodMonsterPicker(): void {
    if (!this.todMonsterPickerOpen) {
      this.todMonsterSearch = '';
      this.todMonsterPickerOpen = true;
      this.todMonsterActiveIndex = 0;
    }
  }

  protected onTodMonsterSearchChange(value: string): void {
    this.todMonsterSearch = value;
    this.todMonsterPickerOpen = true;
    this.todMonsterActiveIndex = 0;
  }

  protected closeTodMonsterPicker(): void {
    setTimeout(() => {
      this.todMonsterPickerOpen = false;
      this.todMonsterActiveIndex = -1;
      this.todMonsterSearch = this.todDraft.monsterName;
      this.cdr.markForCheck();
    });
  }

  protected onTodMonsterPickerKeydown(event: KeyboardEvent): void {
    const options = this.filteredTodMonsterOptions();
    if (event.key === 'ArrowDown') {
      event.preventDefault();
      this.todMonsterPickerOpen = true;
      this.todMonsterActiveIndex = Math.min(this.todMonsterActiveIndex + 1, options.length - 1);
    } else if (event.key === 'ArrowUp') {
      event.preventDefault();
      this.todMonsterPickerOpen = true;
      this.todMonsterActiveIndex = Math.max(this.todMonsterActiveIndex - 1, 0);
    } else if (event.key === 'Enter' && this.todMonsterPickerOpen && options[this.todMonsterActiveIndex]) {
      event.preventDefault();
      this.onTodMonsterSelectionChange(options[this.todMonsterActiveIndex]);
    } else if (event.key === 'Escape') {
      event.preventDefault();
      this.todMonsterPickerOpen = false;
      this.todMonsterActiveIndex = -1;
      this.todMonsterSearch = this.todDraft.monsterName;
    }
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

  // The ToDs-tab "Day" input. Blank (or 0/negative) = the officer didn't record a day, which is
  // what todDayNumberNotSpecified means to submit.
  protected onTodDayNumberChange(value: number | null): void {
    const day = Number(value);
    const normalized = Number.isFinite(day) && day > 0 ? Math.floor(day) : null;
    this.todDraft.dayNumber = normalized;
    this.todDayNumberNotSpecified = normalized == null;
  }

  // The "Popped on window" input on both forms. Windows are 1-based; blank = not recorded.
  protected onTodPopWindowChange(value: number | null): void {
    const window = Number(value);
    this.todPopWindow = Number.isFinite(window) && window > 0 ? Math.floor(window) : null;
  }

  private resolveCooldownHours(): number {
    const amount = Number(this.todCooldownValue);
    if (!Number.isFinite(amount) || amount <= 0) return 0;
    return this.todCooldownUnit === 'hours' ? amount : amount / 60;
  }

  protected onTodTimeLocalChange(value: string): void {
    this.todTimeLocalValue = value;
    this.updateTodRepopTime();
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
    // Fine repop offset: add the "Additional seconds" the officer entered.
    const extraSeconds = Math.max(0, Math.floor(Number(this.todDraft.additionalSeconds) || 0));
    if (extraSeconds > 0) {
      todLocalTime.setSeconds(todLocalTime.getSeconds() + extraSeconds);
    }
    this.todRepopLocalValue = toDateTimeLocalValue(todLocalTime);
  }

  // The "Additional seconds" input changed → normalize + recompute the repop preview.
  protected onTodAdditionalSecondsChange(value: number | null): void {
    this.todDraft.additionalSeconds = Math.max(0, Math.floor(Number(value) || 0));
    this.updateTodRepopTime();
  }

  protected todRepopSummary(): string {
    if (!this.todRepopLocalValue) {
      return 'Pick a date and time to calculate the next repop window.';
    }

    return this.activity.formatDateTimeWithSeconds(this.todRepopLocalValue) ?? this.todRepopLocalValue;
  }

  // Board-mode submit: posts the ToD to the HNM board endpoint, which also moves the event
  // StartTime to the repop, wipes signups, marks the board defeated, and updates Discord.
  private async submitBoardTod(): Promise<void> {
    const eventId = this.boardEventId;
    if (!eventId) {
      return;
    }
    // End Camp may be submitted with no ToD: the window closed, or another linkshell took it, so
    // nobody saw it die. That records "Not entered" (no time, no repop) instead of stamping the
    // moment the officer closed the board. Plain Post/Edit ToD still requires a real time — it
    // exists precisely to record one.
    const hasTod = !!this.todTimeLocalValue && !!this.todDraft.timeLocal.trim();
    if (!hasTod && !this.boardEndCamp) {
      this.activity.actionError.set('Time of Death is required.');
      this.activity.actionMessage.set(null);
      return;
    }
    const cooldown = this.todCooldownLabel();
    if (!cooldown) {
      this.activity.actionError.set('Enter a positive cooldown.');
      this.activity.actionMessage.set(null);
      return;
    }
    const dayNumber = this.todDayNumberNotSpecified ? null : this.todDraft.dayNumber;
    const interval = this.todIntervalLabel();

    try {
      await this.activity.postBoardTod(eventId, {
        // Blank = "Not entered"; the server leaves Time + RepopTime unrecorded.
        timeLocal: hasTod ? this.todDraft.timeLocal : '',
        cooldown: cooldown ?? null,
        interval: interval ?? null,
        dayNumber: dayNumber ?? null,
        claim: this.todDraft.claim ?? null,
        hq: this.todDraft.hq,
        additionalSeconds: this.todDraft.additionalSeconds,
        // End Camp only: cap credit at the pop window + record whether it was killed.
        popWindow: this.boardEndCamp ? this.todPopWindow : null,
        killed: this.boardEndCamp ? (this.boardKilledChoice === 'Yes') : null,
        // Every board form asks this (End Camp, Post ToD, Edit ToD), so every board form answers
        // it. Null only for a non-board ToD, where there is no board to re-post and null means
        // "leave the monster's standing template alone".
        repost: this.boardMode ? this.boardRepost : null,
        repostLeadHours: this.boardMode && this.boardRepost ? this.boardRepostLeadHours : null,
        // End Camp only: the optional kill screenshot (the plain board Post/Edit ToD has no
        // upload field, so it never sends one and the ToD's existing image is left alone).
        imagePath: this.boardEndCamp ? this.todImagePath : null
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

    const cooldown = this.todCooldownLabel();
    if (!cooldown) {
      this.activity.actionError.set('Enter a positive cooldown.');
      this.activity.actionMessage.set(null);
      return;
    }

    const dayNumber = this.todDayNumberNotSpecified ? null : this.todDraft.dayNumber;
    const interval = this.todIntervalLabel();
    const lootStructure = this.lootStructureFor(linkshellId);
    // Loot is NEVER posted from a ToD any more. It is recorded in the Loot tab and filed against a
    // live or past event -- which is what stopped every ToD-recorded drop landing in the history as
    // source "ToD" with no event behind it. Sent empty rather than dropped from the payload so an
    // older server still parses the request.
    const lootDetails: { itemName: string | null; itemWinner: string | null; winningDkpSpent: number | null }[] = [];

    try {
      if (this.editingTodId !== null) {
        await this.activity.updateTod({
          todId: this.editingTodId,
          monsterName,
          dayNumber,
          popWindow: this.todPopWindow,
          hq: this.todDraft.hq,
          additionalSeconds: this.todDraft.additionalSeconds,
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
          popWindow: this.todPopWindow,
          hq: this.todDraft.hq,
          additionalSeconds: this.todDraft.additionalSeconds,
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
    this.todPopWindow = tod.popWindow ?? null;
    this.todDraft.hq = !!tod.hq;
    this.todDraft.additionalSeconds = tod.additionalSeconds ?? 0;
    this.todDraft.claim = !!tod.claim;
    this.todClaimChoice = tod.claim ? 'Yes' : 'No';
    this.todDayNumberNotSpecified = tod.dayNumber == null;

    const monsterName: string = tod.monsterName ?? '';
    if (this.isKnownTodMonster(monsterName)) {
      this.todDraft.monsterName = monsterName;
      this.todCustomMonsterName = '';
    } else {
      this.todDraft.monsterName = 'Other';
      this.todCustomMonsterName = monsterName;
    }
    this.todMonsterSearch = this.todDraft.monsterName;
    this.todMonsterPickerOpen = false;

    this.applyDurationLabel(tod.cooldown ?? '22 Hour', 'cooldown');
    this.applyDurationLabel(tod.interval ?? null, 'interval');
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
    this.todDraft.monsterName = '';
    this.todDraft.dayNumber = null;
    this.todPopWindow = null;
    this.todDraft.hq = false;
    this.todDraft.additionalSeconds = 0;
    this.todDraft.claim = true;
    this.todDraft.timeLocal = '';
    this.todCooldownValue = 22;
    this.todCooldownUnit = 'hours';
    this.todIntervalValue = 10;
    this.todIntervalUnit = 'mins';
    this.todDraft.noLoot = true;
    this.todDraft.lootDetails = [createEmptyTodLootRow()];
    this.todTimeLocalValue = '';
    this.todRepopLocalValue = '';
    this.todMonsterSearch = this.todDraft.monsterName;
    this.todMonsterPickerOpen = false;
    this.todMonsterActiveIndex = -1;
    this.todCustomMonsterName = '';
    this.todClaimChoice = 'Yes';
    this.todDayNumberNotSpecified = false;
    this.todImagePath = null;
    this.isUploadingTodImage = false;
  }
}
