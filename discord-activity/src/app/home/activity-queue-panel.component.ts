import { CommonModule } from '@angular/common';
import {
  ChangeDetectionStrategy,
  ChangeDetectorRef,
  Component,
  ElementRef,
  inject,
  signal,
  viewChild
} from '@angular/core';
import { FormsModule } from '@angular/forms';
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
import { PartySetupService } from '../discord/party-setup.service';
import type { ActivityPartySetupListRow } from '../discord/discord-activity.types';
import { PartySetupPanelComponent } from './tabs/party-setup-panel.component';
import { TodFormComponent } from './tabs/tod-form.component';

@Component({
  selector: 'app-activity-queue-panel',
  imports: [CommonModule, FormsModule, PartySetupPanelComponent, TodFormComponent],
  templateUrl: './activity-queue-panel.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class ActivityQueuePanelComponent {
  protected readonly activity = inject(DiscordActivityService);
  protected readonly partySetups = inject(PartySetupService);
  private readonly cdr = inject(ChangeDetectorRef);
  // The live-event edit form renders inside a native <dialog> opened with
  // showModal() so it escapes the .panel-tab.fade ancestor's stacking context
  // and lands in the browser's top layer, above the sticky tab bar.
  private readonly editDialog = viewChild<ElementRef<HTMLDialogElement>>('editDialog');
  // Shared Log ToD form (the same component the ToDs tab uses). The "Post ToD" button
  // on an HNM board opens it pre-filled with the event's monster.
  private readonly todForm = viewChild<TodFormComponent>('todForm');
  protected editingEventId: number | null = null;
  protected isEditingLiveEvent = false;
  // The event's party setup as it was when the edit form opened. Used to detect
  // a setup change on save so we can warn that it clears the existing slot
  // signups (they're keyed to the old setup's slots). null = had no setup.
  private editingOriginalPartySetupId: number | null = null;

  // Promise-based confirm dialog (window.confirm is suppressed in the Discord
  // Activity iframe). requestConfirm() resolves true/false when the user picks.
  protected readonly confirmModalOpen = signal(false);
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
    repeatOnTod: false,
    repeatLeadHours: 1
  };

  // Free-text monster name when "Other" is picked in the HNM monster dropdown.
  protected customMonsterName = '';

  // Repeat-on-ToD lead time entered as hours/minutes/seconds; combined into the wire's
  // fractional-hours repeatLeadHours on submit, and split back from it on edit.
  protected repeatLeadH = 1;
  protected repeatLeadM = 0;
  protected repeatLeadS = 0;

  private repeatLeadAsHours(): number {
    const h = Math.max(0, Math.floor(Number(this.repeatLeadH) || 0));
    const m = Math.max(0, Math.floor(Number(this.repeatLeadM) || 0));
    const s = Math.max(0, Math.floor(Number(this.repeatLeadS) || 0));
    return h + m / 60 + s / 3600;
  }

  private setRepeatLeadFromHours(hours: number | null | undefined): void {
    const totalSec = Math.max(0, Math.round((hours ?? 1) * 3600));
    this.repeatLeadH = Math.floor(totalSec / 3600);
    this.repeatLeadM = Math.floor((totalSec % 3600) / 60);
    this.repeatLeadS = totalSec % 60;
  }

  // Base manual event types. "HNM" is appended only when the active linkshell has
  // Outside Party Signup enabled (see eventTypeOptionsForForm) — a manual HNM event
  // is a no-DKP outside-signup board. The addon still auto-creates real HNM events.
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
    this.eventTypeSelection = value;
    if (value === 'Other') {
      this.createModel.eventType = '';
    } else {
      this.createModel.eventType = value;
    }
    this.eventTypeError = false;

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
    } else {
      this.createModel.monsterName = null;
      this.customMonsterName = '';
      this.createModel.repeatOnTod = false;
    }
  }

  // HNM Outside Sign Up gates manual HNM creation: the HNM option (and the form's HNM
  // branch) only appear when the active linkshell has HNM Outside Sign Up enabled.
  // Independent of Outside Party Signup.
  protected isHnmSignupEnabledForSelected(): boolean {
    const link = this.linkshellMemberships().find(l => l.id === this.createModel.linkshellId);
    return !!link?.settings?.hnmOutsideSignupEnabled;
  }

  protected eventTypeOptionsForForm(): string[] {
    const base = [...this.eventTypeOptions];
    return this.isHnmSignupEnabledForSelected() ? [...base, 'HNM'] : base;
  }

  protected isHnmSelected(): boolean {
    return (this.createModel.eventType ?? '').trim().toUpperCase() === 'HNM';
  }

  // The 16 canonical monsters (from the party-setup list payload) + the "Other"
  // sentinel, which reveals a free-text field.
  protected monsterOptions(): string[] {
    const list = this.partySetups.list()?.monsterOptions ?? [];
    return [...list, 'Other'];
  }

  // Native-select change handler for the monster dropdown. Driven by the option's
  // string value (and an empty-string placeholder) rather than ngModel/ngValue, so
  // the displayed value can never desync from createModel.monsterName — the cause of
  // the old "you picked one but it says Select a monster" bug.
  protected onMonsterSelect(value: string): void {
    this.createModel.monsterName = value ? value : null;
  }

  protected isOtherMonsterSelected(): boolean {
    return (this.createModel.monsterName ?? '') === 'Other';
  }

  // Repeat-on-ToD needs an exact Tod.MonsterName match, so it's disabled for the
  // free-text "Other" monster.
  protected canRepeatOnTod(): boolean {
    return this.isHnmSelected() && !this.isOtherMonsterSelected();
  }

  protected onPartySetupNotSpecifiedChange(checked: boolean): void {
    this.partySetupNotSpecified = checked;
    if (checked) {
      this.createModel.partySetupId = null;
    }
  }

  protected onEndTimeNotSpecifiedChange(checked: boolean): void {
    this.endTimeNotSpecified = checked;
    if (checked) {
      this.createModel.endTimeLocal = '';
    }
    this.recomputeDuration();
  }

  protected onStartTimeChange(): void {
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
    const membership = this.linkshellMemberships().find(link => link.id === linkshellId);
    const rank = (membership?.rank ?? '').toLowerCase();
    return rank === 'leader' || rank === 'officer';
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

  // HNM events are addon-only-startable; mirrors the web's
  // "Start (addon-only)" disabled button at Views/Event/Index.cshtml.
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
    let sec = Math.max(0, Math.round((start - repost) / 1000));
    const h = Math.floor(sec / 3600);
    sec %= 3600;
    const m = Math.floor(sec / 60);
    const s = sec % 60;
    const parts: string[] = [];
    if (h) { parts.push(`${h}h`); }
    if (m) { parts.push(`${m}m`); }
    if (s) { parts.push(`${s}s`); }
    return parts.length ? parts.join(' ') : '0s';
  }

  protected openPostTodForm(event: {
    id: number;
    linkshellId: number;
    assignedMonsterName?: string | null;
    partySetupAssignedMonsterName?: string | null;
    hnmAwaitingRepost?: boolean;
    sourceTodId?: number | null;
  }): void {
    const monster = event.assignedMonsterName ?? event.partySetupAssignedMonsterName ?? null;
    // Already defeated/awaiting re-post → "Edit ToD": pre-fill the board's logged ToD.
    if (event.hnmAwaitingRepost && event.sourceTodId != null) {
      const tod = (this.activity.overview()?.recentTods ?? []).find(t => t.id === event.sourceTodId);
      if (tod) {
        this.todForm()?.openEditForBoard(tod, event.id, monster ?? '');
        return;
      }
    }
    if (monster) {
      this.todForm()?.openForBoard(event.linkshellId, monster, event.id);
    } else {
      this.todForm()?.openCreate(event.linkshellId, null);
    }
  }

  protected isMemberMode(): boolean {
    return !this.canManageAnyLinkshell();
  }

  protected openCreateForm(): void {
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
    // changed/removed: the current slot signups belong to the old setup's slots.
    // Saving moves those members to "no slot" attendance on the new board (rather
    // than dropping them). Warn and let the user back out.
    if (this.editingEventId
        && this.editingOriginalPartySetupId !== null
        && nextPartySetupId !== this.editingOriginalPartySetupId) {
      const ok = await this.requestConfirm(
        'Changing the party setup will move everyone currently signed up to “no slot” attendance on the new board. Continue?');
      if (!ok) {
        return;
      }
    }

    this.isSubmittingCreate = true;

    try {
      const payload: ActivityCreateEventInput = {
        ...this.createModel,
        partySetupId: nextPartySetupId,
        monsterName,
        repeatOnTod: this.canRepeatOnTod() && !!this.createModel.repeatOnTod,
        repeatLeadHours: this.repeatLeadAsHours()
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
    autoStart?: boolean;
    countsTowardActive?: boolean;
    assignedMonsterName?: string | null;
    repeatOnTod?: boolean;
    repeatLeadHours?: number | null;
  }): void {
    this.activity.clearActionState();
    this.isCreateOpen = true;
    this.editingEventId = event.id;
    this.isEditingLiveEvent = !!event.commencementStartTime;
    this.createModel.linkshellId = event.linkshellId;
    this.createModel.eventName = event.name ?? '';
    const incomingType = event.type ?? '';
    this.createModel.eventType = incomingType;
    if (!incomingType) {
      this.eventTypeSelection = '';
    } else if (incomingType.trim().toUpperCase() === 'HNM') {
      this.eventTypeSelection = 'HNM';
    } else {
      this.eventTypeSelection = (this.eventTypeOptions as readonly string[]).includes(incomingType) ? incomingType : 'Other';
    }
    this.eventTypeError = false;
    this.createModel.eventLocation = event.location ?? '';
    this.createModel.startTimeLocal = this.activity.toViewerLocalInputValue(event.startTime ?? null);
    this.createModel.endTimeLocal = this.activity.toViewerLocalInputValue(event.endTime ?? null);
    // Addon-created (HNM) events come back with no end time; the "N/A" toggle
    // reflects that, and the duration is derived from Start + End below.
    this.endTimeNotSpecified = !this.createModel.endTimeLocal;
    this.createModel.partySetupId = event.partySetupId ?? null;
    this.editingOriginalPartySetupId = event.partySetupId ?? null;
    this.partySetupNotSpecified = event.partySetupId == null;
    this.createModel.autoStart = event.autoStart ?? false;
    this.createModel.countsTowardActive = event.countsTowardActive ?? true;
    // HNM repeat-board fields so editing preserves the monster + repeat-on-ToD + lead time.
    this.createModel.monsterName = event.assignedMonsterName ?? null;
    this.customMonsterName = '';
    this.createModel.repeatOnTod = !!event.repeatOnTod;
    this.setRepeatLeadFromHours(event.repeatLeadHours);
    // Lazy-load the linkshell's PartySetup list so the dropdown populates.
    if (this.createModel.linkshellId) {
      void this.partySetups.loadList(this.createModel.linkshellId);
    }
    this.recomputeDuration();
    this.createModel.dkpPerHour = event.dkpPerHour ?? 0;
    this.createModel.details = event.details ?? '';
    // External callers (e.g. live-event Edit on the events tab) reach this
    // method through a viewChild — Angular's OnPush check for the queue panel
    // wouldn't otherwise run on this synchronous mutation.
    this.cdr.markForCheck();
    if (this.isEditingLiveEvent) {
      // Defer showModal() until Angular has rendered the <dialog> element from
      // the @if branch above — otherwise the viewChild signal still resolves
      // to undefined.
      setTimeout(() => {
        const dialog = this.editDialog()?.nativeElement;
        if (dialog && !dialog.open) {
          dialog.showModal();
        }
      });
    }
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
    this.createModel.repeatOnTod = false;
    this.createModel.repeatLeadHours = 1;
    this.repeatLeadH = 1;
    this.repeatLeadM = 0;
    this.repeatLeadS = 0;
    this.customMonsterName = '';
    this.isEditingLiveEvent = false;
    this.editingOriginalPartySetupId = null;
  }

  // Opens the confirm dialog and resolves once the user chooses. Any pending
  // confirm is rejected (false) first so a stale promise can't dangle.
  private requestConfirm(message: string): Promise<boolean> {
    this.confirmResolver?.(false);
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
