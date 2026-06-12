import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, Input, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivityTodEntry, DiscordActivityService } from '../../discord/discord-activity.service';
import { formatAlts, formatElapsed, parseDate } from '../activity-home.helpers';
import { HNM_NAMES, type TabName } from '../activity-home.types';

@Component({
  selector: 'app-dashboard-tab',
  imports: [CommonModule, FormsModule],
  templateUrl: './dashboard-tab.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class DashboardTabComponent {
  protected readonly activity = inject(DiscordActivityService);
  protected readonly formatAlts = formatAlts;
  private readonly destroyRef = inject(DestroyRef);
  private readonly now = signal(Date.now());

  // Parent-owned modal trigger — opening the modal lives in the parent so a
  // single instance is shared between the Dashboard and ToDs tabs.
  @Input({ required: true }) deleteTodFn!: (todId: number, monsterName: string) => void;
  @Input({ required: true }) setActiveTabFn!: (tab: TabName) => void;

  // Roster search is shared with the linkshell tab via the parent component
  // so the value persists when the user hops between tabs.
  @Input({ required: true }) rosterSearchValue!: string;
  @Input({ required: true }) rosterSearchChange!: (value: string) => void;

  protected get dashboardRosterSearch(): string { return this.rosterSearchValue; }
  protected set dashboardRosterSearch(value: string) { this.rosterSearchChange(value); this.rosterPage.set(1); }

  // App Sync filter: limits the dashboard roster to members who are app-linked
  // (appUserId set). Status stays visible but is never part of the filter.
  protected readonly appSyncOnly = signal(false);
  protected toggleAppSync(value: boolean): void {
    this.appSyncOnly.set(value);
    this.rosterPage.set(1);
  }

  public constructor() {
    const intervalId = window.setInterval(() => this.now.set(Date.now()), 1000);
    this.destroyRef.onDestroy(() => window.clearInterval(intervalId));
  }

  // ----- Re-implemented small reads via this.activity -----

  protected initials(value: string | null | undefined): string {
    const name = (value ?? '').trim();
    if (!name) return '??';
    const parts = name.split(/\s+/).filter(Boolean);
    if (parts.length === 1) return parts[0].slice(0, 2).toUpperCase();
    return (parts[0][0] + parts[1][0]).toUpperCase();
  }

  protected memberAvatarClass(name?: string | null): string {
    const trimmed = (name ?? '').trim();
    if (!trimmed) return 'a';
    let hash = 0;
    for (let i = 0; i < trimmed.length; i += 1) {
      hash = (hash * 31 + trimmed.charCodeAt(i)) >>> 0;
    }
    return ['a', 'b', 'c', 'd', 'e'][hash % 5];
  }

  protected memberStatusClass(status?: string | null): string {
    const normalized = (status ?? 'Active').toLowerCase();
    if (normalized === 'active') return 'success';
    if (normalized === 'pending') return 'warning';
    return 'default';
  }

  protected primaryLinkshell() {
    return this.activity.overview()?.primaryLinkshell ?? null;
  }

  protected dashboardLinkshells() {
    return this.activity.overview()?.linkshells ?? [];
  }

  protected canManageLinkshell(linkshellId: number): boolean {
    const membership = (this.activity.overview()?.linkshells ?? []).find(link => link.id === linkshellId);
    const rank = (membership?.rank ?? '').toLowerCase();
    return rank === 'leader' || rank === 'officer';
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

  protected selectedDashboardMembers() {
    const selectedId = this.selectedDashboardLinkshellId();
    if (this.primaryLinkshell()?.id !== selectedId) {
      return [];
    }

    return [...(this.primaryLinkshell()?.members ?? [])].sort((left, right) =>
      left.characterName.localeCompare(right.characterName)
    );
  }

  protected filteredDashboardMembers() {
    const term = this.dashboardRosterSearch.trim().toLowerCase();
    const appSyncOnly = this.appSyncOnly();
    const members = this.selectedDashboardMembers();
    return members.filter(member => {
      if (appSyncOnly) {
        if (!member.appUserId) return false;
      }
      if (term) {
        const nameMatch = (member.characterName ?? '').toLowerCase().includes(term);
        const rankMatch = (member.rank ?? '').toLowerCase().includes(term);
        if (!nameMatch && !rankMatch) return false;
      }
      return true;
    });
  }

  // Roster pagination: 5 per page (compact dashboard card). rosterPage is
  // 1-based; every read clamps to the valid range so a shrinking member list
  // or a narrowed search can't strand the view on an empty out-of-range page
  // (the search setter also resets to page 1). Clamping is pure — no signal
  // writes during render; only the Prev/Next handlers mutate the signal.
  protected readonly rosterPageSize = 5;
  protected readonly rosterPage = signal(1);

  protected rosterTotalPages(): number {
    return Math.max(1, Math.ceil(this.filteredDashboardMembers().length / this.rosterPageSize));
  }

  protected rosterCurrentPage(): number {
    return Math.min(Math.max(1, this.rosterPage()), this.rosterTotalPages());
  }

  protected pagedDashboardMembers() {
    const start = (this.rosterCurrentPage() - 1) * this.rosterPageSize;
    return this.filteredDashboardMembers().slice(start, start + this.rosterPageSize);
  }

  protected rosterPrev(): void {
    this.rosterPage.set(Math.max(1, this.rosterCurrentPage() - 1));
  }

  protected rosterNext(): void {
    this.rosterPage.set(Math.min(this.rosterTotalPages(), this.rosterCurrentPage() + 1));
  }

  protected canManageSelectedDashboard(): boolean {
    return this.canManageLinkshell(this.selectedDashboardLinkshellId());
  }

  // ----- Rules / announcements (dashboard-only) -----

  protected showRuleForm = signal(false);
  protected ruleTitle = '';
  protected ruleDetails = '';
  protected editingRuleId = signal<number | null>(null);
  protected showAnnouncementForm = signal(false);
  protected announcementTitle = '';
  protected announcementDetails = '';
  protected editingAnnouncementId = signal<number | null>(null);

  protected selectedDashboardRules() {
    const selectedId = this.selectedDashboardLinkshellId();
    if (this.primaryLinkshell()?.id !== selectedId) return [];
    return this.primaryLinkshell()?.rules ?? [];
  }

  protected selectedDashboardAnnouncements() {
    const selectedId = this.selectedDashboardLinkshellId();
    if (this.primaryLinkshell()?.id !== selectedId) return [];
    return this.primaryLinkshell()?.announcements ?? [];
  }

  protected toggleRuleForm(): void {
    this.showRuleForm.update(value => !value);
    this.editingRuleId.set(null);
    if (!this.showRuleForm()) {
      this.ruleTitle = '';
      this.ruleDetails = '';
    }
  }

  protected toggleAnnouncementForm(): void {
    this.showAnnouncementForm.update(value => !value);
    this.editingAnnouncementId.set(null);
    if (!this.showAnnouncementForm()) {
      this.announcementTitle = '';
      this.announcementDetails = '';
    }
  }

  protected startEditRule(rule: { id: number; title: string; details: string }): void {
    this.editingRuleId.set(rule.id);
    this.ruleTitle = rule.title;
    this.ruleDetails = rule.details;
    this.showRuleForm.set(true);
  }

  protected cancelEditRule(): void {
    this.editingRuleId.set(null);
    this.ruleTitle = '';
    this.ruleDetails = '';
    this.showRuleForm.set(false);
  }

  protected startEditAnnouncement(announcement: { id: number; title: string; details: string }): void {
    this.editingAnnouncementId.set(announcement.id);
    this.announcementTitle = announcement.title;
    this.announcementDetails = announcement.details;
    this.showAnnouncementForm.set(true);
  }

  protected cancelEditAnnouncement(): void {
    this.editingAnnouncementId.set(null);
    this.announcementTitle = '';
    this.announcementDetails = '';
    this.showAnnouncementForm.set(false);
  }

  protected async submitRule(): Promise<void> {
    const linkshellId = this.selectedDashboardLinkshellId();
    if (!linkshellId) return;
    const title = this.ruleTitle.trim();
    const details = this.ruleDetails.trim();
    if (!title || !details) return;
    const editingId = this.editingRuleId();
    try {
      if (editingId !== null) {
        await this.activity.updateRule(editingId, title, details);
      } else {
        await this.activity.createRule(linkshellId, title, details);
      }
      this.ruleTitle = '';
      this.ruleDetails = '';
      this.showRuleForm.set(false);
      this.editingRuleId.set(null);
    } catch {
      // error is surfaced via activity.actionError
    }
  }

  protected async submitAnnouncement(): Promise<void> {
    const linkshellId = this.selectedDashboardLinkshellId();
    if (!linkshellId) return;
    const title = this.announcementTitle.trim();
    const details = this.announcementDetails.trim();
    if (!title || !details) return;
    const editingId = this.editingAnnouncementId();
    try {
      if (editingId !== null) {
        await this.activity.updateAnnouncement(editingId, title, details);
      } else {
        await this.activity.createAnnouncement(linkshellId, title, details);
      }
      this.announcementTitle = '';
      this.announcementDetails = '';
      this.showAnnouncementForm.set(false);
      this.editingAnnouncementId.set(null);
    } catch {
      // error is surfaced via activity.actionError
    }
  }

  protected async deleteRule(ruleId: number): Promise<void> {
    try { await this.activity.deleteRule(ruleId); } catch { /* surfaced */ }
  }

  protected async deleteAnnouncement(announcementId: number): Promise<void> {
    try { await this.activity.deleteAnnouncement(announcementId); } catch { /* surfaced */ }
  }

  // ----- Dashboard upcoming events / clocks -----

  protected dashboardUpcomingEvents() {
    return this.selectedDashboardEvents()
      .filter(event => !event.commencementStartTime)
      .slice(0, 4);
  }

  // Maps an event type/category (Sky, Sea, Dynamis, Limbus, ...) to a themed
  // FFXI thumbnail served from the Activity's public folder. Types without a
  // dedicated image (HNM, HENM, NM, BCNM, KSNM, blanks) return null so the
  // caller falls back to the plain placeholder box.
  private static readonly EVENT_TYPE_IMAGES: Record<string, string> = {
    sky: 'ffxi_assets/Other/Sky.jpg',
    sea: 'ffxi_assets/Other/Sea.jpg',
    dynamis: 'ffxi_assets/Other/Dynamis.jpg',
    limbus: 'ffxi_assets/Other/Limbus.jpg'
  };

  protected eventTypeImage(type?: string | null): string | null {
    const key = (type ?? '').trim().toLowerCase();
    return DashboardTabComponent.EVENT_TYPE_IMAGES[key] ?? null;
  }

  protected eventRelativeLabel(value?: string | null): string {
    const target = parseDate(value);
    if (!target) return '';
    const deltaMs = target - this.now();
    if (deltaMs <= 0) return 'Now';
    const minutes = Math.floor(deltaMs / 60000);
    if (minutes < 60) return `in ${minutes}m`;
    const hours = Math.floor(minutes / 60);
    const remainderMinutes = minutes % 60;
    if (hours < 24) {
      return remainderMinutes > 0 ? `in ${hours}h ${remainderMinutes}m` : `in ${hours}h`;
    }
    const days = Math.floor(hours / 24);
    return `in ${days} day${days === 1 ? '' : 's'}`;
  }

  protected eventClockLabel(value?: string | null): string {
    const target = parseDate(value);
    if (!target) return '—';
    const date = new Date(target);
    const now = new Date(this.now());
    const sameDay = date.toDateString() === now.toDateString();
    if (sameDay) {
      return date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', hour12: false });
    }
    const weekday = date.toLocaleDateString([], { weekday: 'short' });
    const time = date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', hour12: false });
    return `${weekday} ${time}`;
  }

  // ----- HNM donut -----

  protected dashboardHnmWindow: '7d' | '30d' | 'all' = '30d';

  protected dashboardHnmClaims(): { monsterName: string; count: number; percent: number; colorClass: string }[] {
    // Restrict the donut to true HNMs (Fafnir / Nidhogg / Behemoth / Tiamat
    // / Bahamut / etc.) — Sky farm pops, ground NMs, HENMs, and Sea NMs are
    // tracked elsewhere and would otherwise dominate the chart.
    const tods = (this.activity.overview()?.recentTods ?? [])
      .filter(tod => tod.linkshellId === this.selectedDashboardLinkshellId()
                  && tod.claim
                  && HNM_NAMES.has((tod.monsterName ?? '').trim()));

    const cutoffMs = this.dashboardHnmWindow === 'all'
      ? 0
      : this.dashboardHnmWindow === '7d' ? 7 * 86400000 : 30 * 86400000;

    const nowMs = this.now();
    const filtered = cutoffMs === 0 ? tods : tods.filter(tod => {
      const timeMs = parseDate(tod.time) ?? 0;
      return timeMs > 0 && (nowMs - timeMs) <= cutoffMs;
    });

    const counts = new Map<string, number>();
    for (const tod of filtered) {
      const name = (tod.monsterName ?? 'Unknown').trim();
      counts.set(name, (counts.get(name) ?? 0) + 1);
    }

    const total = filtered.length;
    const palette = ['a', 'b', 'c', 'd', 'e', 'f'];
    return Array.from(counts.entries())
      .sort((left, right) => right[1] - left[1])
      .slice(0, 6)
      .map((entry, index) => ({
        monsterName: entry[0],
        count: entry[1],
        percent: total === 0 ? 0 : (entry[1] / total) * 100,
        colorClass: palette[index % palette.length]
      }));
  }

  protected dashboardHnmClaimsTotal(): number {
    return this.dashboardHnmClaims().reduce((sum, entry) => sum + entry.count, 0);
  }

  protected donutOffset(index: number): number {
    const circumference = 251.2;
    const claims = this.dashboardHnmClaims();
    const total = claims.reduce((sum, entry) => sum + entry.count, 0);
    if (total === 0) return 0;
    let offset = 0;
    for (let i = 0; i < index; i += 1) {
      offset += (claims[i].count / total) * circumference;
    }
    return -offset;
  }

  protected donutSegmentLength(count: number): number {
    const circumference = 251.2;
    const total = this.dashboardHnmClaims().reduce((sum, entry) => sum + entry.count, 0);
    if (total === 0) return 0;
    return (count / total) * circumference;
  }

  // ----- News & recent activity -----

  protected dashboardNewsUpdates(): {
    title: string;
    subtitle: string;
    dkp: number | null;
    iconPath: string | null;
    relative: string;
    colorClass: string;
    when: number;
  }[] {
    const selectedId = this.selectedDashboardLinkshellId();
    const items: ReturnType<typeof this.dashboardNewsUpdates> = [];

    // The "newsy" feed: announcements, rules, auctions, DKP adjustments, and new
    // members. Operational stuff (kills/claims/loot/events) lives in Recent
    // Activity instead. All sourced from the active linkshell in the overview.
    const primary = this.activity.overview()?.primaryLinkshell;
    if (!primary || primary.id !== selectedId) {
      return [];
    }

    for (const announcement of primary.announcements ?? []) {
      const when = parseDate(announcement.createdAt) ?? 0;
      items.push({
        title: announcement.title,
        subtitle: announcement.createdByCharacterName
          ? `Announcement · ${announcement.createdByCharacterName}`
          : 'Announcement',
        dkp: null,
        iconPath: null,
        relative: this.shortPastRelative(when),
        colorClass: 'c',
        when
      });
    }

    for (const rule of primary.rules ?? []) {
      const when = parseDate(rule.createdAt) ?? 0;
      items.push({
        title: rule.title,
        subtitle: 'Rule updated',
        dkp: null,
        iconPath: null,
        relative: this.shortPastRelative(when),
        colorClass: 'd',
        when
      });
    }

    for (const auction of primary.recentAuctions ?? []) {
      const when = parseDate(auction.when) ?? 0;
      items.push({
        title: `${auction.title} ${auction.closed ? 'closed' : 'opened'}`,
        subtitle: 'Auction',
        dkp: null,
        iconPath: 'ffxi_assets/Other/Auction.jpg',
        relative: this.shortPastRelative(when),
        colorClass: 'e',
        when
      });
    }

    for (const audit of primary.recentDkpAudits ?? []) {
      const when = parseDate(audit.occurredAt) ?? 0;
      const sign = audit.amount >= 0 ? '+' : '';
      items.push({
        title: `${audit.characterName} DKP ${sign}${audit.amount}`,
        subtitle: audit.isCorrection ? 'DKP correction' : 'DKP adjustment',
        dkp: null,
        // Relative (no leading slash) so it resolves against the app's
        // /discord-activity/ base href — the only static path that is both
        // served by the web app AND covered by the Discord proxy's URL
        // mappings. The file ships from discord-activity/public/.
        iconPath: 'ffxi_assets/Other/DKP.jpg',
        relative: this.shortPastRelative(when),
        colorClass: 'f',
        when
      });
    }

    for (const member of primary.members ?? []) {
      if (!member.dateJoined) continue;
      const when = parseDate(member.dateJoined) ?? 0;
      items.push({
        title: `${member.characterName} joined`,
        subtitle: 'New member',
        dkp: null,
        iconPath: 'ffxi_assets/Other/NewMember.jpg',
        relative: this.shortPastRelative(when),
        colorClass: 'a',
        when
      });
    }

    return items
      .filter(item => item.when > 0)
      .sort((left, right) => right.when - left.when)
        .slice(0, 10);
  }

  protected activityFilter: 'all' | 'kills' | 'claims' | 'events' | 'loot' = 'all';

  protected dashboardRecentActivity(): {
    kind: 'loot' | 'no-claim' | 'claim' | 'event' | 'announcement' | 'rule';
    name: string;
    action: string;
    detail: string;
    dkp: number | null;
    categoryLabel: string;
    relative: string;
    when: number;
  }[] {
    const selectedId = this.selectedDashboardLinkshellId();
    const items: ReturnType<typeof this.dashboardRecentActivity> = [];

    const tods = (this.activity.overview()?.recentTods ?? []).filter(tod => tod.linkshellId === selectedId);
    for (const tod of tods) {
      const when = parseDate(tod.time) ?? 0;
      if (tod.claim && tod.lootDetails?.length) {
        for (const loot of tod.lootDetails) {
          const winner = (loot.itemWinner ?? '').trim();
          const dkp = loot.winningDkpSpent ?? null;
          const detail = `${loot.itemName || 'Loot'}${winner ? ` → ${winner}` : ''}`;
          items.push({
            kind: 'loot',
            name: tod.monsterName,
            action: 'defeated',
            detail,
            dkp,
            categoryLabel: 'Loot',
            relative: this.longPastRelative(when),
            when
          });
        }
      } else if (tod.claim) {
        const linkshell = this.activity.overview()?.linkshells?.find(ls => ls.id === tod.linkshellId);
        items.push({
          kind: 'claim',
          name: tod.monsterName,
          action: 'claimed',
          detail: linkshell?.name ? `by ${linkshell.name}` : '',
          dkp: null,
          categoryLabel: 'Claimed',
          relative: this.longPastRelative(when),
          when
        });
      } else {
        items.push({
          kind: 'no-claim',
          name: tod.monsterName,
          action: 'defeated',
          detail: 'No claim',
          dkp: null,
          categoryLabel: 'No claim',
          relative: this.longPastRelative(when),
          when
        });
      }
    }

    // Loot entered during a LIVE (still-running) event — distinct from ToD loot
    // and from closed-event loot. Without this, loot logged in the live event
    // system didn't appear in Recent Activity until the event was archived.
    for (const event of this.selectedDashboardEvents()) {
      if (!event.loot?.length) { continue; }
      const when = parseDate(event.commencementStartTime ?? event.startTime) ?? 0;
      for (const loot of event.loot) {
        const winner = (loot.itemWinner ?? '').trim();
        items.push({
          kind: 'loot',
          name: event.name || 'Event',
          action: 'loot',
          detail: `${loot.itemName || 'Loot'}${winner ? ` → ${winner}` : ''}`,
          dkp: loot.winningDkpSpent ?? null,
          categoryLabel: 'Loot',
          relative: this.longPastRelative(when),
          when
        });
      }
    }

    for (const history of this.selectedDashboardHistory()) {
      const when = parseDate(history.endTime) ?? 0;
      const parts: string[] = [`${history.participantCount} participants`];
      if (history.type) parts.push(history.type);
      if (history.location) parts.push(history.location);
      items.push({
        kind: 'event',
        name: history.name || 'Event',
        action: 'completed',
        detail: parts.join(' · '),
        dkp: null,
        categoryLabel: 'Event',
        relative: this.longPastRelative(when),
        when
      });
    }

    // Announcements + rules now live in the News & Updates feed, not here —
    // Recent Activity is the operational feed (kills/claims/loot/events).

    const filter = this.activityFilter;
    const filtered = items.filter(item => {
      if (filter === 'all') return true;
      if (filter === 'kills') return item.kind === 'loot' || item.kind === 'no-claim' || item.kind === 'claim';
      if (filter === 'claims') return item.kind === 'claim';
      if (filter === 'events') return item.kind === 'event';
      if (filter === 'loot') return item.kind === 'loot';
      return true;
    });

    return filtered
      .filter(item => item.when > 0)
      .sort((left, right) => right.when - left.when)
      .slice(0, 10);
  }

  private shortPastRelative(whenMs: number): string {
    if (!whenMs) return '';
    const deltaSeconds = Math.max(0, Math.floor((this.now() - whenMs) / 1000));
    const minutes = Math.floor(deltaSeconds / 60);
    if (minutes < 60) return minutes <= 1 ? 'just now' : `${minutes}m ago`;
    const hours = Math.floor(minutes / 60);
    if (hours < 24) return `${hours}h ago`;
    const days = Math.floor(hours / 24);
    if (days === 1) return 'yesterday';
    if (days < 7) {
      const date = new Date(whenMs);
      return date.toLocaleDateString([], { weekday: 'short' });
    }
    return `${days}d ago`;
  }

  private longPastRelative(whenMs: number): string {
    if (!whenMs) return '';
    const deltaSeconds = Math.max(0, Math.floor((this.now() - whenMs) / 1000));
    const days = Math.floor(deltaSeconds / 86400);
    const hours = Math.floor((deltaSeconds % 86400) / 3600);
    const minutes = Math.floor((deltaSeconds % 3600) / 60);
    return `${days}d ${hours}h ${minutes}m`;
  }

  protected selectedDashboardEvents() {
    const selectedId = this.selectedDashboardLinkshellId();
    return [...(this.activity.overview()?.activeEvents ?? [])]
      .filter(event => event.linkshellId === selectedId)
      .sort((left, right) => {
        const leftTime = left.startTime ? new Date(left.startTime).getTime() : 0;
        const rightTime = right.startTime ? new Date(right.startTime).getTime() : 0;
        return leftTime - rightTime;
      });
  }

  protected selectedDashboardHistory() {
    const selectedId = this.selectedDashboardLinkshellId();
    return this.activity.historyList().filter(history => history.linkshellId === selectedId);
  }

  protected selectedDashboardTods() {
    const selectedId = this.selectedDashboardLinkshellId();
    // Per-linkshell hidden-mob list, configured in the Customize form.
    // Names compared case-insensitively after trimming so casing in the
    // textarea doesn't have to be exact.
    const link = this.activity.overview()?.linkshells?.find(l => l.id === selectedId);
    const hidden = new Set(
      (link?.settings?.hiddenTodMonsters ?? []).map(name => name.trim().toLowerCase())
    );
    return [...(this.activity.overview()?.recentTods ?? [])]
      .filter(tod => tod.linkshellId === selectedId)
      .filter(tod => !hidden.has((tod.monsterName ?? '').trim().toLowerCase()))
      .sort((left, right) => {
        const leftTime = left.time ? new Date(left.time).getTime() : 0;
        const rightTime = right.time ? new Date(right.time).getTime() : 0;
        return rightTime - leftTime;
      });
  }

  protected upcomingDashboardAuctionsCount(): number {
    const selectedId = this.selectedDashboardLinkshellId();
    return this.activity.auctions()
      .filter(a => a.linkshellId === selectedId)
      .filter(a => {
        const s = (a.status || '').toLowerCase();
        return s !== 'closed' && s !== 'archived' && s !== 'ended';
      }).length;
  }

  protected upcomingDashboardTodsCount(): number {
    const now = Date.now();
    return this.selectedDashboardTods().filter(tod => {
      if (!tod.repopTime) return false;
      const repop = new Date(tod.repopTime).getTime();
      return Number.isFinite(repop) && repop > now;
    }).length;
  }

  // ----- ToD tracker (dashboard view) -----

  protected readonly expandedTodGroups = signal<Set<string>>(new Set());

  protected groupedDashboardTods(): { key: string; latest: ActivityTodEntry; history: ActivityTodEntry[] }[] {
    const groups = new Map<string, ActivityTodEntry[]>();
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

  protected todCountdownLabel(tod: { repopTime?: string | null }): string {
    const remainingMilliseconds = this.remainingMs(tod.repopTime);
    return remainingMilliseconds <= 0 ? 'Ready' : formatElapsed(remainingMilliseconds);
  }

  protected isTodReady(tod: { repopTime?: string | null }): boolean {
    return this.remainingMs(tod.repopTime) <= 0;
  }

  private remainingMs(targetValue?: string | null): number {
    const targetTime = parseDate(targetValue);
    if (!targetTime) {
      return 0;
    }

    return Math.max(0, targetTime - this.now());
  }

  // ----- Roster rank editing (only used here in the dashboard's read-only roster?
  // Actually only the linkshell tab uses the editing UI; the dashboard view is
  // read-only. We keep them out of this component.) -----

  // ----- Linkshell tab navigation handoff -----

  protected setActiveTab(tab: TabName): void {
    this.setActiveTabFn(tab);
  }

  protected deleteTod(todId: number, monsterName: string): void {
    this.deleteTodFn(todId, monsterName);
  }
}
