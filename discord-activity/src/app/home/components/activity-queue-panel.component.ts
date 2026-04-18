import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivityCreateEventInput, DiscordActivityService } from '../../discord/discord-activity.service';
import {
  EVENT_JOB_TYPE_OPTIONS,
  EVENT_MAIN_JOB_OPTIONS,
  EVENT_SUB_JOB_OPTIONS
} from '../event-job-options';

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
  protected isCreateOpen = false;
  protected isSubmittingCreate = false;

  protected readonly mainJobOptions = [...EVENT_MAIN_JOB_OPTIONS];
  protected readonly subJobOptions = [...EVENT_SUB_JOB_OPTIONS];
  protected readonly jobTypeOptions = [...EVENT_JOB_TYPE_OPTIONS];

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

  protected canManageAnyLinkshell(): boolean {
    return this.linkshellMemberships().some(link => this.canManageLinkshell(link.id));
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
      return;
    }

    this.createModel.jobs.splice(index, 1);
  }

  protected async submitCreateForm(): Promise<void> {
    this.isSubmittingCreate = true;

    try {
      if (this.editingEventId) {
        await this.activity.updateEvent(this.editingEventId, this.createModel);
      } else {
        await this.activity.createEvent(this.createModel);
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
    this.createModel.eventType = event.type ?? '';
    this.createModel.eventLocation = event.location ?? '';
    this.createModel.startTimeLocal = this.toLocalInputValue(event.startTime ?? null);
    this.createModel.endTimeLocal = this.toLocalInputValue(event.endTime ?? null);
    this.createModel.duration = 1;
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
    this.createModel.eventLocation = '';
    this.createModel.startTimeLocal = '';
    this.createModel.endTimeLocal = '';
    this.createModel.duration = 1;
    this.createModel.dkpPerHour = 0;
    this.createModel.details = '';
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

  private toLocalInputValue(value?: string | null): string {
    if (!value) {
      return '';
    }

    const date = new Date(value);
    if (Number.isNaN(date.getTime())) {
      return '';
    }

    const pad = (part: number) => part.toString().padStart(2, '0');
    return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`;
  }
}
