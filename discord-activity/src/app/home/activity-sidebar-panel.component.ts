import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, Input, effect, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { DiscordActivityService } from '../discord/discord-activity.service';
import type { ActivityJobRatingCommentSummary, ActivityJobRatingsResponse } from '../discord/discord-activity.types';
import { JOB_RELIC_OPTIONS, PROFILE_CRAFT_OPTIONS, PROFILE_JOB_OPTIONS } from './event-job-options';
import { resolveBrowserTimeZone, resolveTimeZoneOptions } from './sidebar-panel.helpers';
import { AuctionsPanelComponent } from './sidebar-panels/auctions-panel.component';
import { InvitesPanelComponent } from './sidebar-panels/invites-panel.component';
import { JobRatingsPanelComponent } from './sidebar-panels/job-ratings-panel.component';
import { RosterPanelComponent } from './sidebar-panels/roster-panel.component';
import { StarRatingComponent } from './sidebar-panels/star-rating.component';

@Component({
  selector: 'app-activity-sidebar-panel',
  imports: [CommonModule, FormsModule, AuctionsPanelComponent, InvitesPanelComponent, JobRatingsPanelComponent, RosterPanelComponent, StarRatingComponent],
  templateUrl: './activity-sidebar-panel.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
  styles: [`
    .jobs-panel {
      width: 100%;
      padding: 22px 36px 34px;
      border: 1px solid var(--border);
      border-radius: 8px;
      background:
        radial-gradient(circle at top center, rgba(79, 124, 255, 0.08), transparent 34%),
        linear-gradient(180deg, var(--bg-elev) 0%, var(--bg) 100%);
      color: var(--fg);
      box-shadow: inset 0 1px 0 rgba(255, 255, 255, 0.03);
    }
    .jobs-header { text-align: center; margin-bottom: 18px; }
    .jobs-header h2 { margin: 0 0 8px; font-size: 18px; font-weight: 800; letter-spacing: 0.04em; }
    .jobs-header p { margin: 0 0 14px; color: #b8bac4; font-size: 13px; }
    .character-tabs {
      display: inline-grid;
      grid-template-columns: repeat(3, 1fr);
      width: 360px;
      border: 1px solid rgba(255, 255, 255, 0.12);
      border-radius: 6px;
      overflow: hidden;
      background: rgba(0, 0, 0, 0.18);
    }
    .character-tabs button {
      height: 26px; border: 0; border-right: 1px solid rgba(255, 255, 255, 0.08);
      background: transparent; color: #d6d7df; font-size: 12px; font-weight: 600;
      font-family: inherit; cursor: pointer;
    }
    .character-tabs button:last-child { border-right: 0; }
    .character-tabs button.active {
      color: #fff;
      background: linear-gradient(180deg, var(--accent) 0%, var(--accent-strong) 100%);
      box-shadow: 0 0 16px rgba(79, 124, 255, 0.35);
    }
    .jobs-grid { display: grid; grid-template-columns: repeat(5, minmax(0, 1fr)); gap: 10px 16px; }
    .job-card {
      min-height: 112px; padding: 12px 14px;
      border: 1px solid rgba(255, 255, 255, 0.11); border-radius: 8px;
      background:
        linear-gradient(180deg, rgba(255, 255, 255, 0.035), rgba(255, 255, 255, 0.01)),
        #131419;
      box-shadow: inset 0 1px 0 rgba(255, 255, 255, 0.04), 0 8px 18px rgba(0, 0, 0, 0.18);
    }
    .job-top { display: flex; align-items: center; justify-content: space-between; margin-bottom: 10px; }
    .job-top span { font-size: 14px; font-weight: 800; letter-spacing: 0.02em; }
    .job-top input {
      width: 54px; height: 22px; padding: 0 6px; text-align: center;
      border-radius: 6px; border: 1px solid rgba(255, 255, 255, 0.13);
      background: rgba(0, 0, 0, 0.22); color: #fff; font-size: 14px; font-weight: 800;
      font-family: inherit; -moz-appearance: textfield;
    }
    .job-top input::-webkit-inner-spin-button,
    .job-top input::-webkit-outer-spin-button { -webkit-appearance: none; margin: 0; }
    .strength {
      width: 100%; height: 22px; margin-bottom: 10px; padding: 0;
      display: flex; align-items: center; justify-content: center;
      border-radius: 999px; border: 1px solid rgba(79, 124, 255, 0.18);
      background: rgba(255, 255, 255, 0.025); color: var(--accent);
      font-size: 12px; font-weight: 700; font-family: inherit; cursor: pointer;
    }
    .strength.gold { color: #ffbf2f; border-color: rgba(255, 191, 47, 0.18); }
    .strength.empty { color: #858895; font-weight: 600; border-color: rgba(255, 255, 255, 0.08); }
    .rating-row {
      display: grid; grid-template-columns: 52px 1fr; align-items: center; gap: 8px;
      margin-top: 4px; color: #80838e; font-size: 13px; line-height: 1;
    }
    .rating-row > span { color: #c8cad2; font-weight: 500; }
    .relic-block { margin-top: 6px; }
    .relic-block__label { display: block; font-size: 13px; color: #c8cad2; font-weight: 500; margin-bottom: 4px; }
    .relic-chips { display: flex; flex-wrap: wrap; gap: 4px; margin-bottom: 5px; }
    .relic-chip {
      display: inline-flex; align-items: center; gap: 4px; padding: 2px 4px 2px 8px;
      font-size: 11px; font-weight: 600; color: #ffd9a0; border-radius: 999px;
      border: 1px solid rgba(255,177,31,.45); background: rgba(255,120,0,.12);
    }
    .relic-chip button {
      border: 0; background: none; color: inherit; cursor: pointer; line-height: 1;
      font-size: 11px; padding: 0 2px; opacity: .8;
    }
    .relic-chip button:hover { opacity: 1; }
    /* Modern "+ Add relic" control (provided design). A native select can't hold a
       separate icon span, so the "+ Add relic" option text carries the glyph and
       text-align-last centres it; the rest mirrors the supplied .add-relic-btn. */
    .relic-add {
      width: 100%; height: 44px; margin-top: 4px;
      text-align: center; text-align-last: center;
      border: 1px solid rgba(125, 145, 255, 0.85); border-radius: 6px;
      background:
        linear-gradient(180deg, rgba(32, 34, 45, 0.95), rgba(13, 15, 22, 0.95)),
        radial-gradient(circle at top, rgba(105, 123, 255, 0.18), transparent 55%);
      color: #9fadff; font-size: 14px; font-weight: 800; letter-spacing: 0.2px;
      box-shadow: inset 0 0 0 1px rgba(255, 255, 255, 0.03), 0 0 12px rgba(91, 105, 255, 0.18);
      cursor: pointer; font-family: inherit;
      transition: border-color .18s ease, color .18s ease, box-shadow .18s ease, transform .18s ease, background .18s ease;
    }
    .relic-add:hover {
      color: #c6ceff; border-color: rgba(155, 170, 255, 1);
      background:
        linear-gradient(180deg, rgba(38, 41, 56, 0.98), rgba(15, 18, 28, 0.98)),
        radial-gradient(circle at top, rgba(125, 145, 255, 0.28), transparent 60%);
      box-shadow: inset 0 0 0 1px rgba(255, 255, 255, 0.05), 0 0 18px rgba(105, 123, 255, 0.35);
    }
    .relic-add:active { transform: translateY(1px); }
    .relic-add:focus-visible {
      outline: none;
      box-shadow: 0 0 0 3px rgba(125, 145, 255, 0.25), 0 0 18px rgba(105, 123, 255, 0.4);
    }
    .relic-add option { color: #fff; background: #14161d; font-weight: 600; }
    /* Merits modal */
    .merit-modal-backdrop {
      position: fixed; inset: 0; z-index: 300; background: rgba(0, 0, 0, 0.55);
      display: flex; align-items: center; justify-content: center; padding: 24px;
    }
    .merit-modal {
      width: 100%; max-width: 420px; padding: 16px 18px;
      background: var(--surface); border: 1px solid var(--border);
      border-radius: 12px; box-shadow: 0 24px 64px rgba(0,0,0,.45);
    }
    .merit-modal__head { display: flex; align-items: center; justify-content: space-between; gap: 8px; margin-bottom: 6px; }
    .merit-modal__head strong { font-size: 14px; }
    .merit-modal__hint { margin: 0 0 10px; font-size: 12px; color: var(--fg-3); }
    .merit-modal textarea { width: 100%; resize: vertical; font-family: inherit; }
    .merit-modal__actions { display: flex; align-items: center; gap: 8px; margin-top: 12px; }
    .merit-modal__clear { color: var(--danger); }
    @media (max-width: 1200px) { .jobs-grid { grid-template-columns: repeat(3, minmax(0, 1fr)); } }
    @media (max-width: 760px) {
      .jobs-panel { padding: 20px; }
      .character-tabs { width: 100%; }
      .jobs-grid { grid-template-columns: 1fr; }
    }
  `]
})
export class ActivitySidebarPanelComponent {
  @Input() visibleSections?: readonly string[];
  protected readonly activity = inject(DiscordActivityService);

