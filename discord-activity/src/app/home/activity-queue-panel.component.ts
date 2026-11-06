import { CommonModule } from '@angular/common';
import {
  ChangeDetectionStrategy,
  ChangeDetectorRef,
  Component,
  DestroyRef,
  ElementRef,
  Injector,
  afterNextRender,
  inject,
  signal,
  viewChild
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { DateTimeInputComponent } from './datetime-input.component';
import {
  ActivityCreateEventInput,
  ActivityQuickJoinInput,
  DiscordActivityService
} from '../discord/discord-activity.service';
import {
  EVENT_JOB_TYPE_OPTIONS,
  EVENT_MAIN_JOB_OPTIONS,
  EVENT_SUB_JOB_OPTIONS
} from './event-job-options';
import { hasHqVariant, isHnmTierMonster } from './activity-home.types';
import { datetimeLocalToUtcIso } from '../discord/discord-activity.helpers';
import { PartySetupService } from '../discord/party-setup.service';
import { TodService } from '../discord/tod.service';
import {
  canManageLinkshellIn,
  formatLocalClockTime,
  shiftLocalDateTime
} from './activity-home.helpers';
import type { ActivityLinkedTod, ActivityPartySetupListRow, ActivityUpcomingRepop } from '../discord/discord-activity.types';
import { MarkdownTextareaComponent } from './tabs/markdown-textarea.component';
import { PartySetupEditorComponent } from './tabs/party-setup-editor.component';
import { PartySetupPanelComponent } from './tabs/party-setup-panel.component';
import { TodFormComponent } from './tabs/tod-form.component';

// Every per-camp HNM payout override, across both attendance modes. Listed once so
// resetting can clear all of them regardless of which mode's fields are on screen.
const HNM_BONUS_KEYS = [
  'hnmOpenBonusOverride',
  'hnmCloseBonusOverride',
  'hnmClaimBonusOverride',
  'hnmKillBonusOverride',
  'hnmPerWindowOverride'
] as const;

type HnmBonusKey = (typeof HNM_BONUS_KEYS)[number];

// One editable amount in the Camp DKP panel. `suffix` trails the number on the collapsed
// chips ("0.5 per window", "0.5 for claim"); `label` heads the input when editing.
interface HnmBonusField {
  readonly key: HnmBonusKey;
  readonly label: string;
  readonly suffix: string;
  readonly hint: string;
}

@Component({
  selector: 'app-activity-queue-panel',
  imports: [
    CommonModule,
    DateTimeInputComponent,
    FormsModule,
    MarkdownTextareaComponent,
    PartySetupPanelComponent,
    PartySetupEditorComponent,
    TodFormComponent
  ],
  templateUrl: './activity-queue-panel.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class ActivityQueuePanelComponent {
  protected readonly activity = inject(DiscordActivityService);
  private readonly destroyRef = inject(DestroyRef);

  // One clock for every card on the panel, ticking once a second. A signal rather than a
  // per-card setInterval: the cards come and go as the queue refreshes, and a timer owned by a
  // card outlives it. Same pattern the events tab already uses for its window countdowns.
  private readonly now = signal(Date.now());
  protected readonly partySetups = inject(PartySetupService);
  private readonly tods = inject(TodService);
  private readonly cdr = inject(ChangeDetectorRef);
  // The live-event edit form renders inside a native <dialog> opened with
  // showModal() so it escapes the .panel-tab.fade ancestor's stacking context
  // and lands in the browser's top layer, above the sticky tab bar.
  private readonly editDialog = viewChild<ElementRef<HTMLDialogElement>>('editDialog');
  private readonly injector = inject(Injector);
  // Shared Log ToD form (the same component the ToDs tab uses). The "Post ToD" button
  // on an HNM board opens it pre-filled with the event's monster.
  private readonly todForm = viewChild<TodFormComponent>('todForm');
  // Shared party-setup editor (the same dialog the Party Setup tab uses). The
  // "Create New Party Setup" button in the create-event form opens it; on save we
  // auto-select the fresh setup in the linked-setup dropdown.
  private readonly partySetupEditor = viewChild<PartySetupEditorComponent>('partySetupEditor');
  protected editingEventId: number | null = null;
  protected isEditingLiveEvent = false;
  // The event's party setup as it was when the edit form opened. Used to detect
  // a setup change on save so we can warn that it clears the existing slot
  // signups (they're keyed to the old setup's slots). null = had no setup.
  private editingOriginalPartySetupId: number | null = null;
  // What the edit form opened with, so a save can tell whether Starts really moved (see confirmTodMove).
  private editingOriginalStartLocal = '';
  private editingOriginalMonster: string | null = null;

  // Promise-based confirm dialog (window.confirm is suppressed in the Discord
  // Activity iframe). requestConfirm() resolves true/false when the user picks.
  protected readonly confirmModalOpen = signal(false);
  protected confirmTitle = '';
  protected confirmAction = '';
  protected confirmMessage = '';
  private confirmResolver: ((ok: boolean) => void) | null = null;
  protected readonly createModel: ActivityCreateEventInput = {
    linkshellId: 0,
    eventName: '',
    eventType: '',
    eventLocation: '',
    startTimeLocal: '',
    endTimeLocal: '',
    // Derived from Start + End (no longer entered directly); null when there's
    // no end time. Kept on the model so it still posts to the server.
    duration: null,
    dkpPerHour: 1,
    details: '',
    partySetupId: null,
    autoStart: false,
    countsTowardActive: true,
    monsterName: null,
    dayNumber: null,
    // Null = pay the linkshell's HNM bonus. Only non-null once "Change DKP" is used.
    hnmOpenBonusOverride: null,
    hnmCloseBonusOverride: null,
    hnmClaimBonusOverride: null,
    hnmKillBonusOverride: null,
    hnmPerWindowOverride: null,
    // Edit only, and only when the monster already re-posts. Seeded from the event's
    // repeatLeadHours in openEditEventForm.
    repostLeadHours: null
  };

  // Whether the event being edited belongs to a monster that re-posts its board on its own (an
  // enabled standing board). Decides whether "Board re-posts" is an hours box or an "Off" note:
  // the edit form adjusts the lead but never switches re-post on.
  protected editingRepostOn = false;

  // Free-text monster name when "Other" is picked in the HNM monster dropdown.
  protected customMonsterName = '';

  // Base manual event types. HNM + NM are spliced in after Sea by eventTypeOptionsForForm();
  // they are not gated on anything.
  protected readonly eventTypeOptions = ['Sky', 'Sea', 'HENM', 'Limbus', 'Dynamis', 'BCNM', 'KSNM'] as const;
  protected eventTypeSelection = '';
  protected eventTypeError = false;

  protected isCreateOpen = false;
  protected isSubmittingCreate = false;
  protected endTimeNotSpecified = false;
  // True = "No" (no party setup attached). False = "Yes" (dropdown enabled).
  // Defaults to "Yes" so the dropdown is visible on form open — most events
  // get a linked setup, so showing the picker by default saves a click.
  protected partySetupNotSpecified = false;

  // Expanded queued-event ids whose linked PartySetup detail tree should be
  // loaded + shown inline. Members click "View slots" on a card to expand.
  protected readonly expandedPartySetupIds = signal<Set<number>>(new Set());

  // Event ids whose manual ad-hoc signup form is open inside the expanded
  // party setup panel. Members click "Sign Up Manually" to reveal the form.
  protected readonly expandedManualSignupEventIds = signal<Set<number>>(new Set());

  // Party setups offered for the chosen event type: those whose type matches the
  // selected event type, PLUS universal ("Any") and untyped/legacy setups (so
  // nothing existing disappears). With no event type chosen yet, all are shown.
  // The already-linked setup is always kept so it's never silently unlinked.
  // Name of the setup the edited event is currently linked to, for the "current board" option
  // (a per-event snapshot never appears in the template list). Null on create.
  protected editingCurrentSetupName: string | null = null;

  // The event's own setup id when the template list can't represent it — i.e. it's a per-event
  // snapshot made by the live board editor. Null otherwise (create, no setup, or an ordinary
  // template that the list already offers).
  protected currentBoardSetupId(): number | null {
    const id = this.editingOriginalPartySetupId;
    if (id === null) {
      return null;
    }
    return this.availablePartySetups().some(setup => setup.id === id) ? null : id;
  }

  protected availablePartySetups(): ActivityPartySetupListRow[] {
    const all = this.partySetups.list()?.items ?? [];
    const selected = (this.createModel.eventType ?? '').trim().toLowerCase();
    const currentId = this.createModel.partySetupId;
    return all.filter(setup => {
      if (setup.id === currentId) {
        return true;
      }
      if (!selected) {
        return true;
      }
      const type = (setup.eventType ?? '').trim().toLowerCase();
      return type === '' || type === 'any' || type === selected;
    });
  }

  protected onEventTypeSelectionChange(value: string): void {
    const previous = this.eventTypeSelection;
    this.eventTypeSelection = value;
    if (value === 'Other') {
      this.createModel.eventType = '';
    } else if (value === 'NM') {
      // An NM camp is stored as EventType "HNM", exactly as the addon files its NMS presets
      // (render/callbacks_attendance.lua maps both HNMS and NMS to "HNM"). The tier is a
      // question about which monster list to show, not about what the event IS: a stored
      // type of "NM" would drop these camps out of every HNM path at once -- the sign-up
      // board's window controls, the timed auto-advance, the HNM DKP pool, and any Discord
      // channel route filtered on HNM.
      this.createModel.eventType = 'HNM';
    } else {
      this.createModel.eventType = value;
    }
    this.eventTypeError = false;

    // Switching between the two tiers re-cuts the monster list, so a monster picked from the
    // old tier is no longer on offer. Clear it rather than post a Behemoth as an NM.
    if ((value === 'NM') !== (previous === 'NM')) {
      this.createModel.monsterName = null;
      this.customMonsterName = '';
    }

    // HNM signup boards have no end/duration/DKP — blank those so a stale value
    // doesn't post. Leaving HNM clears the monster + repeat state.
    if (this.isHnmSelected()) {
      this.endTimeNotSpecified = true;
      this.createModel.endTimeLocal = '';
      this.createModel.duration = null;
      // Default the monster so the <select>'s displayed value is actually bound.
      // A native <select> whose only "selected" option is the null placeholder
      // renders the first real option (e.g. Adamantoise) without firing a change
      // event, so ngModel stays null and the "Select a monster" check trips even
      // though one looks chosen. Seeding the first real monster keeps model and
      // display in sync; the officer can still change it.
      if (!this.createModel.monsterName) {
        this.createModel.monsterName = this.monsterOptions().find(m => m !== 'Other') ?? null;
      }
      // The seeded (or already-picked) monster may have an outstanding pop — that's the start
      // time this camp is being staged for.
      this.applyRepopStartPrefill();
    } else {
      this.createModel.monsterName = null;
      this.customMonsterName = '';
    }
  }

  // HNM and NM ride in together after Sea. Both make the same kind of camp — they differ only
  // in which monsters they offer — and nothing gates them: HNM is an ordinary event type that
  // any linkshell can run.
  protected eventTypeOptionsForForm(): string[] {
    const base: string[] = [...this.eventTypeOptions];
    const afterSea = base.indexOf('Sea') + 1;
    base.splice(afterSea, 0, 'HNM', 'NM');
    return base;
  }

  protected isHnmSelected(): boolean {
    return (this.createModel.eventType ?? '').trim().toUpperCase() === 'HNM';
  }

  // Whether the NM button is the one lit, as opposed to HNM. Read off the BUTTON, not off
  // createModel.eventType -- an NM camp is stored as EventType "HNM" (see
  // onEventTypeSelectionChange), so the stored type cannot tell the two apart.
  protected isNmTierSelected(): boolean {
    return this.eventTypeSelection === 'NM';
  }

  // The linkshell's own monster catalog + the "Other" sentinel, narrowed to the tier the chosen
  // button names: HNM offers the wyrms and the three NQ/HQ families, NM offers everything else.
  //
  // Sourced from the linkshell's monster setups rather than the party-setup list endpoint. That
  // endpoint serves the RAW per-half catalog, which party setups genuinely need (a setup can target
  // Fafnir or Nidhogg separately) — this picker needs the merged form, and now also needs to see a
  // monster the linkshell added itself. The rows already arrive merged, so there is no
  // client-side merge pass here any more.
  //
  // The tier filter stays: which tier a monster belongs to is a different question from its timing.
  // The Day input only changes what the sign-up board prints (server-side), not this list.
  protected monsterOptions(): string[] {
    const setups = this.selectedLinkshellSettings()?.monsterSetups ?? [];
    const wantHnmTier = !this.isNmTierSelected();
    const inTier = setups
      .map(setup => setup.monsterName)
      .filter(monster => isHnmTierMonster(monster) === wantHnmTier);

    // The monster an edited camp already carries is always on offer, even when the linkshell's
    // catalog no longer lists it (a monster setup renamed or deleted since, or a camp made with
    // a name the catalog never had). Without this the picker would open on "Select a monster"
    // for a camp that plainly has one, and saving would then re-monster it by accident.
    const current = (this.createModel.monsterName ?? '').trim();
    if (current && current !== 'Other'
        && !inTier.some(monster => monster.trim().toLowerCase() === current.toLowerCase())) {
      inTier.unshift(current);
    }

    return [...inTier, 'Other'];
  }

  // Whether this option is the picked one. Case-insensitive because the stored
  // AssignedMonsterName is whatever the camp was made with (addon, Discord modal, import),
  // which need not match the catalog's casing letter for letter.
  protected isMonsterSelected(monster: string): boolean {
    const current = (this.createModel.monsterName ?? '').trim().toLowerCase();
    return current.length > 0 && monster.trim().toLowerCase() === current;
  }

  // Native-select change handler for the monster dropdown. Driven by the option's
  // string value (and an empty-string placeholder) rather than ngModel/ngValue, so
  // the displayed value can never desync from createModel.monsterName — the cause of
  // the old "you picked one but it says Select a monster" bug.
  protected onMonsterSelect(value: string): void {
    this.createModel.monsterName = value ? value : null;
    this.applyRepopStartPrefill();
  }

  protected onCustomMonsterNameChange(): void {
    this.applyRepopStartPrefill();
  }

  protected isOtherMonsterSelected(): boolean {
    return (this.createModel.monsterName ?? '') === 'Other';
  }

  // Day counts a monster's POP CYCLE, and only the three NQ/HQ families have one (mirrors
  // HnmConfig.HnmDayCycles: Nidhogg, King Behemoth, Aspidochelone). Tiamat, Cerberus, a
  // linkshell's own monster -- none of them have a day to be on, so the box is hidden rather
  // than collecting a number the board would then print. Same predicate the ToD form's Day box
  // and HQ toggle use. A custom "Other" monster is read off the typed name, so typing one of
  // the pairs by hand still offers it.
  protected dayNumberVisible(): boolean {
    if (!this.isHnmSelected() || this.isNmTierSelected()) {
      return false;
    }
    const picked = this.isOtherMonsterSelected()
      ? this.customMonsterName
      : (this.createModel.monsterName ?? '');
    return hasHqVariant(picked);
  }

  // ===== Predicted-repop pre-fill for Start =====
  //
  // A camp is almost always staged for a pop the linkshell has already predicted: a ToD was
  // logged, so its repop time IS the start time. Picking that monster fills Start in with it,
  // which is also the instant HnmAutoEventService stamps when it builds the event itself — so a
  // hand-made camp lands on the same schedule an auto-made one would.

  // The linkshell's outstanding pops, loaded when the form opens (see loadUpcomingRepops).
  protected readonly upcomingRepops = signal<ActivityUpcomingRepop[]>([]);

  // The exact value this component last wrote into Start. Anything else in the box was typed by
  // the officer (or belongs to the event being edited) and is never overwritten.
  private autoFilledStartTimeLocal: string | null = null;

  private loadUpcomingRepops(): void {
    this.upcomingRepops.set([]);
    const linkshellId = this.createModel.linkshellId;
    if (!linkshellId) {
      return;
    }

    void this.tods.loadUpcomingRepops(linkshellId).then(entries => {
      this.upcomingRepops.set(entries);
      this.applyRepopStartPrefill();
      this.cdr.markForCheck();
    });
  }

  // The outstanding pop the picked monster is waiting on, or null. Matched against the server's
  // matchNames (every spelling of the spawn) so a "Fafnir" ToD answers a "Fafnir/Nidhogg" camp,
  // and on each half of a combined pick so it works the other way round too.
  protected selectedUpcomingRepop(): ActivityUpcomingRepop | null {
    if (!this.isHnmSelected()) {
      return null;
    }

    const picked = (this.isOtherMonsterSelected()
      ? this.customMonsterName
      : (this.createModel.monsterName ?? '')).trim().toLowerCase();
    if (!picked) {
      return null;
    }

    const spellings = new Set<string>([picked]);
    for (const segment of picked.split('/')) {
      const trimmed = segment.trim();
      if (trimmed) {
        spellings.add(trimmed);
      }
    }

    return this.upcomingRepops().find(entry =>
      entry.matchNames.some(name => spellings.has(name.trim().toLowerCase()))) ?? null;
  }

  // Whether Start already sits on the picked monster's predicted pop.
  protected isStartAtUpcomingRepop(): boolean {
    const repop = this.selectedUpcomingRepop();
    return !!repop
      && this.startWithAdditionalSeconds() === this.activity.toViewerLocalInputValue(repop.repopTime, true);
  }

  // Write the predicted pop into Start, but only into an EMPTY box or over this component's own
  // previous pre-fill. Editing an event opens with its real start time in there, and an officer
  // who typed a time meant it — neither may be silently rewritten by changing the monster.
  private applyRepopStartPrefill(): void {
    const repop = this.selectedUpcomingRepop();
    if (!repop) {
      return;
    }

    const current = this.createModel.startTimeLocal ?? '';
    if (current && current !== this.autoFilledStartTimeLocal) {
      return;
    }

    this.setStartTimeFromRepop(repop);
  }

  // The "Use this time" button next to the hint: the explicit version of the pre-fill, for when
  // the box already holds something this component won't overwrite on its own.
  protected useUpcomingRepopStartTime(): void {
    const repop = this.selectedUpcomingRepop();
    if (repop) {
      this.setStartTimeFromRepop(repop);
    }
  }

  private setStartTimeFromRepop(repop: ActivityUpcomingRepop): void {
    const value = this.activity.toViewerLocalInputValue(repop.repopTime, true);
    if (!value) {
      return;
    }

    // A repop already includes the seconds, and the box holds the time without them.
    const withoutSeconds = shiftLocalDateTime(value, -this.selectedMonsterAdditionalSeconds());
    this.createModel.startTimeLocal = withoutSeconds;
    this.autoFilledStartTimeLocal = withoutSeconds;
    this.recomputeDuration();
  }

  // ===== Additional Seconds =====
  //
  // The Starts box holds the time WITHOUT the monster's Additional Seconds. The rest of the sum is
  // printed beside it ("+ 16 sec = 1:00:16 PM") and the total is what gets saved. Opening a saved event
  // takes the seconds back off for the box, so saving it unchanged lands on the same total and
  // repeated edits can never push the camp later.

  // The picked monster's Additional Seconds, matched on either half of a combined setup so a camp
  // that still stores "Fafnir" finds the "Fafnir/Nidhogg" row.
  protected selectedMonsterAdditionalSeconds(): number {
    const picked = (this.createModel.monsterName ?? '').trim().toLowerCase();
    if (!this.isHnmSelected() || this.isOtherMonsterSelected() || !picked) {
      return 0;
    }
    const setup = (this.selectedLinkshellSettings()?.monsterSetups ?? []).find(candidate => {
      const name = candidate.monsterName.trim().toLowerCase();
      return name === picked || name.split('/').some(half => half.trim() === picked);
    });
    return Math.max(0, Math.floor(Number(setup?.additionalSeconds) || 0));
  }

  // What the event actually starts at: the box plus the seconds.
  private startWithAdditionalSeconds(): string {
    const start = this.createModel.startTimeLocal ?? '';
    return start ? shiftLocalDateTime(start, this.selectedMonsterAdditionalSeconds()) : '';
  }

  // The total's clock time for beside the box. Null with no time in the box, or no seconds to add.
  protected startTotalTime(): string | null {
    const start = this.createModel.startTimeLocal ?? '';
    if (!start || this.selectedMonsterAdditionalSeconds() <= 0) {
      return null;
    }
    return formatLocalClockTime(this.startWithAdditionalSeconds());
  }

  // ===== HNM camp DKP =====
  //
  // What a camp pays is configured once per linkshell, in whichever shape its attendance
  // mode uses. The form lists those amounts as this camp's defaults; "Change DKP" opens
  // them for editing and what the officer types is stored per-camp on the event, which
  // both finalizers prefer over the linkshell value.
  //
  // The two modes pay on the same SHAPE now — a per-window rate plus open / close / claim / kill —
  // so both lists carry all five. What differs is which linkshell setting each override falls back
  // to (hnmLinkshellBonus below) and what "earning" one means: Standard reads the addon's window
  // scans, Manual Check In reads the member's Check In / Check Out range. One override column per
  // amount, shared across the modes; a camp only ever runs in one of them.
  // Order is DISPLAY ONLY -- every consumer reads `key`, never the index: the Open, then what a
  // window in between is worth, then the Close and the two outcome bonuses.
  // "in 2h 14m 09s" for a queued board, counting down to the pop it is queued for.
  //
  // startTime is the same field the card already prints as "Starts", so a re-posted board needs
  // no special case: reviving one rewrites StartTime to the new repop, and this follows it.
  //
  // Returns null when there is nothing to count to, so the template can omit the whole block
  // rather than render an empty one.
  protected startCountdown(startsAtIso: string | null | undefined): string | null {
    if (!startsAtIso) return null;
    const startsAt = new Date(startsAtIso).getTime();
    if (!Number.isFinite(startsAt)) return null;

    const remainingMs = startsAt - this.now();
    if (remainingMs <= 0) return 'due now';

    const totalSeconds = Math.floor(remainingMs / 1000);
    const days = Math.floor(totalSeconds / 86400);
    const hours = Math.floor((totalSeconds % 86400) / 3600);
    const minutes = Math.floor((totalSeconds % 3600) / 60);
    const seconds = totalSeconds % 60;

    // Days only when there are any -- a board three hours out should not read "0d".
    const pad = (n: number) => n.toString().padStart(2, '0');
    return days > 0
      ? `in ${days}d ${hours}h ${pad(minutes)}m`
      : `in ${hours}h ${pad(minutes)}m ${pad(seconds)}s`;
  }

  private static readonly STANDARD_BONUS_FIELDS = [
    { key: 'hnmOpenBonusOverride', label: 'Open', suffix: 'for open', hint: 'On the roster when the camp opens' },
    { key: 'hnmPerWindowOverride', label: 'Regular window', suffix: 'per window', hint: 'Every window the member is scanned in, open and close included' },
    { key: 'hnmCloseBonusOverride', label: 'Close', suffix: 'for close', hint: 'On the roster when the camp closes' },
    { key: 'hnmClaimBonusOverride', label: 'Claim', suffix: 'for claim', hint: 'Camp claimed, and present at close' },
    { key: 'hnmKillBonusOverride', label: 'Kill', suffix: 'for kill', hint: 'Camp killed, and present at close' }
  ] as const;

  private static readonly MANUAL_CHECK_IN_BONUS_FIELDS = [
    { key: 'hnmOpenBonusOverride', label: 'Open', suffix: 'for open', hint: 'Checked in from window 1' },
    { key: 'hnmPerWindowOverride', label: 'Per window', suffix: 'per window', hint: 'Each window the member is checked in for' },
    { key: 'hnmCloseBonusOverride', label: 'Close', suffix: 'for close', hint: 'Still checked in at the camp\'s last window' },
    { key: 'hnmClaimBonusOverride', label: 'Claim', suffix: 'for claim', hint: 'Paid once when the camp is claimed' },
    { key: 'hnmKillBonusOverride', label: 'Kill', suffix: 'for kill', hint: 'Paid once when the camp is killed' }
  ] as const;

  protected hnmDkpOverrideOpen = false;

  protected hnmBonusFields(): readonly HnmBonusField[] {
    if (this.isWdAttendanceMode()) {
      return ActivityQueuePanelComponent.MANUAL_CHECK_IN_BONUS_FIELDS;
    }
    // An Open/Close monster's camp takes just an Open and a Close, so the Regular window
    // rate is never paid on it — offering the box would invite a price that changes nothing.
    return this.selectedMonsterWindowMethod() === 'OpenClose'
      ? ActivityQueuePanelComponent.STANDARD_BONUS_FIELDS.filter(field => field.key !== 'hnmPerWindowOverride')
      : ActivityQueuePanelComponent.STANDARD_BONUS_FIELDS;
  }

  // The picked monster's Window Method from the linkshell's monster setups, or null when no
  // monster is picked, it is "Other", or the server predates the field.
  private selectedMonsterWindowMethod(): string | null {
    const picked = (this.createModel.monsterName ?? '').trim().toLowerCase();
    if (!picked) return null;
    const setup = (this.selectedLinkshellSettings()?.monsterSetups ?? [])
      .find(candidate => candidate.monsterName.trim().toLowerCase() === picked);
    return setup?.windowMethod ?? null;
  }

  private selectedLinkshellSettings() {
    return this.linkshellMemberships().find(link => link.id === this.createModel.linkshellId)?.settings;
  }

  // Fail-closed to Standard, matching the server (Linkshell.HnmAttendanceMode).
  protected isWdAttendanceMode(): boolean {
    return (this.selectedLinkshellSettings()?.hnmAttendanceMode ?? 'Standard') === 'Wd';
  }

  // The linkshell default for one amount — what the camp pays when nothing is overridden.
  //
  // EVERY key reads from the pair belonging to the active mode: the linkshell stores its Standard
  // and Manual Check In amounts separately even though the override column is shared, so which
  // default an override falls back to is decided by the camp's mode and nothing else. Mirrors
  // HnmCampPricing.StandardBonuses / WdAmounts, which is the authority at payout.
  protected hnmLinkshellBonus(key: HnmBonusKey): number {
    const settings = this.selectedLinkshellSettings();
    const isWd = this.isWdAttendanceMode();
    switch (key) {
      case 'hnmOpenBonusOverride':
        return (isWd ? settings?.wdOpenBonus : settings?.hnmStandardOpenBonus) ?? 0;
      case 'hnmCloseBonusOverride':
        return (isWd ? settings?.wdCloseBonus : settings?.hnmStandardCloseBonus) ?? 0;
      case 'hnmPerWindowOverride':
        return (isWd ? settings?.wdDkpPerWindow ?? 0.25 : settings?.hnmStandardWindowBonus ?? 0);
      case 'hnmClaimBonusOverride':
        return (isWd ? settings?.wdClaimBonus : settings?.hnmStandardClaimBonus) ?? 0;
      case 'hnmKillBonusOverride':
        return (isWd ? settings?.wdKillBonus : settings?.hnmStandardKillBonus) ?? 0;
    }
  }

  // What this camp will actually pay: its own override, else the linkshell's.
  protected hnmEffectiveBonus(key: HnmBonusKey): number {
    return this.createModel[key] ?? this.hnmLinkshellBonus(key);
  }

  protected hasHnmBonusOverrides(): boolean {
    return this.hnmBonusFields().some(field => this.createModel[field.key] != null);
  }

  // Opening seeds each box with the linkshell default, so the officer edits real numbers
  // rather than blanks and every field is explicit about what the camp pays.
  protected openHnmDkpOverride(): void {
    if (!this.hasHnmBonusOverrides()) {
      for (const field of this.hnmBonusFields()) {
        this.createModel[field.key] = this.hnmLinkshellBonus(field.key);
      }
    }
    this.hnmDkpOverrideOpen = true;
  }

  // Clearing every override sends nulls, which the server reads as "back to the linkshell
  // default" rather than "leave whatever was there". Clears BOTH modes' keys, so switching
  // a linkshell's mode can't strand an override the panel no longer shows.
  protected resetHnmDkpOverride(): void {
    for (const key of HNM_BONUS_KEYS) {
      this.createModel[key] = null;
    }
    this.hnmDkpOverrideOpen = false;
  }

  protected onPartySetupNotSpecifiedChange(checked: boolean): void {
    this.partySetupNotSpecified = checked;
    if (checked) {
      this.createModel.partySetupId = null;
    }
  }

  // Open the shared party-setup editor to build a new setup without leaving the
  // create-event form (same dialog the Party Setup tab uses).
  protected openCreatePartySetup(): void {
    this.partySetupEditor()?.openForCreate();
  }

  // The editor emitted a saved setup id. Its create() already reloaded the list
  // (so availablePartySetups() now includes it) — just select it and make sure the
  // dropdown is showing (Party Setup = Yes). OnPush: markForCheck so the newly
  // selected value + preview render immediately.
  protected onPartySetupCreated(setupId: number): void {
    this.partySetupNotSpecified = false;
    this.createModel.partySetupId = setupId;
    this.cdr.markForCheck();
  }

  protected onEndTimeNotSpecifiedChange(checked: boolean): void {
    this.endTimeNotSpecified = checked;
    if (checked) {
      this.createModel.endTimeLocal = '';
    }
    this.recomputeDuration();
  }

  protected onStartTimeChange(): void {
    // Typing in the box takes ownership of it, so switching monsters afterwards can't overwrite
    // what the officer entered (see applyRepopStartPrefill).
    if ((this.createModel.startTimeLocal ?? '') !== this.autoFilledStartTimeLocal) {
      this.autoFilledStartTimeLocal = null;
    }
    this.recomputeDuration();
  }

  protected onEndTimeChange(): void {
    this.recomputeDuration();
  }

  // Duration is derived from Start + End (no longer entered directly) — kept on
  // the model for submission and shown as read-only text via durationLabel().
  // Null when there's no end time or the range is invalid.
  private recomputeDuration(): void {
    if (this.endTimeNotSpecified) {
      this.createModel.duration = null;
      return;
    }
    const start = this.parseLocalDateTime(this.createModel.startTimeLocal);
    const end = this.parseLocalDateTime(this.createModel.endTimeLocal);
    if (!start || !end) {
      this.createModel.duration = null;
      return;
    }
    const hours = (end.getTime() - start.getTime()) / 3_600_000;
    this.createModel.duration = hours >= 0 ? Math.round(hours * 100) / 100 : null;
  }

  // ± buttons on the DKP/hour stepper. The middle stays a typable number input,
  // so this only has to clamp at zero — negative payouts are never intended.
  protected adjustDkpPerHour(delta: number): void {
    const current = Number(this.createModel.dkpPerHour) || 0;
    this.createModel.dkpPerHour = Math.max(0, current + delta);
  }

  // Human-readable Start→End span for the read-only Duration field ("3h 30m").
  protected durationLabel(): string {
    const hours = this.createModel.duration;
    if (hours == null || hours <= 0) {
      return 'Not Specified';
    }
    const totalMinutes = Math.round(hours * 60);
    const h = Math.floor(totalMinutes / 60);
    const m = totalMinutes % 60;
    if (h > 0 && m > 0) {
      return `${h}h ${m}m`;
    }
    return h > 0 ? `${h}h` : `${m}m`;
  }

  private parseLocalDateTime(value: string | null | undefined): Date | null {
    if (!value) return null;
    const trimmed = value.trim();
    if (!trimmed) return null;
    const parsed = new Date(trimmed);
    return Number.isNaN(parsed.getTime()) ? null : parsed;
  }

  protected readonly mainJobOptions = [...EVENT_MAIN_JOB_OPTIONS];
  protected readonly subJobOptions = [...EVENT_SUB_JOB_OPTIONS];
  protected readonly jobTypeOptions = [...EVENT_JOB_TYPE_OPTIONS];

  // Per-event draft of the user's job selection for pending events that have
  // no pre-defined party setup. Mirrors the late-join draft pattern used in
  // activity-home; lazily seeded so the template can two-way bind directly.
  protected readonly signupDrafts: Record<number, ActivityQuickJoinInput> = {};


  protected getSignupDraft(eventId: number): ActivityQuickJoinInput {
    let draft = this.signupDrafts[eventId];
    if (!draft) {
      draft = { jobName: '', subJobName: '', jobType: '' };
      this.signupDrafts[eventId] = draft;
    }
    return draft;
  }

  protected isSignupDraftComplete(eventId: number): boolean {
    const draft = this.signupDrafts[eventId];
    return !!(draft && draft.jobName && draft.subJobName && draft.jobType);
  }

  protected async submitAdHocSignup(eventId: number): Promise<void> {
    const draft = this.getSignupDraft(eventId);
    if (!draft.jobName || !draft.subJobName || !draft.jobType) {
      this.activity.actionError.set('Role, main job, and sub job are required to sign up.');
      return;
    }
    await this.activity.signUpForEvent(eventId, 0, draft);
    delete this.signupDrafts[eventId];
  }

  protected queuedEvents() {
    return (this.activity.overview()?.activeEvents ?? []).filter(event => !event.commencementStartTime);
  }

  protected linkshellMemberships() {
    return this.activity.overview()?.linkshells ?? [];
  }

  protected canManageLinkshell(linkshellId: number): boolean {
    return canManageLinkshellIn(this.activity.overview(), linkshellId);
  }

  protected linkshellLootStructure(linkshellId: number): 'Dkp' | 'LootCouncil' | 'Hybrid' {
    const link = this.linkshellMemberships().find(l => l.id === linkshellId);
    return (link?.settings?.lootStructure as 'Dkp' | 'LootCouncil' | 'Hybrid') ?? 'Dkp';
  }

  protected isDkpModeForSelectedLinkshell(): boolean {
    return this.linkshellLootStructure(this.createModel.linkshellId) !== 'LootCouncil';
  }

  protected canManageAnyLinkshell(): boolean {
    return this.linkshellMemberships().some(link => this.canManageLinkshell(link.id));
  }

  // HNM events get their start time from the addon's window-post flow, not the
  // Activity. Block the click before it hits the server (which rejects HNM starts
  // anyway) and show the reason via the in-app action error — native dialogs like
  // window.alert are silently suppressed inside the Discord iframe, so the message
  // would otherwise never appear.
  protected attemptStartEvent(event: { id: number; type?: string | null }): void {
    if (this.isAddonOnlyStart(event)) {
      this.activity.actionError.set('HNM events are started with the in-game addon (Att launcher). Use /attend in-game to start this event.');
      return;
    }
    void this.activity.startEvent(event.id);
  }

  // HNM events are addon-only-startable.
  // The server-side StartEvent guard rejects this regardless, but
  // showing a disabled button is friendlier than an alert on click.
  protected isAddonOnlyStart(event: { type?: string | null }): boolean {
    return (event.type ?? '').trim().toUpperCase() === 'HNM';
  }

  // HNM boards are never "started". Instead the officer logs a ToD here, and the
  // recurring-board background service re-posts the signup board the configured hours
  // before the next pop. Pre-fill the monster from the event (its own assigned monster,
  // falling back to the linked party setup's monster for legacy/addon boards).
  // Human "1h 30m" gap between the board's auto-re-post time and the predicted pop
  // ("N before the first window"). Null when either time is missing/unparseable.
  protected repostLeadLabel(event: { startTime?: string | null; hnmRepostAt?: string | null }): string | null {
    if (!event.startTime || !event.hnmRepostAt) {
      return null;
    }
    const start = new Date(event.startTime).getTime();
    const repost = new Date(event.hnmRepostAt).getTime();
    if (Number.isNaN(start) || Number.isNaN(repost)) {
      return null;
    }
    // Whole minutes. The re-post is timed off the ToD, so a Start edited a few seconds off it read
    // "12h 16s" for a 12-hour lead; seconds never mean anything here.
    const totalMinutes = Math.max(0, Math.round((start - repost) / 60_000));
    const h = Math.floor(totalMinutes / 60);
    const m = totalMinutes % 60;
    const parts: string[] = [];
    if (h) { parts.push(`${h}h`); }
    if (m) { parts.push(`${m}m`); }
    return parts.length ? parts.join(' ') : '0m';
  }

  protected openPostTodForm(event: {
    id: number;
    linkshellId: number;
    assignedMonsterName?: string | null;
    partySetupAssignedMonsterName?: string | null;
    hnmAwaitingRepost?: boolean;
    sourceTodId?: number | null;
    dayNumber?: number | null;
    repeatOnTod?: boolean;
    repeatLeadHours?: number | null;
  }): void {
    const monster = event.assignedMonsterName ?? event.partySetupAssignedMonsterName ?? null;
    const dayNumber = event.dayNumber ?? null;
    // The monster's standing re-post settings, so the board ToD form opens on what the
    // create-event form (or the last End Camp) actually configured rather than on "off".
    const repeatOnTod = event.repeatOnTod ?? false;
    const repeatLeadHours = event.repeatLeadHours ?? null;
    // Already defeated/awaiting re-post → "Edit ToD": pre-fill the board's logged ToD.
    if (event.hnmAwaitingRepost && event.sourceTodId != null) {
      const tod = (this.activity.overview()?.recentTods ?? []).find(t => t.id === event.sourceTodId);
      if (tod) {
        this.todForm()?.openEditForBoard(tod, event.id, monster ?? '', dayNumber, repeatOnTod, repeatLeadHours);
        return;
      }
    }
    if (monster) {
      this.todForm()?.openForBoard(
        event.linkshellId, monster, event.id, dayNumber, null, repeatOnTod, repeatLeadHours);
    } else {
      this.todForm()?.openCreate(event.linkshellId, null);
    }
  }

  protected isMemberMode(): boolean {
    return !this.canManageAnyLinkshell();
  }

  // Public: the Event System header's "Create Event" button (EventsTabComponent) opens this
  // panel's form; the panel no longer carries a button of its own.
  openCreateForm(): void {
    this.activity.clearActionState();
    this.isCreateOpen = true;
    this.editingEventId = null;
    // Always bind a new event to the active linkshell (selected in
    // Configurations) — the Linkshell field is read-only in the form.
    this.createModel.linkshellId =
      this.activity.overview()?.primaryLinkshell?.id ??
      this.activity.overview()?.linkshells?.[0]?.id ??
      0;
    // Lazy-load the linkshell's PartySetup list so the dropdown is populated.
    if (this.createModel.linkshellId) {
      void this.partySetups.loadList(this.createModel.linkshellId);
    }
    this.autoFilledStartTimeLocal = null;
    this.loadUpcomingRepops();
    // Called from outside (the Event System header) through a viewChild, so OnPush wouldn't
    // otherwise re-check this panel and the dialog would never render. Same as openEditEventForm.
    this.cdr.markForCheck();
    this.showEditDialog();
  }

  // Show the shared create/edit form as a native modal <dialog>, once Angular has rendered it
  // (from the @if (isCreateOpen) branch) — before that the viewChild is still undefined.
  // afterNextRender rather than setTimeout: with eventCoalescing (app.config) the render after a
  // click waits for the next animation frame, which a zero-delay timer can beat, and then the
  // dialog rendered but never opened (seen when Create Event moved to the Event System header).
  private showEditDialog(): void {
    afterNextRender(() => {
      const dialog = this.editDialog()?.nativeElement;
      if (dialog && !dialog.open) {
        dialog.showModal();
      }
    }, { injector: this.injector });
  }

  // Display name for the read-only Linkshell field, resolved from the model's
  // linkshellId (the active linkshell for new events, or the event's own
  // linkshell when editing).
  protected createLinkshellName(): string {
    const id = this.createModel.linkshellId;
    return this.activity.overview()?.linkshells?.find(l => l.id === id)?.name
      ?? this.activity.overview()?.primaryLinkshell?.name
      ?? '—';
  }

  protected closeCreateForm(): void {
    this.editDialog()?.nativeElement.close();
    this.isCreateOpen = false;
    this.editingEventId = null;
    this.isEditingLiveEvent = false;
  }

  // Native <dialog> click events fire on both the dialog box and its
  // ::backdrop. The backdrop is the dialog itself, so an event whose target is
  // the dialog element (not a child) means the user clicked outside the form.
  protected onEditDialogClick(event: MouseEvent): void {
    if (event.target === this.editDialog()?.nativeElement) {
      this.closeCreateForm();
    }
  }

  public constructor() {
    const intervalId = window.setInterval(() => this.now.set(Date.now()), 1000);
    this.destroyRef.onDestroy(() => window.clearInterval(intervalId));
  }

  protected async submitCreateForm(): Promise<void> {
    // Clear any banner from a prior attempt before re-validating, so a corrected
    // submit never shows a stale error (e.g. an old "Select a monster" message).
    this.activity.clearActionState();
    const eventType = this.createModel.eventType?.trim() ?? '';
    if (!this.eventTypeSelection || !eventType) {
      this.eventTypeError = true;
      return;
    }
    this.eventTypeError = false;
    this.createModel.eventType = eventType;

    // HNM signup board: resolve the monster (free text when "Other") and require it.
    let monsterName: string | null = null;
    if (this.isHnmSelected()) {
      monsterName = this.isOtherMonsterSelected()
        ? (this.customMonsterName.trim() || null)
        : (this.createModel.monsterName ?? null);
      if (!monsterName) {
        this.activity.actionError.set('Select a monster for the HNM event.');
        return;
      }
    }

    const nextPartySetupId = this.partySetupNotSpecified ? null : (this.createModel.partySetupId ?? null);

    // Editing an event that already had a party setup, and the setup is being
    // changed/removed: the current slot signups belong to the OLD setup's slots, so the
    // server wipes them and the board re-posts empty. Participation (and with it every
    // timer, DKP award and attendance credit) survives untouched — the wipe is the board's
    // seating only. Warn and let the user back out.
    if (this.editingEventId
        && this.editingOriginalPartySetupId !== null
        && nextPartySetupId !== this.editingOriginalPartySetupId) {
      const ok = await this.requestConfirm(
        this.isEditingLiveEvent
          ? 'Changing the party setup wipes the live sign-up board — every slot is cleared and members have to claim again. Their participation, timers and DKP are not affected. Continue?'
          : 'Changing the party setup wipes the sign-up board — everyone currently signed up loses their slot and has to sign up again. Continue?');
      if (!ok) {
        return;
      }
    }

    const todMove = await this.confirmTodMove(monsterName);
    if (!todMove.proceed) {
      return;
    }

    this.isSubmittingCreate = true;

    try {
      const payload: ActivityCreateEventInput = {
        ...this.createModel,
        moveTodId: todMove.todId,
        // The total the form printed beside the box, not the box itself.
        startTimeLocal: this.startWithAdditionalSeconds(),
        partySetupId: nextPartySetupId,
        monsterName,
        // A day the form didn't ask for is not a day the board should print: switching the
        // monster from a merge pair to anything else must not post the number that was typed
        // while the box was still on offer. The model keeps it, so switching back restores it.
        dayNumber: this.dayNumberVisible() ? this.createModel.dayNumber : null,
        // Only what the "Board re-posts" box showed. Anything else -- a live camp, a monster that
        // does not re-post, the create form -- sends nothing and the lead stays as it is.
        repostLeadHours: this.showRepostLeadField() && this.editingRepostOn
          ? this.createModel.repostLeadHours ?? null
          : null
      };
      if (this.editingEventId) {
        await this.activity.updateEvent(this.editingEventId, payload);
      } else {
        await this.activity.createEvent(payload);
      }
      this.resetCreateModel();
      this.isCreateOpen = false;
      this.editingEventId = null;
      this.isEditingLiveEvent = false;
    } finally {
      this.isSubmittingCreate = false;
    }
  }

  // Starts on an HNM event IS a ToD's repop, so changing it moves that ToD on the ToD page too, and
  // everyone in the linkshell sees the new time. The officer is told exactly what changes before
  // anything is saved, and the ToD only moves if they agree. Cancel goes back to the form.
  private async confirmTodMove(monsterName: string | null): Promise<{ proceed: boolean; todId: number | null }> {
    const nothingToMove = { proceed: true, todId: null };
    const toSeconds = (ms: number) => Math.floor(ms / 1000);

    const newRepop = Date.parse(datetimeLocalToUtcIso(this.startWithAdditionalSeconds()) ?? '');
    const oldStart = Date.parse(datetimeLocalToUtcIso(this.editingOriginalStartLocal) ?? '');
    if (!this.editingEventId
        || !this.isHnmSelected()
        || monsterName !== this.editingOriginalMonster
        || Number.isNaN(newRepop)
        || toSeconds(newRepop) === toSeconds(oldStart)) {
      return nothingToMove;
    }

    let tod: ActivityLinkedTod | null;
    try {
      tod = await this.tods.loadLinkedTod(this.editingEventId);
    } catch {
      this.activity.actionError.set('Could not look up the ToD for this event, so nothing was saved. Please try again.');
      return { proceed: false, todId: null };
    }

    const oldRepop = tod ? Date.parse(tod.repopTime) : NaN;
    if (!tod || toSeconds(oldRepop) === toSeconds(newRepop)) {
      return nothingToMove;
    }

    const format = (ms: number) => this.activity.formatDateTimeWithSeconds(new Date(ms).toISOString());
    const lines = [`Saving also changes the ${tod.monsterName} ToD on the ToD page, for everyone in the linkshell.`, ''];
    if (tod.time) {
      const oldDeath = Date.parse(tod.time);
      lines.push(`Time of Death: ${format(oldDeath)} → ${format(oldDeath + newRepop - oldRepop)}`);
    }
    lines.push(`Repop: ${format(oldRepop)} → ${format(newRepop)}`);

    const ok = await this.requestConfirm(lines.join('\n'), 'Change the ToD too?', 'Change ToD & save');
    return ok ? { proceed: true, todId: tod.todId } : { proceed: false, todId: null };
  }

  // "Board re-posts" is offered when editing a QUEUED HNM board. Not on create (a new board has no
  // re-post cycle yet) and not on a live camp (its next re-post is set when it ends).
  protected showRepostLeadField(): boolean {
    return this.isHnmSelected() && this.editingEventId !== null && !this.isEditingLiveEvent;
  }

  // When the board would re-post with the lead in the box: the first window minus the lead.
  // Null until both are filled in, so the hint only shows a real time.
  protected repostPreview(): string | null {
    const lead = Number(this.createModel.repostLeadHours);
    const startIso = datetimeLocalToUtcIso(this.startWithAdditionalSeconds());
    if (!startIso || !Number.isFinite(lead) || lead <= 0) {
      return null;
    }
    const start = Date.parse(startIso);
    return Number.isNaN(start) ? null : new Date(start - lead * 3_600_000).toISOString();
  }

  public openEditEventForm(event: {
    id: number;
    linkshellId: number;
    name?: string | null;
    type?: string | null;
    location?: string | null;
    startTime?: string | null;
    endTime?: string | null;
    commencementStartTime?: string | null;
    duration?: number | null;
    dkpPerHour?: number | null;
    details?: string | null;
    partySetupId?: number | null;
    partySetupName?: string | null;
    autoStart?: boolean;
    countsTowardActive?: boolean;
    assignedMonsterName?: string | null;
    dayNumber?: number | null;
    hnmNextDay?: number | null;
    hnmOpenBonusOverride?: number | null;
    hnmCloseBonusOverride?: number | null;
    hnmClaimBonusOverride?: number | null;
    hnmKillBonusOverride?: number | null;
    hnmPerWindowOverride?: number | null;
    repeatOnTod?: boolean;
    repeatLeadHours?: number | null;
  }): void {
    this.activity.clearActionState();
    this.isCreateOpen = true;
    this.editingEventId = event.id;
    this.editingRepostOn = event.repeatOnTod ?? false;
    this.createModel.repostLeadHours = event.repeatLeadHours ?? null;
    this.isEditingLiveEvent = !!event.commencementStartTime;
    this.createModel.linkshellId = event.linkshellId;
    this.createModel.eventName = event.name ?? '';
    const incomingType = event.type ?? '';
    this.createModel.eventType = incomingType;
    if (!incomingType) {
      this.eventTypeSelection = '';
    } else if (incomingType.trim().toUpperCase() === 'HNM') {
      // An NM camp is STORED as EventType "HNM" (see onEventTypeSelectionChange), so the
      // stored type can't tell the two tiers apart — the monster can. Lighting the wrong chip
      // would cut the monster out of monsterOptions()' tier filter and open the picker on
      // "Select a monster" for a camp that has one.
      this.eventTypeSelection = event.assignedMonsterName && !isHnmTierMonster(event.assignedMonsterName)
        ? 'NM'
        : 'HNM';
    } else {
      this.eventTypeSelection = (this.eventTypeOptions as readonly string[]).includes(incomingType) ? incomingType : 'Other';
    }
    this.eventTypeError = false;
    this.createModel.eventLocation = event.location ?? '';
    // Seconds included: both inputs are step="1", and an HNM pop time is second-precise —
    // re-filling at :00 would quietly move the camp's start every time it was edited.
    const savedStartLocal = this.activity.toViewerLocalInputValue(event.startTime ?? null, true);
    this.createModel.endTimeLocal = this.activity.toViewerLocalInputValue(event.endTime ?? null, true);
    // The box holds the time without the seconds, which are read off the monster, so this waits
    // until the monster is set below.
    this.createModel.startTimeLocal = savedStartLocal;
    // Addon-created (HNM) events come back with no end time; the "N/A" toggle
    // reflects that, and the duration is derived from Start + End below.
    this.endTimeNotSpecified = !this.createModel.endTimeLocal;
    this.createModel.partySetupId = event.partySetupId ?? null;
    this.editingOriginalPartySetupId = event.partySetupId ?? null;
    this.editingCurrentSetupName = event.partySetupName ?? null;
    this.partySetupNotSpecified = event.partySetupId == null;
    this.createModel.autoStart = event.autoStart ?? false;
    this.createModel.countsTowardActive = event.countsTowardActive ?? true;
    // HNM board fields so editing preserves the monster and day.
    this.createModel.monsterName = event.assignedMonsterName ?? null;
    this.editingOriginalMonster = event.assignedMonsterName ?? null;
    this.editingOriginalStartLocal = savedStartLocal;
    this.customMonsterName = '';
    this.createModel.dayNumber = event.hnmNextDay ?? event.dayNumber ?? null;
    this.createModel.startTimeLocal = savedStartLocal
      ? shiftLocalDateTime(savedStartLocal, -this.selectedMonsterAdditionalSeconds())
      : '';
    // Lazy-load the linkshell's PartySetup list so the dropdown populates.
    if (this.createModel.linkshellId) {
      void this.partySetups.loadList(this.createModel.linkshellId);
    }
    // The event's own start time is already in the box, so nothing is pre-filled here — the
    // outstanding pops are loaded only so the hint (and its "Use this time" button) can offer
    // the predicted repop if the monster is changed.
    this.autoFilledStartTimeLocal = null;
    this.loadUpcomingRepops();
    this.recomputeDuration();
    this.createModel.dkpPerHour = event.dkpPerHour ?? 0;
    this.createModel.details = event.details ?? '';
    // A camp priced by hand reopens with "Change DKP" already expanded, so the numbers
    // that will actually be paid are visible instead of hidden behind a button.
    this.createModel.hnmOpenBonusOverride = event.hnmOpenBonusOverride ?? null;
    this.createModel.hnmCloseBonusOverride = event.hnmCloseBonusOverride ?? null;
    this.createModel.hnmClaimBonusOverride = event.hnmClaimBonusOverride ?? null;
    this.createModel.hnmKillBonusOverride = event.hnmKillBonusOverride ?? null;
    this.createModel.hnmPerWindowOverride = event.hnmPerWindowOverride ?? null;
    this.hnmDkpOverrideOpen = this.hasHnmBonusOverrides();
    // External callers (e.g. live-event Edit on the events tab) reach this
    // method through a viewChild — Angular's OnPush check for the queue panel
    // wouldn't otherwise run on this synchronous mutation.
    this.cdr.markForCheck();
    this.showEditDialog();
  }

  // Two-stage inline confirmation for cancelling a queued event.
  // window.confirm() is suppressed in the Discord Activity iframe (no
  // `allow-modals`), so a first click flags the event and the template
  // swaps the Cancel button out for a Confirm/Keep pair. Second click
  // on Confirm calls the API.
  protected readonly pendingCancelEventId = signal<number | null>(null);

  protected requestCancelEvent(eventId: number): void {
    this.pendingCancelEventId.set(eventId);
  }

  protected abortCancelEvent(): void {
    this.pendingCancelEventId.set(null);
  }

  protected async confirmCancelEvent(eventId: number): Promise<void> {
    this.pendingCancelEventId.set(null);
    await this.activity.cancelEvent(eventId);
  }

  private resetCreateModel(): void {
    const defaultLinkshellId =
      this.activity.overview()?.primaryLinkshell?.id ??
      this.activity.overview()?.linkshells?.[0]?.id ??
      0;

    this.createModel.linkshellId = defaultLinkshellId;
    this.createModel.eventName = '';
    this.createModel.eventType = '';
    this.eventTypeSelection = '';
    this.eventTypeError = false;
    this.createModel.eventLocation = '';
    this.createModel.startTimeLocal = '';
    this.createModel.endTimeLocal = '';
    this.createModel.duration = null;
    this.createModel.dkpPerHour = 1;
    this.createModel.details = '';
    this.endTimeNotSpecified = false;
    this.partySetupNotSpecified = false;
    this.createModel.partySetupId = null;
    this.createModel.autoStart = false;
    this.createModel.countsTowardActive = true;
    this.createModel.monsterName = null;
    this.createModel.dayNumber = null;
    this.createModel.repostLeadHours = null;
    this.editingRepostOn = false;
    this.customMonsterName = '';
    this.isEditingLiveEvent = false;
    this.editingOriginalPartySetupId = null;
    this.editingOriginalStartLocal = '';
    this.editingOriginalMonster = null;
    this.editingCurrentSetupName = null;
    this.autoFilledStartTimeLocal = null;
    this.resetHnmDkpOverride();
  }

  // Opens the confirm dialog and resolves once the user chooses. Any pending
  // confirm is rejected (false) first so a stale promise can't dangle.
  private requestConfirm(
    message: string,
    title = 'Change party setup?',
    action = 'Move to no-slot & save'
  ): Promise<boolean> {
    this.confirmResolver?.(false);
    this.confirmTitle = title;
    this.confirmAction = action;
    this.confirmMessage = message;
    this.confirmModalOpen.set(true);
    this.cdr.markForCheck();
    return new Promise<boolean>(resolve => {
      this.confirmResolver = resolve;
    });
  }

  protected resolveConfirm(ok: boolean): void {
    this.confirmModalOpen.set(false);
    const resolve = this.confirmResolver;
    this.confirmResolver = null;
    resolve?.(ok);
    this.cdr.markForCheck();
  }

  // ----- Inline PartySetup slot tree (members expand a queued event to sign
  // up against the planned slots — uses the embedded PartySetupPanelComponent
  // which renders the exact same alliance → parties → slots tree as the
  // Party Setup tab, and shares its signUp / withdraw flow.
  // Keyed by EVENT id (not party-setup id) so expanding one queued event only
  // opens that card's slots — several events can share the same party setup,
  // and keying by setup id would expand them all at once.
  protected togglePartySetupExpanded(eventId: number, linkshellId: number): void {
    const next = new Set(this.expandedPartySetupIds());
    if (next.has(eventId)) {
      next.delete(eventId);
    } else {
      next.add(eventId);
      // The embedded panel pulls option lists (role / main / sub) from
      // partySetups.list(); load it once so the sign-up dropdowns populate
      // even when the user hasn't visited the Party Setup tab yet.
      if (linkshellId) void this.partySetups.loadList(linkshellId);
    }
    this.expandedPartySetupIds.set(next);
  }

  protected isPartySetupExpanded(eventId: number): boolean {
    return this.expandedPartySetupIds().has(eventId);
  }

  // Manual signup form inside the expanded party setup panel — gated behind a
  // "Sign Up Manually" button so the party setup slots stay visually primary.
  protected toggleManualSignupExpanded(eventId: number): void {
    const next = new Set(this.expandedManualSignupEventIds());
    if (next.has(eventId)) {
      next.delete(eventId);
    } else {
      next.add(eventId);
    }
    this.expandedManualSignupEventIds.set(next);
  }

  protected isManualSignupExpanded(eventId: number): boolean {
    return this.expandedManualSignupEventIds().has(eventId);
  }

  // True when the current viewer already owns a slot in the linked PartySetup.
  // We hide the "Sign Up Manually" affordance in that case so a user who already
  // claimed a slot above can't double-sign as an ad-hoc participant on the same
  // event.
  protected isCurrentUserInPartySetup(partySetupId: number | null | undefined): boolean {
    if (!partySetupId) return false;
    const detail = this.partySetups.detailFor(partySetupId);
    if (!detail) return false;
    const me = this.activity.overview()?.appUser?.id;
    if (!me) return false;
    return detail.alliances.some(alliance =>
      alliance.parties.some(party =>
        party.slots.some(slot => slot.signedUpAppUserId === me)
      )
    );
  }
}
