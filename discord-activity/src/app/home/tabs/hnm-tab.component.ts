import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { DiscordActivityService } from '../../discord/discord-activity.service';
import type {
  ActivityEvent,
  ActivityTodEntry,
} from '../../discord/discord-activity.types';
import { HNM_NAMES } from '../activity-home.types';

// Per-HNM zone + day-cycle catalog mirrored from Services/HnmConfig.cs. Keep
// these arrays in sync with the server when the curated set changes (rare).
// The render order is long-window first, short-window next, testing last --
// same as the web dashboard.
const HNM_ORDER: readonly string[] = [
  'Tiamat',
  'Jormungand',
  'Vrtra',
  'Fafnir',
  'Nidhogg',
  'Behemoth',
  'King Behemoth',
  'Adamantoise',
  'Aspidochelone',
  'Goblin Furrier',
  'Goblin Shaman',
];

const HNM_ZONES: Record<string, string> = {
  'Behemoth': "Behemoth's Dominion",
  'King Behemoth': "Behemoth's Dominion",
  'Fafnir': "Dragon's Aery",
  'Nidhogg': "Dragon's Aery",
  'Adamantoise': 'Qufim Island',
  'Aspidochelone': 'Qufim Island',
  'Tiamat': 'Attohwa Chasm',
  'Jormungand': 'Uleguerand Range',
  'Vrtra': 'Riverne - Site #B01',
};

const HNM_DAY_CYCLES: Record<string, number> = {
  'Nidhogg': 3,
  'King Behemoth': 5,
  'Aspidochelone': 3,
};

interface HnmRow {
  monsterName: string;
  zone: string | null;
  isDayTracked: boolean;
  dayCycle: number | null;
  latestTod: ActivityTodEntry | null;
  recentTods: ActivityTodEntry[];
  linkedEvent: ActivityEvent | null;
}

@Component({
  selector: 'app-hnm-tab',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './hnm-tab.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class HnmTabComponent {
  protected readonly activity = inject(DiscordActivityService);
  protected readonly expanded = signal<Set<string>>(new Set());

  protected readonly rows = computed<HnmRow[]>(() => {
    const overview = this.activity.overview();
    const selectedLinkshellId =
      overview?.appUser?.primaryLinkshellId ??
      overview?.primaryLinkshell?.id ??
      overview?.linkshells?.[0]?.id ??
      0;
    if (!overview || !selectedLinkshellId) return [];

    const allTods = (overview.recentTods ?? [])
      .filter(t => t.linkshellId === selectedLinkshellId)
      .filter(t => HNM_NAMES.has((t.monsterName ?? '').trim()));

    const allEvents = (overview.activeEvents ?? [])
      .filter(e => e.linkshellId === selectedLinkshellId)
      .filter(e => (e.type ?? '').trim().toUpperCase() === 'HNM');

    const todsByMonster = new Map<string, ActivityTodEntry[]>();
    for (const tod of allTods) {
      const key = (tod.monsterName ?? '').trim();
      if (!key) continue;
      const list = todsByMonster.get(key) ?? [];
      list.push(tod);
      todsByMonster.set(key, list);
    }
    for (const list of todsByMonster.values()) {
      list.sort((a, b) => {
        const at = a.time ? new Date(a.time).getTime() : 0;
        const bt = b.time ? new Date(b.time).getTime() : 0;
        return bt - at;
      });
    }

    return HNM_ORDER.map(name => {
      const monsterTods = todsByMonster.get(name) ?? [];
      const latest = monsterTods[0] ?? null;

      // Match the auto-event to the latest ToD: same monster, queued/live
      // (no endTime), startTime within +-15 minutes of repop. Mirrors the
      // server-side idempotency window so the UI groups the same event the
      // server treats as the auto-event for this ToD.
      let linkedEvent: ActivityEvent | null = null;
      if (latest?.repopTime) {
        const repopMs = new Date(latest.repopTime).getTime();
        const window = 15 * 60 * 1000;
        linkedEvent = allEvents.find(e => {
          if (e.endTime) return false;
          const evName = (e.name ?? '').trim();
          if (!evName.startsWith(name)) return false;
          if (!e.startTime) return false;
          const evStartMs = new Date(e.startTime).getTime();
          return Math.abs(evStartMs - repopMs) <= window;
        }) ?? null;
      }

      return {
        monsterName: name,
        zone: HNM_ZONES[name] ?? null,
        isDayTracked: name in HNM_DAY_CYCLES,
        dayCycle: HNM_DAY_CYCLES[name] ?? null,
        latestTod: latest,
        recentTods: monsterTods.slice(1, 4),
        linkedEvent,
      };
    });
  });

  protected toggleExpanded(monster: string): void {
    const next = new Set(this.expanded());
    if (next.has(monster)) next.delete(monster);
    else next.add(monster);
    this.expanded.set(next);
  }

  protected isExpanded(monster: string): boolean {
    return this.expanded().has(monster);
  }

  protected formatLocal(iso: string | null | undefined): string {
    if (!iso) return '—';
    const date = new Date(iso);
    if (Number.isNaN(date.getTime())) return '—';
    return date.toLocaleString();
  }

  protected eventStateLabel(event: ActivityEvent): string {
    if (event.endTime) return 'ended';
    if (event.commencementStartTime) return 'LIVE';
    return 'queued';
  }

  protected claimLabel(claim: boolean | null | undefined): string {
    if (claim === true) return 'Claimed';
    if (claim === false) return 'Unclaimed';
    return 'Not specified';
  }
}