  protected showSection(name: string): boolean {
    return !this.visibleSections || this.visibleSections.includes(name);
  }

  protected readonly profileModel = {
    characterName: '',
    timeZone: '',
    altCharacterName1: '',
    altCharacterName2: '',
    // Catalog-aligned per-job levels (index 0 = WAR ... 14 = SMN); seeded from
    // the overview's appUser.jobLevels and bound to the "My Jobs" inputs. Starts
    // as 15 zeros (the classic job count) so the inputs always have a value even
    // before the overview seeds it.
    jobLevels: Array.from({ length: 15 }, () => 0) as number[],
    // Same per-job arrays for the two alt characters (bound to the alt tabs).
    alt1JobLevels: Array.from({ length: 15 }, () => 0) as number[],
    alt2JobLevels: Array.from({ length: 15 }, () => 0) as number[],
    // Parallel "strong" flags (well-geared/merited), toggled per job under each
    // slot. Same index layout as the level arrays above.
    strongJobs: Array.from({ length: 15 }, () => false) as boolean[],
    alt1StrongJobs: Array.from({ length: 15 }, () => false) as boolean[],
    alt2StrongJobs: Array.from({ length: 15 }, () => false) as boolean[],
    // Per-craft levels (index 0 = Alchemy ... 8 = Fishing) for the main + two
    // alts. Account-level (sent regardless of linkshell membership).
    craftLevels: Array.from({ length: 9 }, () => 0) as number[],
    alt1CraftLevels: Array.from({ length: 9 }, () => 0) as number[],
    alt2CraftLevels: Array.from({ length: 9 }, () => 0) as number[],
    // Per-job free-text merit notes (index 0 = WAR … 14 = SMN) for main + alts,
    // set via the "Merited" modal. Parallel to the strong-flag arrays.
    meritJobs: Array.from({ length: 15 }, () => '') as string[],
    alt1MeritJobs: Array.from({ length: 15 }, () => '') as string[],
    alt2MeritJobs: Array.from({ length: 15 }, () => '') as string[]
  };

  // The 15 classic jobs, in the exact order the API's catalog-aligned jobLevels
  // array uses, so profileModel.jobLevels[i] is the level of profileJobOptions[i].
  protected readonly profileJobOptions = [...PROFILE_JOB_OPTIONS];
  protected readonly profileJobMaxLevel = 75;

  // Which character's job grid the tabs are showing. The alt tabs only appear
  // once the matching alt character has a name.
  protected selectedJobTab: 'main' | 'alt1' | 'alt2' = 'main';
  protected selectJobTab(tab: 'main' | 'alt1' | 'alt2'): void {
    this.selectedJobTab = tab;
    const slot = tab === 'alt1' ? 1 : tab === 'alt2' ? 2 : 0;
    // Lazily pull this character's self gear/skill/relic the first time it's shown.
    this.ensureSelfSlotLoaded(slot);
    // Reflect this character's peer feedback immediately (from cache if loaded;
    // loadMyRatings applies it when the fetch completes otherwise).
    this.applyPeerBlockForSlot(slot);
  }

  // Number of visible character tabs (main + each named alt) — drives the
  // character-tabs grid columns so 1–3 tabs always fill their row evenly.
  protected jobTabCount(): number {
    return 1
      + (this.profileModel.altCharacterName1.trim() ? 1 : 0)
      + (this.profileModel.altCharacterName2.trim() ? 1 : 0);
  }

  // The job-level array for the active tab (a live reference into profileModel,
  // so edits write straight back to the right character).
  protected get activeJobLevels(): number[] {
    if (this.selectedJobTab === 'alt1' && this.profileModel.altCharacterName1.trim()) return this.profileModel.alt1JobLevels;
    if (this.selectedJobTab === 'alt2' && this.profileModel.altCharacterName2.trim()) return this.profileModel.alt2JobLevels;
    return this.profileModel.jobLevels;
  }

