import { ChangeDetectionStrategy, Component, computed, effect, inject } from '@angular/core';
import { FormsModule } from '@angular/forms';

import { datetimeLocalToUtcIso } from '../../discord/discord-activity.helpers';
import { DiscordActivityService } from '../../discord/discord-activity.service';
import type {
  ActivityPendingAttendee,
  ActivityPendingTod,
  ActivityPendingTodEdits,
  ActivityPendingTodLoot
} from '../../discord/discord-activity.types';
import { PendingSubmissionsService } from '../../discord/pending-submissions.service';

// What a reviewer can change on a pending ToD before approving it. Times are datetime-local
// values in the viewer's profile zone.
interface TodDraft {
  monsterName: string;
  dayNumber: number | null;
  claim: boolean;
  timeLocal: string;
  cooldown: string;
  interval: string;
  repopTimeLocal: string;
  lootRows: ActivityPendingTodLoot[];
}

// The review queue for what members submitted for approval — ToDs (editable, with their loot) and
// attendance windows (untick anyone who wasn't there) — as the last section of the Event System's
// Pending view, inside Discord and on the website. Members' /lsm now captures are not here: they
// land as Pending snapshots in that view's Triage. A legacy queued snapshot (nothing queues those
// any more) still shows here if one exists. EventsTabComponent mounts this only for reviewers; the
// API checks again per kind (CanManageTods / CanModerateLiveEvent).
@Component({
  selector: 'app-pending-submissions-section',
  imports: [FormsModule],
  templateUrl: './pending-submissions-section.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class PendingSubmissionsSectionComponent {
  protected readonly activity = inject(DiscordActivityService);
  protected readonly pending = inject(PendingSubmissionsService);

  protected readonly linkshellId = computed(() =>
    this.activity.overview()?.primaryLinkshell?.id ?? this.activity.overview()?.appUser?.primaryLinkshellId ?? 0);
  protected readonly queue = computed(() => this.pending.queueFor(this.linkshellId()));
  protected readonly isEmpty = computed(() => {
    const q = this.queue();
    return !!q && q.tods.length + q.windows.length + q.snapshots.length === 0;
  });

  // Drafts and unticked attendees, kept per submission so a reload of the queue (after another
  // card's action) doesn't wipe what the reviewer typed into this one.
  private readonly todDrafts = new Map<number, TodDraft>();
  private readonly unticked = new Map<string, Set<number>>();

  constructor() {
    effect(() => {
      const id = this.linkshellId();
      if (id) {
        queueMicrotask(() => void this.pending.load(id));
      }
    });
  }

  protected todDraft(tod: ActivityPendingTod): TodDraft {
    let draft = this.todDrafts.get(tod.id);
    if (!draft) {
      draft = {
        monsterName: tod.monsterName ?? '',
        dayNumber: tod.dayNumber,
        claim: tod.claim ?? false,
        timeLocal: this.activity.toViewerLocalInputValue(tod.time, true),
        cooldown: tod.cooldown ?? '',
        interval: tod.interval ?? '',
        repopTimeLocal: this.activity.toViewerLocalInputValue(tod.repopTime, true),
        lootRows: tod.lootRows.map(row => ({ ...row }))
      };
      this.todDrafts.set(tod.id, draft);
    }
    return draft;
  }

  // The submitted monster stays pickable even when it isn't in this linkshell's setups.
  protected monsterOptions(tod: ActivityPendingTod): string[] {
    const options = this.queue()?.monsterOptions ?? [];
    return tod.monsterName && !options.includes(tod.monsterName) ? [tod.monsterName, ...options] : options;
  }

  protected addLootRow(draft: TodDraft): void {
    draft.lootRows.push({ itemName: '', itemWinner: '', winningDkpSpent: null });
  }

  protected removeLootRow(draft: TodDraft, index: number): void {
    draft.lootRows.splice(index, 1);
  }

  protected approveTod(tod: ActivityPendingTod): void {
    const draft = this.todDraft(tod);
    const edits: ActivityPendingTodEdits = {
      monsterName: draft.monsterName || null,
      dayNumber: draft.dayNumber,
      claim: draft.claim,
      timeLocal: datetimeLocalToUtcIso(draft.timeLocal),
      cooldown: draft.cooldown || null,
      interval: draft.interval || null,
      repopTimeLocal: datetimeLocalToUtcIso(draft.repopTimeLocal),
      lootRows: draft.lootRows
    };
    void this.pending.approveTod(this.linkshellId(), tod.id, edits).then(() => this.todDrafts.delete(tod.id));
  }

  protected rejectTod(tod: ActivityPendingTod): void {
    void this.pending.rejectTod(this.linkshellId(), tod.id);
  }

  protected isKept(key: string, attendee: ActivityPendingAttendee): boolean {
    return !this.unticked.get(key)?.has(attendee.id);
  }

  protected toggleKept(key: string, attendee: ActivityPendingAttendee): void {
    const set = this.unticked.get(key) ?? new Set<number>();
    if (!set.delete(attendee.id)) {
      set.add(attendee.id);
    }
    this.unticked.set(key, set);
  }

  protected keptIds(key: string, attendees: ActivityPendingAttendee[]): number[] {
    return attendees.filter(attendee => this.isKept(key, attendee)).map(attendee => attendee.id);
  }

  protected approveWindow(id: number, members: ActivityPendingAttendee[]): void {
    void this.pending.approveWindow(this.linkshellId(), id, this.keptIds(`window-${id}`, members));
  }

  protected approveSnapshot(id: number, entries: ActivityPendingAttendee[]): void {
    void this.pending.approveSnapshot(this.linkshellId(), id, this.keptIds(`snapshot-${id}`, entries));
  }

  protected job(attendee: ActivityPendingAttendee): string {
    const main = attendee.mainJob ? `${attendee.mainJob}${attendee.mainJobLevel ?? ''}` : '';
    const sub = attendee.subJob ? `/${attendee.subJob}${attendee.subJobLevel ?? ''}` : '';
    return main + sub;
  }
}
