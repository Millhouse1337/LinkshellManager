import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
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

@Component({
  selector: 'app-activity-queue-panel',
  imports: [CommonModule, FormsModule],
  templateUrl: './activity-queue-panel.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class ActivityQueuePanelComponent {
  protected readonly activity = inject(DiscordActivityService);
  protected editingEventId: number | null = null;
  protected readonly createModel: ActivityCreateEventInput = {
    linkshellId: 0,
    eventName: '',
    eventType: '',
    eventLocation: '',
    startTimeLocal: '',
    endTimeLocal: '',
    duration: 1,
    dkpPerHour: 0,
    details: '',
    jobs: [
      {
        jobName: '',
        subJobName: '',
        jobType: '',
        quantity: 1,
        details: ''
      }
    ]
  };

  protected readonly eventTypeOptions = ['Sky', 'Sea', 'HNM', 'HENM', 'Limbus', 'Dynamis', 'BCNM', 'KSNM'] as const;
  protected eventTypeSelection: string = '';
  protected eventTypeError = false;

  protected isCreateOpen = false;
  protected isSubmittingCreate = false;
  protected durationNotSpecified = false;
  protected endTimeNotSpecified = false;
  protected partySetupNotSpecified = false;
  protected jobQuantityNotSpecified: boolean[] = [false];

  protected onEventTypeSelectionChange(value: string): void {
    this.eventTypeSelection = value;
    if (value === 'Other') {
      this.createModel.eventType = '';
    } else {
      this.createModel.eventType = value;
    }
    this.eventTypeError = false;
  }

  protected onJobQuantityNotSpecifiedChange(index: number, checked: boolean): void {
    this.jobQuantityNotSpecified = this.jobQuantityNotSpecified.map((v, i) => i === index ? checked : v);
    const job = this.createModel.jobs[index];
    if (!job) return;
    if (checked) {
      job.quantity = null;
    } else if (job.quantity == null) {
      job.quantity = 1;
    }
  }

  protected onDurationNotSpecifiedChange(checked: boolean): void {
    this.durationNotSpecified = checked;
    if (checked) {
      this.createModel.duration = null;
    } else {
      if (this.createModel.duration == null) {
        this.createModel.duration = 1;
      }
      this.recomputeEndFromStartDuration();
    }
  }

  protected onEndTimeNotSpecifiedChange(checked: boolean): void {
    this.endTimeNotSpecified = checked;
    if (checked) {
      this.createModel.endTimeLocal = '';
    } else {
      this.recomputeEndFromStartDuration();
    }
  }

  protected onStartTimeChange(): void {
    if (!this.endTimeNotSpecified && this.createModel.endTimeLocal) {
      this.recomputeDurationFromStartEnd();
    } else if (!this.durationNotSpecified && this.createModel.duration != null) {
      this.recomputeEndFromStartDuration();
    }
  }

  protected onEndTimeChange(): void {
    this.recomputeDurationFromStartEnd();
  }

  protected onDurationChange(): void {
    this.recomputeEndFromStartDuration();
  }

  private recomputeDurationFromStartEnd(): void {
    if (this.durationNotSpecified || this.endTimeNotSpecified) return;
    const start = this.parseLocalDateTime(this.createModel.startTimeLocal);
    const end = this.parseLocalDateTime(this.createModel.endTimeLocal);
    if (!start || !end) return;
    const hours = (end.getTime() - start.getTime()) / 3_600_000;
    if (hours < 0) return;
    this.createModel.duration = Math.round(hours * 100) / 100;
  }

  private recomputeEndFromStartDuration(): void {
    if (this.endTimeNotSpecified || this.durationNotSpecified) return;
    const start = this.parseLocalDateTime(this.createModel.startTimeLocal);
    const hours = this.createModel.duration;
    if (!start || hours == null || hours < 0) return;
    const end = new Date(start.getTime() + hours * 3_600_000);
    this.createModel.endTimeLocal = this.formatLocalDateTime(end);
  }

  private parseLocalDateTime(value: string | null | undefined): Date | null {
    if (!value) return null;
    const trimmed = value.trim();
    if (!trimmed) return null;
    const parsed = new Date(trimmed);
    return Number.isNaN(parsed.getTime()) ? null : parsed;
  }

  private formatLocalDateTime(date: Date): string {
    const pad = (n: number) => String(n).padStart(2, '0');
    return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`;
  }

  protected readonly mainJobOptions = [...EVENT_MAIN_JOB_OPTIONS];
  protected readonly subJobOptions = [...EVENT_SUB_JOB_OPTIONS];
  protected readonly jobTypeOptions = [...EVENT_JOB_TYPE_OPTIONS];

  // Per-event draft of the user's job selection for pending events that have
  // no pre-defined party setup. Mirrors the late-join draft pattern used in
  // activity-home; lazily seeded so the template can two-way bind directly.
  protected readonly signupDrafts: { [eventId: number]: ActivityQuickJoinInput } = {};

  // Tracks queued-event cards that are currently EXPANDED. Default empty
  // set => everything starts collapsed. Toggling adds/removes the id.
  // Session-only.
  protected readonly expandedQueueIds = signal<Set<number>>(new Set());

  protected isQueueCollapsed(eventId: number): boolean {
    return !this.expandedQueueIds().has(eventId);
  }

  protected toggleQueueCollapsed(eventId: number): void {
    const next = new Set(this.expandedQueueIds());
    if (next.has(eventId)) next.delete(eventId); else next.add(eventId);
    this.expandedQueueIds.set(next);
  }

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

  protected isMemberMode(): boolean {
    return !this.canManageAnyLinkshell();
  }

  protected openCreateForm(): void {
    this.activity.clearActionState();
    this.isCreateOpen = true;
    this.editingEventId = null;
    const defaultLinkshellId =
      this.activity.overview()?.primaryLinkshell?.id ??
      this.activity.overview()?.linkshells?.[0]?.id ??
      0;

    if (!this.createModel.linkshellId) {
      this.createModel.linkshellId = defaultLinkshellId;
    }
  }

  protected closeCreateForm(): void {
    this.isCreateOpen = false;
    this.editingEventId = null;
  }

  protected addJobRow(): void {
    this.createModel.jobs.push({
      jobName: '',
      subJobName: '',
      jobType: '',
      quantity: 1,
      details: ''
    });
    this.jobQuantityNotSpecified = [...this.jobQuantityNotSpecified, false];
  }

  protected removeJobRow(index: number): void {
    if (this.createModel.jobs.length === 1) {
      this.createModel.jobs[0] = {
        jobName: '',
        subJobName: '',
        jobType: '',
        quantity: 1,
        details: ''
      };
      this.jobQuantityNotSpecified = [false];
      return;
    }

    this.createModel.jobs.splice(index, 1);
    this.jobQuantityNotSpecified = this.jobQuantityNotSpecified.filter((_, i) => i !== index);
  }

  protected async submitCreateForm(): Promise<void> {
    const eventType = this.createModel.eventType?.trim() ?? '';
    if (!this.eventTypeSelection || !eventType) {
      this.eventTypeError = true;
      return;
    }
    this.eventTypeError = false;
    this.createModel.eventType = eventType;

    this.isSubmittingCreate = true;

    try {
      const payload = {
        ...this.createModel,
        jobs: this.partySetupNotSpecified ? [] : this.createModel.jobs
      };
      if (this.editingEventId) {
        await this.activity.updateEvent(this.editingEventId, payload);
      } else {
        await this.activity.createEvent(payload);
      }
      this.resetCreateModel();
      this.isCreateOpen = false;
      this.editingEventId = null;
    } finally {
      this.isSubmittingCreate = false;
    }
  }

  protected openEditEventForm(event: {
    id: number;
    linkshellId: number;
    name?: string | null;
    type?: string | null;
    location?: string | null;
    startTime?: string | null;
    endTime?: string | null;
    duration?: number | null;
    dkpPerHour?: number | null;
    details?: string | null;
    jobs: {
      jobName?: string | null;
      subJobName?: string | null;
      jobType?: string | null;
      quantity?: number | null;
    }[];
  }): void {
    this.activity.clearActionState();
    this.isCreateOpen = true;
    this.editingEventId = event.id;
    this.createModel.linkshellId = event.linkshellId;
    this.createModel.eventName = event.name ?? '';
    const incomingType = event.type ?? '';
    this.createModel.eventType = incomingType;
    if (!incomingType) {
      this.eventTypeSelection = '';
    } else {
      this.eventTypeSelection = (this.eventTypeOptions as readonly string[]).includes(incomingType) ? incomingType : 'Other';
    }
    this.eventTypeError = false;
    this.createModel.eventLocation = event.location ?? '';
    this.createModel.startTimeLocal = this.activity.toViewerLocalInputValue(event.startTime ?? null);
    this.createModel.endTimeLocal = this.activity.toViewerLocalInputValue(event.endTime ?? null);
    // Source of truth is the actual stored value: addon-created (HNM) events
    // come back with both endTime and duration null, so the "Not specified"
    // checkboxes should reflect that on edit.
    this.createModel.duration = event.duration ?? 1;
    this.durationNotSpecified = event.duration == null;
    this.endTimeNotSpecified = !this.createModel.endTimeLocal;
    this.partySetupNotSpecified = !(event.jobs && event.jobs.some(j =>
      !!j.jobName || !!j.subJobName || !!j.jobType || (j.quantity != null && j.quantity > 0)));
    if (!this.durationNotSpecified && !this.endTimeNotSpecified) {
      this.recomputeDurationFromStartEnd();
    }
    this.createModel.dkpPerHour = event.dkpPerHour ?? 0;
    this.createModel.details = event.details ?? '';
    this.createModel.jobs = event.jobs.map(job => ({
      jobName: job.jobName ?? '',
      subJobName: job.subJobName ?? '',
      jobType: job.jobType ?? '',
      quantity: job.quantity ?? 1,
      details: ''
    }));

    if (this.createModel.jobs.length === 0) {
      this.createModel.jobs = [
        {
          jobName: '',
          subJobName: '',
          jobType: '',
          quantity: 1,
          details: ''
        }
      ];
    }
    this.jobQuantityNotSpecified = this.createModel.jobs.map(job => job.quantity == null);
  }

  protected async confirmCancelEvent(eventId: number, eventName?: string | null): Promise<void> {
    const label = eventName?.trim() || 'this event';
    if (!window.confirm(`Cancel ${label}? This removes all queued signups.`)) {
      return;
    }

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
    this.createModel.duration = 1;
    this.createModel.dkpPerHour = 0;
    this.createModel.details = '';
    this.durationNotSpecified = false;
    this.endTimeNotSpecified = false;
    this.partySetupNotSpecified = false;
    this.createModel.jobs = [
      {
        jobName: '',
        subJobName: '',
        jobType: '',
        quantity: 1,
        details: ''
      }
    ];
    this.jobQuantityNotSpecified = [false];
  }
}