  // The "strong" flag array for the active tab (a live reference, like
  // activeJobLevels), so the per-job toggles write to the right character.
  protected get activeStrongJobs(): boolean[] {
    if (this.selectedJobTab === 'alt1' && this.profileModel.altCharacterName1.trim()) return this.profileModel.alt1StrongJobs;
    if (this.selectedJobTab === 'alt2' && this.profileModel.altCharacterName2.trim()) return this.profileModel.alt2StrongJobs;
    return this.profileModel.strongJobs;
  }

  // Clicking the pill marks the job merited (if not already) and opens the modal
  // to specify/edit which merits the member has. The modal can clear it again.
  protected toggleStrong(index: number): void {
    if (!this.activeStrongJobs[index]) { this.activeStrongJobs[index] = true; }
    this.openMeritModal(index);
  }

  // Merit notes for the active tab (a live reference, like activeStrongJobs).
  protected get activeMeritJobs(): string[] {
    if (this.selectedJobTab === 'alt1' && this.profileModel.altCharacterName1.trim()) return this.profileModel.alt1MeritJobs;
    if (this.selectedJobTab === 'alt2' && this.profileModel.altCharacterName2.trim()) return this.profileModel.alt2MeritJobs;
    return this.profileModel.meritJobs;
  }

  // ----- "Specify your merits" modal (opened when a job is marked merited) -----
  protected readonly meritModalJobIndex = signal<number | null>(null);
  protected meritDraft = '';
  protected meritModalJobName(): string {
    const i = this.meritModalJobIndex();
    return i === null ? '' : (this.profileJobOptions[i] ?? '');
  }
  protected openMeritModal(index: number): void {
    this.meritDraft = this.activeMeritJobs[index] ?? '';
    this.meritModalJobIndex.set(index);
  }
  protected saveMerit(): void {
    const i = this.meritModalJobIndex();
    if (i !== null) { this.activeMeritJobs[i] = this.meritDraft.trim(); }
    this.meritModalJobIndex.set(null);
  }
  // "Not merited" — un-mark the job and drop its merit note.
  protected clearMerited(): void {
    const i = this.meritModalJobIndex();
    if (i !== null) {
      this.activeStrongJobs[i] = false;
      this.activeMeritJobs[i] = '';
    }
    this.meritModalJobIndex.set(null);
  }
  protected closeMeritModal(): void {
    this.meritModalJobIndex.set(null);
  }

  // The nine crafts, in the order the API's craftLevels array uses.
  protected readonly profileCraftOptions = [...PROFILE_CRAFT_OPTIONS];
  protected readonly profileCraftMaxLevel = 110;

  // Craft levels for the active job tab (reuses selectedJobTab so the Crafts grid
  // tracks the same character as the Jobs grid above it).
  protected get activeCraftLevels(): number[] {
    if (this.selectedJobTab === 'alt1' && this.profileModel.altCharacterName1.trim()) return this.profileModel.alt1CraftLevels;
    if (this.selectedJobTab === 'alt2' && this.profileModel.altCharacterName2.trim()) return this.profileModel.alt2CraftLevels;
    return this.profileModel.craftLevels;
  }

  // Inputs for the embedded "Rate teammates" panel (alt names drive its
  // per-teammate character picker).
  protected ratingLinkshellId(): number { return this.activity.overview()?.appUser?.primaryLinkshellId ?? 0; }
  protected ratingMe(): string { return this.activity.overview()?.appUser?.id ?? ''; }
  protected ratingMembers(): { appUserId: string; characterName: string; altCharacterName1?: string | null; altCharacterName2?: string | null }[] {
    return (this.activity.overview()?.primaryLinkshell?.members ?? [])
      .filter(m => !!m.appUserId)
      .map(m => ({
        appUserId: m.appUserId!,
        characterName: m.characterName,
        altCharacterName1: m.altCharacterName1,
        altCharacterName2: m.altCharacterName2
      }));
  }

  // The rating character slot for the active jobs tab (0 = main, 1 = alt 1, 2 = alt 2).
  protected currentRatingSlot(): number {
    return this.selectedJobTab === 'alt1' ? 1 : this.selectedJobTab === 'alt2' ? 2 : 0;
  }

  // Name of the character whose peer feedback is currently shown (active tab).
  protected activeRatingCharacterName(): string {
    if (this.selectedJobTab === 'alt1') return this.profileModel.altCharacterName1.trim();
    if (this.selectedJobTab === 'alt2') return this.profileModel.altCharacterName2.trim();
    return this.profileModel.characterName.trim();
  }

  // ----- My own ratings (jobs grid self gear/skill/relic stars + "what others think") -----
  // Self gear/skill/relic are per character (main + each alt). One GET per slot
  // seeds the grid stars; the slot-0 GET ALSO carries the peer averages/comments
  // shown in the "what your linkshell thinks" block. AI summary is on-demand.
  protected readonly myRatings = signal<ActivityJobRatingsResponse | null>(null);
  protected readonly aiSummary = signal<ActivityJobRatingCommentSummary | null>(null);
  protected readonly loadingAiSummary = signal(false);
  // slot -> jobIndex -> { gear, skill, relicNames }
  protected readonly selfBySlot: Record<number, Record<number, { gear: number; skill: number; relicNames: string[] }>> = {};
  private readonly loadedSelfSlots = new Set<number>();
  private myRatingsSeed = '';
  // Peer block (averages + comments) and AI summary cached PER SLOT so switching
  // to an alt's tab shows that character's feedback, not the main character's.
  private readonly peerBySlot = new Map<number, ActivityJobRatingsResponse>();
  private readonly summaryBySlot = new Map<number, ActivityJobRatingCommentSummary | null>();

  private slotMap(slot: number): Record<number, { gear: number; skill: number; relicNames: string[] }> {
    return (this.selfBySlot[slot] ??= {});
  }

  // Jobs that at least one teammate has rated — drives the per-job averages table.
  protected peerRatedJobs() {
    return (this.myRatings()?.jobs ?? []).filter(job => job.peerCount > 0);
  }

  protected selfGearValue(jobIndex: number): number { return this.slotMap(this.currentRatingSlot())[jobIndex]?.gear ?? 0; }
  protected selfSkillValue(jobIndex: number): number { return this.slotMap(this.currentRatingSlot())[jobIndex]?.skill ?? 0; }
  protected selfRelicsValue(jobIndex: number): string[] { return this.slotMap(this.currentRatingSlot())[jobIndex]?.relicNames ?? []; }

