import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, Input, effect, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  ActivityLinkshellSettings,
  DiscordActivityService
} from '../../discord/discord-activity.service';
import type { ActivityPartySetupListRow } from '../../discord/discord-activity.types';
import { PartySetupService } from '../../discord/party-setup.service';
import { PartySetupPanelComponent } from './party-setup-panel.component';
import { TodFormComponent } from './tod-form.component';
import { TOD_NOT_ENTERED, isTodReady, todCountdownLabel, todSortKey } from '../activity-home.helpers';

@Component({
  selector: 'app-tods-tab',
  imports: [CommonModule, FormsModule, PartySetupPanelComponent, TodFormComponent],
  templateUrl: './tods-tab.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class TodsTabComponent {
  protected readonly activity = inject(DiscordActivityService);
  protected readonly partySetup = inject(PartySetupService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly now = signal(Date.now());

  // The single-instance ToD delete confirmation modal lives in the parent so
  // it remains mounted regardless of which tab is active.
  @Input({ required: true }) deleteTodFn!: (todId: number, monsterName: string) => void;

  // The Log ToD form is its own component (TodFormComponent) so the Events tab's
  // "Post ToD" button can open the same form on an HNM board. The "+ Log ToD"
  // header button and each row's Edit button drive the embedded <app-tod-form
  // #todForm /> directly from the template (openCreate / openEdit).

  // Discord's URL mapping only proxies /api/* through to the backend.
  // Static uploads at /uploads/tods/... would 404 inside the activity
  // iframe (discordsays.com origin). Rewrite to the API proxy endpoint
  // (GET /api/activity/uploads/tods/{file}) so the same image resolves
  // through Discord's mapping. Paths that don't match are returned
  // unchanged so external URLs / data URIs still work.
  protected displayImagePath(path: string | null | undefined): string | null {
    if (!path) return null;
    if (path.startsWith('/uploads/tods/')) {
      return '/api/activity/uploads/tods/' + path.substring('/uploads/tods/'.length);
    }
    return path;
  }

  // Click handler for the ToD screenshot thumbnail. Plain <a target="_blank">
  // is blocked by Discord's embed sandbox, so route the click through the
  // Discord SDK's openExternalLink (falls back to window.open outside the
  // iframe -- e.g. ng serve during dev).
  protected openTodImage(event: Event, path: string | null | undefined): void {
    event.preventDefault();
    const resolved = this.displayImagePath(path);
    if (!resolved) return;
    // Build an absolute URL so Discord's openExternalLink has something to
    // navigate to (relative paths would resolve against discordsays.com).
    const absolute = resolved.startsWith('http')
      ? resolved
      : window.location.origin + resolved;
    void this.activity.openExternalLink(absolute);
  }

  public constructor() {
    const intervalId = window.setInterval(() => this.now.set(Date.now()), 1000);
    this.destroyRef.onDestroy(() => window.clearInterval(intervalId));

    // Load party setups for the active linkshell so a ToD row whose monster has
    // an assigned setup can offer the inline sign-up panel.
    effect(() => {
      const id = this.selectedDashboardLinkshellId();
      if (id) queueMicrotask(() => void this.partySetup.loadList(id));
    });
  }

  // ----- Party setup inline panel -----

  // The party setup (if any) assigned to a ToD group's monster. Matched
  // case-insensitively + trimmed, the same convention as
  // PartySetupController.ClearSignupsForMonsterAsync.
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

  protected primaryLinkshell() {
    return this.activity.overview()?.primaryLinkshell ?? null;
  }

  protected dashboardLinkshells() {
    return this.activity.overview()?.linkshells ?? [];
  }

  protected selectedDashboardLinkshellId(): number {
    return (
      this.activity.overview()?.appUser?.primaryLinkshellId ??
      this.primaryLinkshell()?.id ??
      this.dashboardLinkshells()[0]?.id ??
      0
    );
  }

  protected selectedDashboardLinkshell() {
    const selectedId = this.selectedDashboardLinkshellId();
    return this.dashboardLinkshells().find(linkshell => linkshell.id === selectedId) ?? null;
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

  protected groupedDashboardTods(): { key: string; latest: any; history: any[] }[] {
    const groups = new Map<string, any[]>();
    for (const tod of this.selectedDashboardTods()) {
      const key = (tod.monsterName ?? '').trim().toLowerCase() || `__${tod.id}`;
      if (!groups.has(key)) groups.set(key, []);
      groups.get(key)!.push(tod);
    }
    const result = Array.from(groups.entries()).map(([key, entries]) => ({
      key,
      latest: entries[0],
      history: entries.slice(1, 10)
    }));
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

  protected readonly expandedTodLoot = signal<Set<number>>(new Set());

  protected isTodLootExpanded(todId: number): boolean {
    return this.expandedTodLoot().has(todId);
  }

  protected toggleTodLoot(todId: number): void {
    const next = new Set(this.expandedTodLoot());
    if (next.has(todId)) next.delete(todId); else next.add(todId);
    this.expandedTodLoot.set(next);
  }

  // Countdown / ready state / the "Not entered" label all live in activity-home.helpers so this
  // tab and the dashboard tab can't drift apart — they render the same card from the same data.
  protected readonly notEntered = TOD_NOT_ENTERED;

  protected todCountdownLabel(tod: { repopTime?: string | null }): string {
    return todCountdownLabel(tod.repopTime, this.now());
  }

  protected isTodReady(tod: { repopTime?: string | null }): boolean {
    return isTodReady(tod.repopTime, this.now());
  }

  protected deleteTod(todId: number, monsterName: string): void {
    this.deleteTodFn(todId, monsterName);
  }
}
