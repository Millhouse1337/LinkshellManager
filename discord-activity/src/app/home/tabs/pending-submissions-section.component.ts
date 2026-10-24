import { ChangeDetectionStrategy, Component, computed, effect, inject } from '@angular/core';
import { FormsModule } from '@angular/forms';

import { datetimeLocalToUtcIso } from '../../discord/discord-activity.helpers';
import { DiscordActivityService } from '../../discord/discord-activity.service';
import type {
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

// "ToD Submissions Needing Approval", at the top of the ToDs tab, inside Discord and on the
// website: the ToDs members without the right to post them directly submitted (the ToDs tab's form
// or the addon's capture), editable, with their loot, to Approve or Reject. Only ToDs: members'
// /lsm now attendance captures wait in the Event System's Unlinked Snapshots ("Awaiting
// confirmation"). TodsTabComponent mounts this only for reviewers; the API checks again
// (CanManageTods). The top bar's Refresh reloads it (AutoRefreshService).
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
    return !!q && q.tods.length === 0;
  });

  // Drafts, kept per submission so a reload of the queue (after another card's action) doesn't
  // wipe what the reviewer typed into this one.
  private readonly todDrafts = new Map<number, TodDraft>();

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
}
