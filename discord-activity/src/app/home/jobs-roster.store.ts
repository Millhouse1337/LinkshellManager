import { Injectable, inject, signal } from '@angular/core';
import {
  ActivityJobsRoster,
  ActivityJobsRosterMember,
  DiscordActivityService
} from '../discord/discord-activity.service';

/** One leveled job on a member's pill row. */
export interface RosterJobPill {
  name: string;
  level: number;
  strong: boolean;
  relic: boolean;
  merit: string;
  relicName: string;
}

/** A member's main character or one of their named alts, ready to render. */
export interface RosterCharacterJobs {
  label: string;
  isAlt: boolean;
  levels: number[];
  strong: boolean[];
  relic: boolean[];
  merit: string[];
  relicName: string[];
}

/**
 * The linkshell's jobs roster (every member's leveled jobs), fetched once and
 * shared by every view that renders job pills: the Dashboard roster's "Show Jobs"
 * column, the Manage Team roster's, and the per-member profile modal. Tab
 * components are destroyed when you switch tabs, so caching here (rather than in
 * each component) is what keeps a tab hop from re-fetching the same roster.
 *
 * Nothing loads until a caller asks for it via ensure().
 */
@Injectable({ providedIn: 'root' })
export class JobsRosterStore {
  private readonly activity = inject(DiscordActivityService);
  private readonly roster = signal<ActivityJobsRoster | null>(null);
  private readonly loadedFor = signal<number | null>(null);
  // De-dupes concurrent ensure() calls (both tabs, or a row + the profile modal)
  // into one request per linkshell.
  private pending: { linkshellId: number; promise: Promise<void> } | null = null;

  readonly busy = signal(false);

  /** The cached roster, but only when it belongs to the linkshell being asked about. */
  forLinkshell(linkshellId: number): ActivityJobsRoster | null {
    return this.loadedFor() === linkshellId ? this.roster() : null;
  }

  async ensure(linkshellId: number): Promise<void> {
    if (linkshellId <= 0 || this.forLinkshell(linkshellId)) return;
    if (this.pending?.linkshellId === linkshellId) return this.pending.promise;

    const promise = this.fetch(linkshellId);
    this.pending = { linkshellId, promise };
    try {
      await promise;
    } finally {
      if (this.pending?.linkshellId === linkshellId) this.pending = null;
    }
  }

  private async fetch(linkshellId: number): Promise<void> {
    this.busy.set(true);
    try {
      const data = await this.activity.loadJobsRoster(linkshellId);
      if (data) {
        this.roster.set(data);
        this.loadedFor.set(linkshellId);
      }
    } finally {
      this.busy.set(false);
    }
  }

  /**
   * The jobs entry for a roster row. Both the linkshell roster and the jobs
   * roster are keyed by membership id, so a row maps to its jobs directly.
   * Null for members with no entry (never linked the app).
   */
  memberFor(linkshellId: number, memberId: number): ActivityJobsRosterMember | null {
    return this.forLinkshell(linkshellId)?.members.find(member => member.id === memberId) ?? null;
  }

  /** Main + named alts for one member, as labeled characters to render. */
  characters(member: ActivityJobsRosterMember): RosterCharacterJobs[] {
    const list: RosterCharacterJobs[] = [{
      label: member.characterName, isAlt: false,
      levels: member.jobLevels ?? [], strong: member.strongJobs ?? [],
      relic: member.relicFlags ?? [], merit: member.meritJobs ?? [], relicName: member.relicNames ?? []
    }];
    if (member.alt1Name) {
      list.push({
        label: member.alt1Name, isAlt: true,
        levels: member.alt1JobLevels ?? [], strong: member.alt1StrongJobs ?? [],
        relic: member.alt1RelicFlags ?? [], merit: member.alt1MeritJobs ?? [], relicName: member.alt1RelicNames ?? []
      });
    }
    if (member.alt2Name) {
      list.push({
        label: member.alt2Name, isAlt: true,
        levels: member.alt2JobLevels ?? [], strong: member.alt2StrongJobs ?? [],
        relic: member.alt2RelicFlags ?? [], merit: member.alt2MeritJobs ?? [], relicName: member.alt2RelicNames ?? []
      });
    }
    return list;
  }

  /** Main + alts for a roster row, or an empty list when the member has no entry. */
  charactersFor(linkshellId: number, memberId: number): RosterCharacterJobs[] {
    const member = this.memberFor(linkshellId, memberId);
    return member ? this.characters(member) : [];
  }

  /**
   * The leveled jobs (level > 0) for one character, highest level first; each
   * carries its "strong" (merited) flag + relic flag/weapon + merit note for the pills.
   */
  leveledJobs(
    levels: number[] | null | undefined,
    strong?: boolean[] | null,
    relic?: boolean[] | null,
    merit?: string[] | null,
    relicName?: string[] | null
  ): RosterJobPill[] {
    const catalog = this.roster()?.jobCatalog ?? [];
    const arr = levels ?? [];
    const flags = strong ?? [];
    const relicFlags = relic ?? [];
    const meritNotes = merit ?? [];
    const relicNames = relicName ?? [];
    return catalog
      .map((name, i) => ({
        name,
        level: arr[i] ?? 0,
        strong: flags[i] ?? false,
        relic: relicFlags[i] ?? false,
        merit: meritNotes[i] ?? '',
        relicName: relicNames[i] ?? ''
      }))
      .filter(entry => entry.level > 0)
      .sort((a, b) => b.level - a.level);
  }

  /** Catalog job name for a rating's jobIndex. */
  jobName(jobIndex: number): string {
    return this.roster()?.jobCatalog?.[jobIndex] ?? `Job ${jobIndex + 1}`;
  }

  /** Hover tooltip for a job pill: relic (weapon name if known) + merit info. */
  pillTitle(job: { strong: boolean; relic: boolean; merit: string; relicName: string }): string | null {
    const parts: string[] = [];
    if (job.relic) { parts.push(job.relicName ? 'Relic: ' + job.relicName : 'Relic weapon'); }
    if (job.strong && job.merit) { parts.push('Merits: ' + job.merit); }
    else if (job.strong) { parts.push('Merited'); }
    return parts.length ? parts.join(' · ') : null;
  }

  /** True when a member's alt names or any job name/level matches a roster search term. */
  matchesSearch(linkshellId: number, memberId: number, term: string): boolean {
    const member = this.memberFor(linkshellId, memberId);
    if (!member) return false;

    if ([member.alt1Name, member.alt2Name].some(value => (value ?? '').toLowerCase().includes(term))) {
      return true;
    }

    return this.characters(member).some(character =>
      this.leveledJobs(character.levels, character.strong)
        .some(job => `${job.name} ${job.level}`.toLowerCase().includes(term))
    );
  }
}