  // The relic weapons the given job can equip (the full catalog for that job).
  protected relicOptions(jobIndex: number): readonly string[] { return JOB_RELIC_OPTIONS[jobIndex] ?? []; }
  // The job's relics the member hasn't added yet (for the "+ Add relic" dropdown).
  protected availableRelics(jobIndex: number): string[] {
    const owned = this.selfRelicsValue(jobIndex);
    return this.relicOptions(jobIndex).filter(r => !owned.includes(r));
  }

  // Load my self ratings for one character slot. Slot 0 also feeds the peer block.
  private async loadMyRatings(slot: number): Promise<void> {
    const linkshellId = this.ratingLinkshellId();
    const me = this.ratingMe();
    if (!linkshellId || !me) { return; }
    const res = await this.activity.loadJobRatings(linkshellId, me, slot);
    if (!res) { return; }
    const map = this.slotMap(slot);
    for (const job of res.jobs) {
      map[job.jobIndex] = { gear: job.selfGear, skill: job.selfSkill, relicNames: [...(job.selfRelicNames ?? [])] };
    }
    this.loadedSelfSlots.add(slot);
    // Cache this character's peer block; reflect it if it's the active tab.
    this.peerBySlot.set(slot, res);
    if (slot === this.currentRatingSlot()) { this.applyPeerBlockForSlot(slot); }
  }

  // Push the cached peer block for `slot` into the signals the template reads,
  // auto-loading that character's comment summary on demand (cached per slot).
  private applyPeerBlockForSlot(slot: number): void {
    const res = this.peerBySlot.get(slot) ?? null;
    this.myRatings.set(res);
    if (this.summaryBySlot.has(slot)) {
      this.aiSummary.set(this.summaryBySlot.get(slot) ?? null);
    } else {
      this.aiSummary.set(null);
      if ((res?.peerCommentCount ?? 0) > 0) { void this.generateAiSummary(slot); }
    }
  }

  // Lazily load a tab's self ratings the first time it's shown.
  protected ensureSelfSlotLoaded(slot: number): void {
    if (!this.loadedSelfSlots.has(slot)) { void this.loadMyRatings(slot); }
  }

  private async saveSelf(jobIndex: number): Promise<void> {
    const slot = this.currentRatingSlot();
    const e = this.slotMap(slot)[jobIndex] ?? { gear: 0, skill: 0, relicNames: [] };
    await this.activity.rateJob(this.ratingLinkshellId(), this.ratingMe(), jobIndex, e.gear, e.skill, e.relicNames.length > 0, slot, e.relicNames);
  }

  private current(jobIndex: number): { gear: number; skill: number; relicNames: string[] } {
    const e = this.slotMap(this.currentRatingSlot())[jobIndex];
    return { gear: e?.gear ?? 0, skill: e?.skill ?? 0, relicNames: e?.relicNames ?? [] };
  }

  protected setSelfGear(jobIndex: number, value: number): void {
    this.slotMap(this.currentRatingSlot())[jobIndex] = { ...this.current(jobIndex), gear: value };
    void this.saveSelf(jobIndex);
  }

  protected setSelfSkill(jobIndex: number, value: number): void {
    this.slotMap(this.currentRatingSlot())[jobIndex] = { ...this.current(jobIndex), skill: value };
    void this.saveSelf(jobIndex);
  }

  // Add one of the job's relics to this character (ignores blanks/dupes).
  protected addRelic(jobIndex: number, name: string): void {
    const relic = (name ?? '').trim();
    if (!relic) { return; }
    const cur = this.current(jobIndex);
    if (cur.relicNames.includes(relic)) { return; }
    this.slotMap(this.currentRatingSlot())[jobIndex] = { ...cur, relicNames: [...cur.relicNames, relic] };
    void this.saveSelf(jobIndex);
  }

  protected removeRelic(jobIndex: number, name: string): void {
    const cur = this.current(jobIndex);
    this.slotMap(this.currentRatingSlot())[jobIndex] = { ...cur, relicNames: cur.relicNames.filter(r => r !== name) };
    void this.saveSelf(jobIndex);
  }

  protected async generateAiSummary(slot: number = this.currentRatingSlot()): Promise<void> {
    const linkshellId = this.ratingLinkshellId();
    const me = this.ratingMe();
    if (!linkshellId || !me) { return; }
    this.loadingAiSummary.set(true);
    const res = await this.activity.loadJobRatingCommentSummary(linkshellId, me, slot);
    this.loadingAiSummary.set(false);
    this.summaryBySlot.set(slot, res);
    if (slot === this.currentRatingSlot()) { this.aiSummary.set(res); }
  }

  protected selectedLinkshellId = 0;
  protected selectedDkpHistoryAppUserId = '';
  protected dkpSearchTerm = '';
  protected dkpMemberSearchTerm = '';

  // Pagination for the DKP ledger table. Page is a signal so OnPush picks
  // up clicks on the Prev/Next buttons; size is a small constant chosen so
  // a typical screen shows ~one viewport with no scroll inside the table.
  protected readonly dkpPage = signal(0);
  protected readonly dkpPageSize = 15;
  protected isDkpAuditOpen = false;
  protected readonly dkpAuditModel: {
    mode: 'Adjust' | 'Add' | 'Misc';
    targetAppUserId: string;
    relatedLedgerEntryId: number | null;
    sourceWindowEventId: number | null;
    amount: number | null;
    reason: string;
  } = {
    mode: 'Misc',
    targetAppUserId: '',
    relatedLedgerEntryId: null,
    sourceWindowEventId: null,
    amount: null,
    reason: ''
  };
  protected readonly browserTimeZone = resolveBrowserTimeZone();
  protected readonly timeZoneOptions = resolveTimeZoneOptions(this.activity.overview()?.appUser?.timeZone, this.browserTimeZone);
  private profileSeed = '';
  private historySeed = '';

  // Bumped after a brand-new linkshell is created in the roster panel so
  // the invites panel re-anchors its target to the freshly-resolved
  // primary linkshell (matches pre-refactor behavior).
  protected readonly invitePrimaryResetTick = signal(0);

  // Latest-write-wins guard. Each linkshell-selection change bumps this
  // counter; in-flight loads from a previous selection capture the older
  // value and bail before mutating state, so a slow detail/auction load
  // can't overwrite data for the user's current selection.
  private selectionGen = 0;

  // Stable arrow bindings for child component callback @Inputs — wrapping
  // the methods avoids `this` binding issues when the references are passed
  // into `[selectLinkshell]` etc.
  protected readonly selectLinkshellFn = (linkshellId: number) => this.selectLinkshell(linkshellId);
  protected readonly onPrimaryLinkshellChangedFn = () => this.invitePrimaryResetTick.update(tick => tick + 1);

