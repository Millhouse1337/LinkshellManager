import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, Input, computed, effect, inject, model, signal } from '@angular/core';
import { ClockService } from '../../discord/clock.service';
import { FormsModule } from '@angular/forms';
import {
  ActivityLinkshellSettings,
  DiscordActivityService
} from '../../discord/discord-activity.service';
import type { ActivityMonsterSetup, ActivityPartySetupListRow } from '../../discord/discord-activity.types';
import { PartySetupService } from '../../discord/party-setup.service';
import { PendingSubmissionsService } from '../../discord/pending-submissions.service';
import { PartySetupPanelComponent } from './party-setup-panel.component';
import { PendingSubmissionsSectionComponent } from './pending-submissions-section.component';
import { TodFormComponent } from './tod-form.component';
import { TodWindowRowComponent } from './tod-window-row.component';
import { TOD_NOT_ENTERED, canReviewSubmissionsIn, mergedMonsterName, monsterSetupForTod, todSortKey, todWindowState } from '../activity-home.helpers';
import { hasHqVariant } from '../activity-home.types';

type TodFilter = 'All' | 'Ready' | 'Pending' | 'Done';

// Main = the tracker (approved ToDs). Pending = members' ToD submissions waiting for approval.
export type TodsView = 'main' | 'pending';

