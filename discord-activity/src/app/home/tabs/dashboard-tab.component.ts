import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, Input, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivityTodEntry, DiscordActivityService } from '../../discord/discord-activity.service';
import type { ActivityEvent } from '../../discord/discord-activity.types';
import {
  TOD_NOT_ENTERED,
  formatAlts,
  isTodReady,
  memberAvatarClass,
  memberInitials,
  memberStatusClass,
  parseDate,
  todCountdownLabel,
  todSortKey
} from '../activity-home.helpers';
import { isHnmMonsterName, type TabName } from '../activity-home.types';
import { JobsRosterStore } from '../jobs-roster.store';
import { RULE_CATEGORY_OPTIONS, categoryBadge, parseRuleDetails } from '../rule-content.helpers';

@Component({
  selector: 'app-dashboard-tab',
  imports: [CommonModule, FormsModule],
  templateUrl: './dashboard-tab.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class DashboardTabComponent {
  protected readonly activity = inject(DiscordActivityService);
  // Shared with the Manage Team roster: whichever "Show Jobs" is switched on first
  // fetches the jobs roster, and the other reads it from the same cache.
  private readonly jobs = inject(JobsRosterStore);
  protected readonly formatAlts = formatAlts;
  // Shared roster helpers (avatar initials/color, status tag) — single source in
  // activity-home.helpers so every roster renders members identically. `initials`
  // keeps its template name and delegates to the shared memberInitials.
  protected readonly initials = memberInitials;
  protected readonly memberAvatarClass = memberAvatarClass;
  protected readonly memberStatusClass = memberStatusClass;
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
  protected set dashboardRosterSearch(value: string) { this.rosterSearchChange(value); }

  // "Show Jobs" switches the dashboard roster into a jobs view: Rank/DKP/Status
  // give way to each member's leveled jobs (main + alts), the same toggle the
  // Manage Team roster has. The data is lazy-loaded and shared between them.
  protected readonly showJobs = signal(false);
  protected async toggleShowJobs(value: boolean): Promise<void> {
    this.showJobs.set(value);
    if (value) await this.jobs.ensure(this.selectedDashboardLinkshellId());
  }

  protected readonly jobsBusy = this.jobs.busy;
  protected readonly leveledJobs = this.jobs.leveledJobs.bind(this.jobs);
  protected readonly pillTitle = this.jobs.pillTitle.bind(this.jobs);

  // Main + alts for a roster row's Jobs cell (empty when the member has no entry).
  protected jobsCharacters(memberId: number) {
    return this.jobs.charactersFor(this.selectedDashboardLinkshellId(), memberId);
  }

  public constructor() {
    const intervalId = window.setInterval(() => this.now.set(Date.now()), 1000);
    this.destroyRef.onDestroy(() => window.clearInterval(intervalId));
  }

  // ----- Re-implemented small reads via this.activity -----

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
    const members = this.selectedDashboardMembers();
    if (!term) {
      return members;
    }

    return members.filter(member => {
      const nameMatch = (member.characterName ?? '').toLowerCase().includes(term);
      const rankMatch = (member.rank ?? '').toLowerCase().includes(term);
      if (nameMatch || rankMatch) return true;
      // With the jobs view on, the search also reaches alt names and job names/levels.
      return this.showJobs() && this.jobs.matchesSearch(this.selectedDashboardLinkshellId(), member.id, term);
    });
  }

  protected canManageSelectedDashboard(): boolean {
    return this.canManageLinkshell(this.selectedDashboardLinkshellId());
  }

  // ----- Rules / announcements (dashboard-only) -----

  protected showRuleForm = signal(false);
  protected ruleTitle = '';
  protected ruleDetails = '';
  protected ruleCategorySelect = '';
  protected ruleCategoryCustom = '';
  protected editingRuleId = signal<number | null>(null);
  protected showAnnouncementForm = signal(false);
  protected announcementTitle = '';
  protected announcementDetails = '';
  protected announcementCategorySelect = '';
  protected announcementCategoryCustom = '';
  protected editingAnnouncementId = signal<number | null>(null);

  // Category picker presets (the dropdown), shared with the web via
  // rule-content.helpers (mirrors C# RuleContent).
  protected readonly categoryOptions = RULE_CATEGORY_OPTIONS;

  // Rules/announcements pre-shaped for the card layout: the row + its accent/icon
  // badge + parsed detail blocks (paragraphs + bullet lists), computed once per render.
  protected dashboardRuleCards() {
    return this.selectedDashboardRules().map((rule, i) => ({
      rule, badge: categoryBadge(rule.category, i), blocks: parseRuleDetails(rule.details)
    }));
  }
  protected dashboardAnnouncementCards() {
    return this.selectedDashboardAnnouncements().map((announcement, i) => ({
      announcement, badge: categoryBadge(announcement.category, i), blocks: parseRuleDetails(announcement.details)
    }));
  }

  // Map a stored category to the (select, custom) control values, and back.
  private categoryToControls(category: string | null | undefined): { select: string; custom: string } {
    const cur = (category ?? '').trim();
    if (!cur) { return { select: '', custom: '' }; }
    return RULE_CATEGORY_OPTIONS.includes(cur) ? { select: cur, custom: '' } : { select: '__other__', custom: cur };
  }
  private effectiveCategory(select: string, custom: string): string | null {
    if (select === '__other__') { const c = custom.trim(); return c.length ? c : null; }
    return select.trim().length ? select : null;
  }

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
      this.ruleCategorySelect = '';
      this.ruleCategoryCustom = '';
    }
  }

  protected toggleAnnouncementForm(): void {
    this.showAnnouncementForm.update(value => !value);
    this.editingAnnouncementId.set(null);
    if (!this.showAnnouncementForm()) {
      this.announcementTitle = '';
      this.announcementDetails = '';
      this.announcementCategorySelect = '';
      this.announcementCategoryCustom = '';
    }
  }

  protected startEditRule(rule: { id: number; title: string; details: string; category?: string | null }): void {
    this.editingRuleId.set(rule.id);
    this.ruleTitle = rule.title;
    this.ruleDetails = rule.details;
    const c = this.categoryToControls(rule.category);
    this.ruleCategorySelect = c.select;
    this.ruleCategoryCustom = c.custom;
    this.showRuleForm.set(true);
  }

  protected cancelEditRule(): void {
    this.editingRuleId.set(null);
    this.ruleTitle = '';
    this.ruleDetails = '';
    this.ruleCategorySelect = '';
    this.ruleCategoryCustom = '';
    this.showRuleForm.set(false);
  }

  protected startEditAnnouncement(announcement: { id: number; title: string; details: string; category?: string | null }): void {
    this.editingAnnouncementId.set(announcement.id);
    this.announcementTitle = announcement.title;
    this.announcementDetails = announcement.details;
    const c = this.categoryToControls(announcement.category);
    this.announcementCategorySelect = c.select;
    this.announcementCategoryCustom = c.custom;
    this.showAnnouncementForm.set(true);
  }

  protected cancelEditAnnouncement(): void {
    this.editingAnnouncementId.set(null);
    this.announcementTitle = '';
    this.announcementDetails = '';
    this.announcementCategorySelect = '';
    this.announcementCategoryCustom = '';
    this.showAnnouncementForm.set(false);
  }

  protected async submitRule(): Promise<void> {
    const linkshellId = this.selectedDashboardLinkshellId();
    if (!linkshellId) return;
    const title = this.ruleTitle.trim();
    const details = this.ruleDetails.trim();
    if (!title || !details) return;
    const editingId = this.editingRuleId();
    const category = this.effectiveCategory(this.ruleCategorySelect, this.ruleCategoryCustom);
    try {
      if (editingId !== null) {
        await this.activity.updateRule(editingId, title, details, category);
      } else {
        await this.activity.createRule(linkshellId, title, details, category);
      }
      this.ruleTitle = '';
      this.ruleDetails = '';
      this.ruleCategorySelect = '';
      this.ruleCategoryCustom = '';
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
    const category = this.effectiveCategory(this.announcementCategorySelect, this.announcementCategoryCustom);
    try {
      if (editingId !== null) {
        await this.activity.updateAnnouncement(editingId, title, details, category);
      } else {
        await this.activity.createAnnouncement(linkshellId, title, details, category);
      }
      this.announcementTitle = '';
      this.announcementDetails = '';
      this.announcementCategorySelect = '';
      this.announcementCategoryCustom = '';
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

  // Maps an event type/category (Sky, Sea, Dynamis, Limbus, HENM, ...) to a themed
  // FFXI thumbnail served from the Activity's public folder. Types without a
  // dedicated image (NM, BCNM, KSNM, blanks) return null so the caller falls back
  // to the plain placeholder box. HNM is absent on purpose — there is no single
  // "HNM" picture; those rows resolve per-monster below.
  private static readonly EVENT_TYPE_IMAGES: Record<string, string> = {
    sky: 'ffxi_assets/Other/Sky.jpg',
    sea: 'ffxi_assets/Other/Sea.jpg',
    dynamis: 'ffxi_assets/Other/Dynamis.jpg',
    limbus: 'ffxi_assets/Other/Limbus.jpg',
    // One shared image for every HENM row — unlike HNM, these aren't per-monster.
    henm: 'ffxi_assets/HENM/HENM.png'
  };

  // Per-monster HNM art from public/ffxi_assets/HNM. Keyed on the monster name
  // lower-cased with spaces stripped, because the files are unspaced PascalCase
  // ("King Behemoth" -> KingBehemoth.jpg). Bahamut and the Goblin testing
  // presets have no art in that folder and fall through to the placeholder.
  private static readonly HNM_MONSTER_IMAGES: Record<string, string> = {
    adamantoise: 'ffxi_assets/HNM/Adamantoise.jpg',
    aspidochelone: 'ffxi_assets/HNM/Aspidochelone.jpg',
    behemoth: 'ffxi_assets/HNM/Behemoth.jpg',
    cerberus: 'ffxi_assets/HNM/Cerberus.jpg',
    fafnir: 'ffxi_assets/HNM/Fafnir.jpg',
    hydra: 'ffxi_assets/HNM/Hydra.jpg',
    jormungand: 'ffxi_assets/HNM/Jormungand.jpg',
    khimaira: 'ffxi_assets/HNM/Khimaira.jpg',
    kingbehemoth: 'ffxi_assets/HNM/KingBehemoth.jpg',
    nidhogg: 'ffxi_assets/HNM/Nidhogg.jpg',
    simurgh: 'ffxi_assets/HNM/Simurgh.jpg',
    tiamat: 'ffxi_assets/HNM/Tiamat.jpg',
    vrtra: 'ffxi_assets/HNM/Vrtra.jpg'
  };

  protected eventTypeImage(type?: string | null): string | null {
    const key = (type ?? '').trim().toLowerCase();
    return DashboardTabComponent.EVENT_TYPE_IMAGES[key] ?? null;
  }

  // A combined "Base/Stronger" label ("Fafnir/Nidhogg") resolves to the first
  // half that has art, matching how the board names the day-1..3 spawn.
  private monsterImage(name?: string | null): string | null {
    for (const segment of (name ?? '').split('/')) {
      const key = segment.trim().toLowerCase().replace(/\s+/g, '');
      const image = DashboardTabComponent.HNM_MONSTER_IMAGES[key];
      if (image) { return image; }
    }
    return null;
  }

  // Thumbnail for an Upcoming Events row. The monster wins over the event type:
  // an HNM board carries its own AssignedMonsterName, and matching on type alone
  // meant every HNM row fell through to the blank grey box.
  protected eventThumbImage(event: ActivityEvent): string | null {
    return this.monsterImage(event.assignedMonsterName)
      ?? this.monsterImage(event.partySetupAssignedMonsterName)
      ?? this.eventTypeImage(event.type);
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
      return date.toLocaleTimeString([], { hour: 'numeric', minute: '2-digit', hour12: true });
    }
    const weekday = date.toLocaleDateString([], { weekday: 'short' });
    const time = date.toLocaleTimeString([], { hour: 'numeric', minute: '2-digit', hour12: true });
    return `${weekday} ${time}`;
  }

  // ----- HNM donut -----

  protected dashboardHnmWindow: '7d' | '30d' | 'all' = '30d';

  protected dashboardHnmClaims(): { monsterName: string; count: number; percent: number; colorClass: string }[] {
    // Restrict the donut to true HNMs (Fafnir / Nidhogg / Behemoth / Tiamat
    // / Bahamut / etc.) — Sky farm pops, ground NMs, HENMs, and Sea NMs are
    // tracked elsewhere and would otherwise dominate the chart. isHnmMonsterName
    // matches either half of a combined "Base/Stronger" label, which is how HNM
    // boards record the name.
    const tods = (this.activity.overview()?.recentTods ?? [])
      .filter(tod => tod.linkshellId === this.selectedDashboardLinkshellId()
                  && tod.claim
                  && isHnmMonsterName(tod.monsterName));

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
        // Relative path (no leading slash) so it resolves against the app's
        // /discord-activity/ base href; the file ships from discord-activity/public/.
        iconPath: 'ffxi_assets/Other/Announcements.png',
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
        // Relative path (no leading slash) so it resolves against the app's
        // /discord-activity/ base href; the file ships from discord-activity/public/.
        iconPath: 'ffxi_assets/Other/New_Rule.jpg',
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
    return [...(this.activity.overview()?.recentTods ?? [])]
      .filter(tod => tod.linkshellId === selectedId)
      // Newest first on time ?? timeStamp, so a ToD that was never entered still sorts as the
      // monster's latest instead of sinking below the pop it superseded.
      .sort((left, right) => todSortKey(right) - todSortKey(left));
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

  // Countdown / ready state / the "Not entered" label all live in activity-home.helpers so this
  // tab and the ToDs tab can't drift apart — they render the same card from the same data.
  protected readonly notEntered = TOD_NOT_ENTERED;

  protected todCountdownLabel(tod: { repopTime?: string | null }): string {
    return todCountdownLabel(tod.repopTime, this.now());
  }

  protected isTodReady(tod: { repopTime?: string | null }): boolean {
    return isTodReady(tod.repopTime, this.now());
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