  public constructor()
  {
    effect(() => {
      const appUser = this.activity.overview()?.appUser;
      if (!appUser) {
        return;
      }

      const nextCharacterName = appUser.characterName ?? '';
      const nextTimeZone = appUser.timeZone ?? this.browserTimeZone;
      const nextAlt1 = appUser.altCharacterName1 ?? '';
      const nextAlt2 = appUser.altCharacterName2 ?? '';
      const sourceLevels = appUser.jobLevels ?? [];
      const nextJobLevels = this.profileJobOptions.map((_, i) => sourceLevels[i] ?? 0);
      const sourceAlt1 = appUser.alt1JobLevels ?? [];
      const sourceAlt2 = appUser.alt2JobLevels ?? [];
      const nextAlt1Jobs = this.profileJobOptions.map((_, i) => sourceAlt1[i] ?? 0);
      const nextAlt2Jobs = this.profileJobOptions.map((_, i) => sourceAlt2[i] ?? 0);
      const sourceStrong = appUser.strongJobs ?? [];
      const sourceAlt1Strong = appUser.alt1StrongJobs ?? [];
      const sourceAlt2Strong = appUser.alt2StrongJobs ?? [];
      const nextStrong = this.profileJobOptions.map((_, i) => sourceStrong[i] ?? false);
      const nextAlt1Strong = this.profileJobOptions.map((_, i) => sourceAlt1Strong[i] ?? false);
      const nextAlt2Strong = this.profileJobOptions.map((_, i) => sourceAlt2Strong[i] ?? false);
      const sourceCrafts = appUser.craftLevels ?? [];
      const sourceAlt1Crafts = appUser.alt1CraftLevels ?? [];
      const sourceAlt2Crafts = appUser.alt2CraftLevels ?? [];
      const nextCrafts = this.profileCraftOptions.map((_, i) => sourceCrafts[i] ?? 0);
      const nextAlt1Crafts = this.profileCraftOptions.map((_, i) => sourceAlt1Crafts[i] ?? 0);
      const nextAlt2Crafts = this.profileCraftOptions.map((_, i) => sourceAlt2Crafts[i] ?? 0);
      const sourceMerits = appUser.meritJobs ?? [];
      const sourceAlt1Merits = appUser.alt1MeritJobs ?? [];
      const sourceAlt2Merits = appUser.alt2MeritJobs ?? [];
      const nextMerits = this.profileJobOptions.map((_, i) => sourceMerits[i] ?? '');
      const nextAlt1Merits = this.profileJobOptions.map((_, i) => sourceAlt1Merits[i] ?? '');
      const nextAlt2Merits = this.profileJobOptions.map((_, i) => sourceAlt2Merits[i] ?? '');
      const nextSeed =
        `${appUser.id}|${nextCharacterName}|${nextTimeZone}|${nextAlt1}|${nextAlt2}|` +
        `${nextJobLevels.join(',')}|${nextAlt1Jobs.join(',')}|${nextAlt2Jobs.join(',')}|` +
        `${nextStrong.join(',')}|${nextAlt1Strong.join(',')}|${nextAlt2Strong.join(',')}|` +
        `${nextCrafts.join(',')}|${nextAlt1Crafts.join(',')}|${nextAlt2Crafts.join(',')}|` +
        `${nextMerits.join('§')}|${nextAlt1Merits.join('§')}|${nextAlt2Merits.join('§')}`;

      if (nextSeed === this.profileSeed) {
        return;
      }

      this.profileSeed = nextSeed;
      this.profileModel.characterName = nextCharacterName;
      this.profileModel.timeZone = nextTimeZone;
      this.profileModel.altCharacterName1 = nextAlt1;
      this.profileModel.altCharacterName2 = nextAlt2;
      this.profileModel.jobLevels = nextJobLevels;
      this.profileModel.alt1JobLevels = nextAlt1Jobs;
      this.profileModel.alt2JobLevels = nextAlt2Jobs;
      this.profileModel.strongJobs = nextStrong;
      this.profileModel.alt1StrongJobs = nextAlt1Strong;
      this.profileModel.alt2StrongJobs = nextAlt2Strong;
      this.profileModel.craftLevels = nextCrafts;
      this.profileModel.alt1CraftLevels = nextAlt1Crafts;
      this.profileModel.alt2CraftLevels = nextAlt2Crafts;
      this.profileModel.meritJobs = nextMerits;
      this.profileModel.alt1MeritJobs = nextAlt1Merits;
      this.profileModel.alt2MeritJobs = nextAlt2Merits;
    });

    // Load my own job ratings (self gear/skill seeds + peer averages/comments)
    // once my primary linkshell + id are known, and whenever they change.
    effect(() => {
      const linkshellId = this.ratingLinkshellId();
      const me = this.ratingMe();
      if (!linkshellId || !me) { return; }
      const seed = `${linkshellId}|${me}`;
      if (seed === this.myRatingsSeed) { return; }
      this.myRatingsSeed = seed;
      this.loadedSelfSlots.clear();
      this.peerBySlot.clear();
      this.summaryBySlot.clear();
      void this.loadMyRatings(0);
      this.loadedSelfSlots.add(0);
    });

    effect(() => {
      const memberships = this.linkshellMemberships();
      if (memberships.length === 0) {
        this.selectedLinkshellId = 0;
        this.activity.clearLinkshellDetail();
        return;
      }

      const preferredId =
        this.selectedLinkshellId ||
        this.primaryLinkshellId() ||
        memberships[0]?.id ||
        0;

      if (!memberships.some(linkshell => linkshell.id === preferredId)) {
        this.selectedLinkshellId = memberships[0].id;
      } else if (this.selectedLinkshellId === 0) {
        this.selectedLinkshellId = preferredId;
      }
    });

    effect(() => {
      const selectedLinkshellId = this.selectedLinkshellId;
      const memberships = this.linkshellMemberships();

      if (!selectedLinkshellId || !memberships.some(linkshell => linkshell.id === selectedLinkshellId)) {
        this.activity.clearLinkshellDetail();
        this.selectedDkpHistoryAppUserId = '';
        this.activity.clearDkpHistory();
        this.activity.clearAuctionState();
        return;
      }

      const gen = ++this.selectionGen;
      const isStale = (): boolean => gen !== this.selectionGen;

      void (async () => {
        await this.activity.loadLinkshellDetail(selectedLinkshellId);
        if (isStale()) return;
        await this.reloadDkpHistory();
        if (isStale()) return;
        await this.activity.loadAuctions(selectedLinkshellId);
        if (isStale()) return;
        await this.activity.loadAuctionHistory(selectedLinkshellId);
      })();
    });

    effect(() => {
      const overview = this.activity.overview();
      if (!overview) {
        this.historySeed = '';
        this.activity.clearHistoryList();
        this.activity.clearHistoryDetail();
        return;
      }

      const primaryLinkshellId = overview.appUser?.primaryLinkshellId ?? overview.primaryLinkshell?.id ?? 0;
      const recentHistoryIds = (overview.recentHistory ?? []).map(history => history.id).sort((left, right) => left - right);
      const nextSeed = `${primaryLinkshellId}|${recentHistoryIds.join(',')}`;

      if (nextSeed === this.historySeed) {
        return;
      }

      this.historySeed = nextSeed;
      this.activity.clearHistoryDetail();
      void this.activity.loadHistoryList();
    });
  }