@Component({
  selector: 'app-tods-tab',
  imports: [CommonModule, FormsModule, PartySetupPanelComponent, PendingSubmissionsSectionComponent, TodFormComponent, TodWindowRowComponent],
  templateUrl: './tods-tab.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class TodsTabComponent {
  protected readonly activity = inject(DiscordActivityService);
  protected readonly partySetup = inject(PartySetupService);
  private readonly pendingSubmissions = inject(PendingSubmissionsService);
  protected readonly now = inject(ClockService).now;

  // Two-way `view` binding: ActivityHomeComponent keeps it in the location (/tods/pending).
  readonly todsView = model<TodsView>('main', { alias: 'view' });

  // Pending is for reviewers only: anyone else sees no tabs and always gets Main, even when they
  // follow a /tods/pending link.
  protected showingPending(): boolean {
    return this.todsView() === 'pending' && this.canReviewSubmissions();
  }

  // ToDs waiting for approval, for the "Pending (N)" tab label.
  protected pendingCount(): number {
    return this.pendingSubmissions.queueFor(this.selectedDashboardLinkshellId())?.tods.length ?? 0;
  }

  // The linkshell whose approval queue to load, or 0 when the viewer may not review it. A computed
  // so the load below runs when this changes, not on every overview refresh.
  private readonly partySetupLinkshellId = computed(() => this.selectedDashboardLinkshellId());
  private readonly reviewLinkshellId = computed(() =>
    this.canReviewSubmissions() ? this.selectedDashboardLinkshellId() : 0);

  // The single-instance ToD delete confirmation modal lives in the parent so
  // it remains mounted regardless of which tab is active.
  @Input({ required: true }) deleteTodFn!: (todId: number, monsterName: string) => void;

  // The Log ToD form is its own component (TodFormComponent) so the Events tab's
  // "Post ToD" button can open the same form on an HNM board. The "+ Log ToD"
  // header button and each row's Edit button drive the embedded <app-tod-form
  // #todForm /> directly from the template (openCreate / openEdit).

  // Click handler for the ToD screenshot thumbnail. The image itself is shown through
  // activity.resolveUploadUrl (the one place that maps /uploads/... onto the /api proxy Discord
  // forwards); a click opens it in a real browser tab, since target="_blank" is blocked in Discord.
  protected openTodImage(event: Event, path: string | null | undefined): void {
    event.preventDefault();
    this.activity.openUploadInBrowser(path);
  }

  public constructor() {
    // Load party setups for the active linkshell so a ToD row whose monster has
    // an assigned setup can offer the inline (view-only) setup panel. Reads the computed id,
    // so it runs when the linkshell changes rather than on every overview refresh;
    // the refresh timer (AutoRefreshService, 'tods') keeps the list fresh.
    effect(() => {
      const id = this.partySetupLinkshellId();
      if (id) queueMicrotask(() => void this.partySetup.loadList(id));
    });

    // Loads the approval queue while Main is open too, so the Pending tab's count is right before
    // anyone clicks it. (The Pending section reloads it when it opens.)
    effect(() => {
      const id = this.reviewLinkshellId();
      if (id) queueMicrotask(() => void this.pendingSubmissions.load(id));
    });
  }

  // ----- Party setup inline panel -----

  // The party setup (if any) assigned to a ToD group's monster. Matched
  // case-insensitively + trimmed, the convention the server uses for monster
  // names everywhere.
  protected setupForMonster(monsterName: string | null | undefined): ActivityPartySetupListRow | null {
    const target = (monsterName ?? '').trim().toLowerCase();
    if (!target) return null;
    const items = this.partySetup.list()?.items ?? [];
    return items.find(row => (row.assignedMonsterName ?? '').trim().toLowerCase() === target) ?? null;
  }

  protected readonly expandedSetupGroups = signal<Set<string>>(new Set());

  protected isSetupExpanded(key: string): boolean {
    return this.expandedSetupGroups().has(key);
  }

  protected toggleSetup(key: string): void {
    const next = new Set(this.expandedSetupGroups());
    if (next.has(key)) next.delete(key); else next.add(key);
    this.expandedSetupGroups.set(next);
  }

  // ----- Re-implemented small reads -----

  // May approve members' ToD submissions (the ToD Submissions Needing Approval section). Same
  // gate as the API.
  protected canReviewSubmissions(): boolean {
    return canReviewSubmissionsIn(this.activity.overview(), this.selectedDashboardLinkshellId());
  }

  // The app's one current linkshell (AuthService.currentLinkshellId).
  protected selectedDashboardLinkshellId(): number {
    return this.activity.currentLinkshellId();
  }

  protected selectedDashboardTods() {
    const selectedId = this.selectedDashboardLinkshellId();
    return [...(this.activity.overview()?.recentTods ?? [])]
      .filter(tod => tod.linkshellId === selectedId)
      // Newest first on time ?? timeStamp, so a ToD that was never entered still sorts as the
      // monster's latest instead of sinking below the pop it superseded.
      .sort((left, right) => todSortKey(right) - todSortKey(left));
  }

  private linkshellSettingsFor(linkshellId: number): ActivityLinkshellSettings | null {
    const link = this.activity.overview()?.linkshells?.find(l => l.id === linkshellId);
    return link?.settings ?? null;
  }

  // ----- ToD list rendering helpers -----

  protected readonly expandedTodGroups = signal<Set<string>>(new Set());

  // The All / Ready / Pending / Done pills on Tracked Windows.
  //   Ready   = the repop has passed and it can still pop (window open, or up with no window grid)
  //   Pending = still counting down to the repop
  //   Done    = every spawn window has passed (the red rows; TodWindowRowComponent's windowPassed)
  // A "Not entered" ToD is none of them, so only All shows it.
  protected readonly todFilters: readonly TodFilter[] = ['All', 'Ready', 'Pending', 'Done'];
  protected readonly todFilter = signal<TodFilter>('All');

  protected filteredTodGroups() {
    const filter = this.todFilter();
    const now = this.now();
    return this.groupedDashboardTods().filter(group => {
      if (filter === 'All') return true;
      const { tone, closeMs } = todWindowState(group.latest, group.setup, now);
      const done = tone === 'up' && closeMs !== null;
      switch (filter) {
        case 'Ready': return (tone === 'open' || tone === 'up') && !done;
        case 'Pending': return tone === 'pending';
        case 'Done': return done;
      }
    });
  }

  protected groupedDashboardTods(): { key: string; displayName: string; latest: any; history: any[]; setup: ActivityMonsterSetup | null }[] {
    const setups = this.linkshellSettingsFor(this.selectedDashboardLinkshellId())?.monsterSetups;
    // Keyed on the MERGED spawn name, not the raw monster name. The feed returns the newest ToD
    // per name and a merge pair has two of them, so Fafnir and Fafnir/Nidhogg used to open two
    // cards for one dragon, each counting down to a different repop. Whichever a camper read
    // first, the other was telling them something else.
    const groups = new Map<string, any[]>();
    for (const tod of this.selectedDashboardTods()) {
      const key = mergedMonsterName(tod.monsterName).toLowerCase() || `__${tod.id}`;
      if (!groups.has(key)) groups.set(key, []);
      groups.get(key)!.push(tod);
    }
    const result = Array.from(groups.entries()).map(([key, entries]) => {
      // Re-sorted because a merged group is two server lists spliced together: each arrived
      // newest-first for ITS OWN name, so entries[0] was simply whichever half the feed listed
      // first, not the kill that actually happened last. todSortKey is the same "which ToD is
      // this monster's most recent" rule the rest of the tracker sorts on.
      const ordered = [...entries].sort((a, b) => todSortKey(b) - todSortKey(a));
      return {
        key,
        // Captioned from the key rather than from the surviving row, so the card does not read
        // 'Fafnir' on a group that also holds tonight's Nidhogg.
        displayName: mergedMonsterName(ordered[0]?.monsterName),
        latest: ordered[0],
        // Starts WITH the latest kill: the History list is the one place a kill's claim and pop
        // window are shown, so the current one has to be in it too.
        history: ordered.slice(0, 10),
        // Supplies the window count the row needs to show when the spawn window closes.
        setup: monsterSetupForTod(setups, ordered[0]?.monsterName)
      };
    });
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

  // The day count only means something for the HQ/NQ pairs (Adamantoise/Aspidochelone,
  // Behemoth/King Behemoth, Fafnir/Nidhogg), where it decides which one pops. Every other
  // monster shows no Day at all.
  protected showsDay(tod: { monsterName?: string | null }): boolean {
    return hasHqVariant(tod.monsterName);
  }

  // The row itself (countdown, ready state, bar) is TodWindowRowComponent, shared with the
  // Dashboard. This label is only for the history list underneath.
  protected readonly notEntered = TOD_NOT_ENTERED;

  protected deleteTod(todId: number, monsterName: string): void {
    this.deleteTodFn(todId, monsterName);
  }
}