  protected selectedLinkshell() {
    const selectedId = this.selectedLinkshellId;
    const primary = this.activity.overview()?.primaryLinkshell;

    if (primary && primary.id === selectedId) {
      return {
        id: primary.id,
        name: primary.name,
        memberCount: primary.memberCount,
        details: primary.details,
        status: 'Active',
        members: primary.members
      };
    }

    return this.activity.linkshellDetail();
  }

  protected linkshellMemberships() {
    return this.activity.overview()?.linkshells ?? [];
  }

  protected primaryLinkshellId(): number | null {
    return this.activity.overview()?.appUser?.primaryLinkshellId ?? this.activity.overview()?.primaryLinkshell?.id ?? null;
  }

  protected isManagerMode(): boolean {
    return this.linkshellMemberships().some(link => this.canManageLinkshell(link.id));
  }

  protected isMemberMode(): boolean {
    return !this.isManagerMode();
  }

  protected canManageLinkshell(linkshellId: number): boolean {
    const membership = this.linkshellMemberships().find(link => link.id === linkshellId);
    const rank = (membership?.rank ?? '').toLowerCase();
    return rank === 'leader' || rank === 'officer';
  }

  protected needsProfileSetup(): boolean {
    const appUser = this.activity.overview()?.appUser;
    return !appUser?.characterName?.trim() || !appUser?.timeZone?.trim();
  }

  protected hasLinkshell(): boolean {
    return this.linkshellMemberships().length > 0;
  }

  protected async submitProfile(): Promise<void> {
    const clamp = (levels: number[]): number[] => levels.map(level => {
      const value = Math.trunc(Number(level)) || 0;
      return value < 0 ? 0 : value > this.profileJobMaxLevel ? this.profileJobMaxLevel : value;
    });

    // Main job levels are stored per-membership, so only send them in a linkshell.
    const jobLevels = this.hasLinkshell() ? clamp(this.profileModel.jobLevels) : null;
    // Alt job levels live on the account; send each named alt's grid.
    const alt1JobLevels = this.profileModel.altCharacterName1.trim() ? clamp(this.profileModel.alt1JobLevels) : null;
    const alt2JobLevels = this.profileModel.altCharacterName2.trim() ? clamp(this.profileModel.alt2JobLevels) : null;
    // "Strong" flags travel alongside the matching level arrays, same gating.
    const strongJobs = this.hasLinkshell() ? [...this.profileModel.strongJobs] : null;
    const alt1StrongJobs = this.profileModel.altCharacterName1.trim() ? [...this.profileModel.alt1StrongJobs] : null;
    const alt2StrongJobs = this.profileModel.altCharacterName2.trim() ? [...this.profileModel.alt2StrongJobs] : null;

    // Crafts are account-level: always send the main, plus each named alt's grid.
    const clampCraft = (levels: number[]): number[] => levels.map(level => {
      const value = Math.trunc(Number(level)) || 0;
      return value < 0 ? 0 : value > this.profileCraftMaxLevel ? this.profileCraftMaxLevel : value;
    });
    const craftLevels = clampCraft(this.profileModel.craftLevels);
    const alt1CraftLevels = this.profileModel.altCharacterName1.trim() ? clampCraft(this.profileModel.alt1CraftLevels) : null;
    const alt2CraftLevels = this.profileModel.altCharacterName2.trim() ? clampCraft(this.profileModel.alt2CraftLevels) : null;

    // Merit notes travel alongside the strong flags, same per-character gating.
    const trimMerits = (notes: string[]): string[] => notes.map(note => (note ?? '').trim());
    const meritJobs = this.hasLinkshell() ? trimMerits(this.profileModel.meritJobs) : null;
    const alt1MeritJobs = this.profileModel.altCharacterName1.trim() ? trimMerits(this.profileModel.alt1MeritJobs) : null;
    const alt2MeritJobs = this.profileModel.altCharacterName2.trim() ? trimMerits(this.profileModel.alt2MeritJobs) : null;

    await this.activity.updateProfile({
      characterName: this.profileModel.characterName.trim(),
      timeZone: this.profileModel.timeZone.trim() || null,
      altCharacterName1: this.profileModel.altCharacterName1.trim() || null,
      altCharacterName2: this.profileModel.altCharacterName2.trim() || null,
      jobLevels,
      alt1JobLevels,
      alt2JobLevels,
      strongJobs,
      alt1StrongJobs,
      alt2StrongJobs,
      craftLevels,
      alt1CraftLevels,
      alt2CraftLevels,
      meritJobs,
      alt1MeritJobs,
      alt2MeritJobs
    });
  }

  protected historyList() {
    return this.activity.historyList();
  }

  // ---------- Event History pagination (10 per page) ----------
  protected readonly historyPageSize = 10;
  protected readonly historyPage = signal(1);

  protected historyTotalPages(): number {
    return Math.max(1, Math.ceil(this.historyList().length / this.historyPageSize));
  }

  protected historyPageItems() {
    const all = this.historyList();
    // Clamp the page so deletes/refreshes that shrink the list can never
    // leave us pointing at a non-existent page.
    const page = Math.min(Math.max(1, this.historyPage()), this.historyTotalPages());
    if (page !== this.historyPage()) {
      // Defer the corrective set so we don't synchronously mutate during
      // change detection — a microtask keeps Angular happy.
      queueMicrotask(() => this.historyPage.set(page));
    }
    const start = (page - 1) * this.historyPageSize;
    return all.slice(start, start + this.historyPageSize);
  }

  protected historyPageGoto(page: number): void {
    const clamped = Math.min(Math.max(1, page), this.historyTotalPages());
    this.historyPage.set(clamped);
  }

  protected historyPageRangeLabel(): string {
    const total = this.historyList().length;
    if (total === 0) return '0';
    const page = Math.min(Math.max(1, this.historyPage()), this.historyTotalPages());
    const start = (page - 1) * this.historyPageSize + 1;
    const end = Math.min(start + this.historyPageSize - 1, total);
    return `${start}–${end} of ${total}`;
  }

  protected historyDetail() {
    return this.activity.historyDetail();
  }

  protected formatDurationForLinkshell(duration: number | null | undefined, _linkshellId: number): string {
    // Exact elapsed time (second precision), never rounded down to a misleading
    // "0h". Long events read "2h 15m"; short ones "35m" / "35s".
    const totalSeconds = Math.max(0, Math.round((duration ?? 0) * 3600));
    if (totalSeconds === 0) { return '0m'; }
    const h = Math.floor(totalSeconds / 3600);
    const m = Math.floor((totalSeconds % 3600) / 60);
    const s = totalSeconds % 60;
    const parts: string[] = [];
    if (h > 0) { parts.push(`${h}h`); }
    if (m > 0) { parts.push(`${m}m`); }
    if (s > 0 && h === 0) { parts.push(`${s}s`); }
    return parts.join(' ');
  }

  protected dkpHistory() {
    return this.activity.dkpHistory();
  }

  protected async selectLinkshell(linkshellId: number): Promise<void> {
    if (!linkshellId) {
      return;
    }

    this.selectedLinkshellId = linkshellId;
    this.selectedDkpHistoryAppUserId = '';
    await this.activity.loadLinkshellDetail(linkshellId);
    await this.reloadDkpHistory();
    await this.activity.loadAuctions(linkshellId);
    await this.activity.loadAuctionHistory(linkshellId);
  }

  protected async onDkpHistoryMemberChange(appUserId: string): Promise<void> {
    this.selectedDkpHistoryAppUserId = appUserId;
    await this.reloadDkpHistory();
  }

  protected filteredDkpMembers() {
    const members = this.dkpHistory()?.members ?? [];
    const term = this.dkpMemberSearchTerm.trim().toLowerCase();
    if (!term) {
      return members;
    }
    return members.filter(member =>
      (member.characterName ?? '').toLowerCase().includes(term)
    );
  }

  protected filteredDkpEntries() {
    const entries = this.dkpHistory()?.entries ?? [];
    const term = this.dkpSearchTerm.trim().toLowerCase();
    if (!term) {
      return entries;
    }
    return entries.filter(entry => {
      const haystacks = [
        this.dkpEntryTypeLabel(entry.entryType),
        entry.entryType,
        entry.eventName,
        entry.eventType,
        entry.eventLocation,
        entry.itemName,
        entry.details
      ];
      return haystacks.some(field =>
        typeof field === 'string' && field.toLowerCase().includes(term)
      );
    });
  }

  protected pagedDkpEntries() {
    const all = this.filteredDkpEntries();
    const size = this.dkpPageSize;
    const totalPages = Math.max(1, Math.ceil(all.length / size));
    const currentPage = this.dkpPage();
    const clamped = currentPage >= totalPages ? totalPages - 1 : currentPage;
    if (clamped !== currentPage) {
      // Defer corrective set so we don't synchronously mutate during change
      // detection — same pattern as historyPageItems above.
      queueMicrotask(() => this.dkpPage.set(clamped));
    }
    const start = clamped * size;
    return all.slice(start, start + size);
  }

  protected dkpPageCount(): number {
    return Math.max(1, Math.ceil(this.filteredDkpEntries().length / this.dkpPageSize));
  }

  protected dkpNextPage(): void {
    const next = this.dkpPage() + 1;
    if (next < this.dkpPageCount()) this.dkpPage.set(next);
  }

  protected dkpPrevPage(): void {
    const current = this.dkpPage();
    if (current > 0) this.dkpPage.set(current - 1);
  }

  protected dkpEntryTypeLabel(entryType: string): string {
    switch (entryType) {
      case 'LootSpent':
        return 'Loot Spent';
      case 'LootRefund':
        return 'Loot Refund';
      case 'LootEditRefund':
        return 'Loot Edit · Refund';
      case 'LootEditSpent':
        return 'Loot Edit · Spent';
      case 'LootDeleteRefund':
        return 'Loot Delete · Refund';
      case 'EventEarned':
        return 'Event Earned';
      case 'SnapshotEarned':
        return 'Snapshot Earned';
      case 'AuctionSpent':
        return 'Auction Spent';
      case 'AuditAdjustment':
        return 'Audit · Adjustment';
      case 'AuditMisc':
        return 'Audit · Misc';
      default:
        return entryType || 'Entry';
    }
  }

  // Canonical event-type ordering. Anything not in this list (e.g. blank
  // entries that get bucketed as "Unspecified", or future custom types)
  // sorts to the bottom in alphabetical order.
  private static readonly DKP_EVENT_TYPE_ORDER: readonly string[] = [
    'Sky', 'Sea', 'Dynamis', 'Limbus',
    'HNM', 'HENM', 'NM', 'BCNM', 'KSNM',
    'Other'
  ];

  protected dkpEarnedByEventType(): { eventType: string; amount: number }[] {
    const entries = this.dkpHistory()?.entries ?? [];
    const totals = new Map<string, number>();
    for (const entry of entries) {
      if (entry.entryType !== 'EventEarned' && entry.entryType !== 'SnapshotEarned') continue;
      const key = (entry.eventType ?? '').trim() || 'Unspecified';
      totals.set(key, (totals.get(key) ?? 0) + entry.amount);
    }
    const orderIndex = (eventType: string): number => {
      const idx = ActivitySidebarPanelComponent.DKP_EVENT_TYPE_ORDER.indexOf(eventType);
      return idx === -1 ? Number.MAX_SAFE_INTEGER : idx;
    };
    return [...totals.entries()]
      .map(([eventType, amount]) => ({ eventType, amount: Math.round(amount * 100) / 100 }))
      .sort((a, b) => {
        const ai = orderIndex(a.eventType);
        const bi = orderIndex(b.eventType);
        if (ai !== bi) return ai - bi;
        return a.eventType.localeCompare(b.eventType);
      });
  }

  protected dkpTotalEarned(): number {
    const sum = this.dkpEarnedByEventType().reduce((acc, row) => acc + row.amount, 0);
    return Math.round(sum * 100) / 100;
  }

  // "Showing X to Y of Z" range for the ledger footer (1-based, clamped).
  protected dkpShowingRange(): { start: number; end: number; total: number } {
    const total = this.filteredDkpEntries().length;
    if (total === 0) return { start: 0, end: 0, total: 0 };
    const start = this.dkpPage() * this.dkpPageSize;
    return { start: start + 1, end: Math.min(start + this.dkpPageSize, total), total };
  }

  // Page-number buttons for the ledger paginator, windowed to at most 7 around
  // the current page so a long ledger doesn't spray dozens of buttons.
  protected dkpPageNumbers(): number[] {
    const count = this.dkpPageCount();
    const max = 7;
    if (count <= max) return Array.from({ length: count }, (_, i) => i);
    const current = this.dkpPage();
    let start = Math.max(0, current - Math.floor(max / 2));
    const end = Math.min(count, start + max);
    start = Math.max(0, end - max);
    return Array.from({ length: end - start }, (_, i) => start + i);
  }

  protected goToDkpPage(page: number): void {
    const clamped = Math.max(0, Math.min(page, this.dkpPageCount() - 1));
    this.dkpPage.set(clamped);
  }

  protected canAuditSelectedLinkshell(): boolean {
    return !!this.selectedLinkshellId && this.canManageLinkshell(this.selectedLinkshellId);
  }

  // "Add to a previous entry" credits a member missed by a snapshot — a concept
  // that only exists for HNM-style attendance (snapshots / window events). Hide
  // it for Sky/Sea/Dynamis linkshells, which never produce snapshots.
  protected canCreditSnapshot(): boolean {
    const ls = (this.activity.overview()?.linkshells ?? []).find(l => l.id === this.selectedLinkshellId);
    return (ls?.settings?.linkshellType ?? 'Both') !== 'SkySeaDynamis';
  }

  protected openDkpAudit(): void {
    const history = this.dkpHistory();
    this.dkpAuditModel.mode = 'Misc';
    this.dkpAuditModel.targetAppUserId =
      this.selectedDkpHistoryAppUserId || history?.members[0]?.appUserId || '';
    this.dkpAuditModel.relatedLedgerEntryId = null;
    this.dkpAuditModel.sourceWindowEventId = null;
    this.dkpAuditModel.amount = null;
    this.dkpAuditModel.reason = '';
    this.activity.clearDkpAuditAddCandidates();
    this.isDkpAuditOpen = true;
  }

  protected closeDkpAudit(): void {
    this.isDkpAuditOpen = false;
  }

  protected onDkpAuditModeChange(mode: 'Adjust' | 'Add' | 'Misc'): void {
    this.dkpAuditModel.mode = mode;
    if (mode !== 'Adjust') {
      this.dkpAuditModel.relatedLedgerEntryId = null;
    }
    if (mode !== 'Add') {
      this.dkpAuditModel.sourceWindowEventId = null;
    } else {
      void this.loadDkpAuditAddCandidates();
    }
  }

  // Reload the "Add" snapshot candidates when the target member changes (the
  // candidate list is per-member: it excludes events the member is already
  // credited for).
  protected onDkpAuditTargetChange(appUserId: string): void {
    this.dkpAuditModel.targetAppUserId = appUserId;
    this.dkpAuditModel.sourceWindowEventId = null;
    if (this.dkpAuditModel.mode === 'Add') {
      void this.loadDkpAuditAddCandidates();
    }
  }

  private async loadDkpAuditAddCandidates(): Promise<void> {
    if (this.selectedLinkshellId && this.dkpAuditModel.targetAppUserId) {
      await this.activity.loadDkpAuditAddCandidates(this.selectedLinkshellId, this.dkpAuditModel.targetAppUserId);
    }
  }

  protected dkpAuditCandidateEntries() {
    const entries = this.dkpHistory()?.entries ?? [];
    return entries.filter(entry =>
      entry.entryType === 'EventEarned' ||
      entry.entryType === 'SnapshotEarned' ||
      entry.entryType === 'LootSpent' ||
      entry.entryType === 'AuditAdjustment' ||
      entry.entryType === 'AuditMisc'
    );
  }

  protected async submitDkpAudit(): Promise<void> {
    if (!this.selectedLinkshellId) {
      this.activity.actionError.set('Select a linkshell before submitting an audit.');
      return;
    }

    if (!this.dkpAuditModel.targetAppUserId) {
      this.activity.actionError.set('Select the member this audit applies to.');
      return;
    }

    const reason = this.dkpAuditModel.reason?.trim() ?? '';
    if (!reason) {
      this.activity.actionError.set('Enter a reason for the audit.');
      return;
    }

    // "Add" credits the member for a posted snapshot — the amount comes from the
    // event server-side, so only the snapshot pick is required (no amount).
    if (this.dkpAuditModel.mode === 'Add') {
      if (!this.dkpAuditModel.sourceWindowEventId) {
        this.activity.actionError.set('Pick the snapshot entry to credit this member for.');
        return;
      }
    } else {
      const amount = this.dkpAuditModel.amount;
      if (amount === null || Number.isNaN(amount)) {
        this.activity.actionError.set('Enter a numeric amount for the audit.');
        return;
      }
      if (this.dkpAuditModel.mode === 'Adjust' && !this.dkpAuditModel.relatedLedgerEntryId) {
        this.activity.actionError.set('Pick the previous entry you want to correct.');
        return;
      }
    }

    const ok = await this.activity.submitDkpAudit({
      linkshellId: this.selectedLinkshellId,
      targetAppUserId: this.dkpAuditModel.targetAppUserId,
      mode: this.dkpAuditModel.mode,
      relatedLedgerEntryId:
        this.dkpAuditModel.mode === 'Adjust' ? this.dkpAuditModel.relatedLedgerEntryId : null,
      sourceWindowEventId:
        this.dkpAuditModel.mode === 'Add' ? this.dkpAuditModel.sourceWindowEventId : null,
      amount: this.dkpAuditModel.mode === 'Add' ? 0 : (this.dkpAuditModel.amount ?? 0),
      reason
    });

    if (ok) {
      this.selectedDkpHistoryAppUserId = this.dkpAuditModel.targetAppUserId;
      this.isDkpAuditOpen = false;
    }
  }

  protected async openHistoryDetail(historyId: number): Promise<void> {
    await this.activity.loadHistoryDetail(historyId);
  }

  protected closeHistoryDetail(): void {
    this.activity.clearHistoryDetail();
  }

  private async reloadDkpHistory(): Promise<void> {
    if (!this.selectedLinkshellId) {
      this.activity.clearDkpHistory();
      return;
    }

    const history = await this.activity.loadDkpHistory(
      this.selectedLinkshellId,
      this.selectedDkpHistoryAppUserId || null
    );

    if (!history) {
      return;
    }

    this.selectedDkpHistoryAppUserId =
      history.selectedAppUserId ||
      history.members[0]?.appUserId ||
      '';
  }
}
