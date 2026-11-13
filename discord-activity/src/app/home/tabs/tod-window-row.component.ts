import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { DiscordActivityService } from '../../discord/discord-activity.service';
import type { ActivityMonsterSetup, ActivityTodEntry } from '../../discord/discord-activity.types';
import {
  TOD_NOT_ENTERED,
  formatElapsed,
  formatShortDuration,
  parseTodDurationMinutes,
  todWindowState
} from '../activity-home.helpers';
import { hasHqVariant } from '../activity-home.types';

// One Tracked Windows row: the monster and its tags, where the spawn is in its cycle, and a bar
// running from the time of death through the repop to the last window. The ToDs tab and the
// Dashboard both render it so the two can't drift apart; each projects its own buttons, history
// and panels underneath.
@Component({
  selector: 'app-tod-window-row',
  templateUrl: './tod-window-row.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'tod-row',
    '[attr.data-tone]': 'view().tone',
    '[attr.data-window-passed]': 'view().windowPassed ? "" : null'
  }
})
export class TodWindowRowComponent {
  private readonly activity = inject(DiscordActivityService);

  readonly tod = input.required<ActivityTodEntry>();
  // The merged "Base/Stronger" caption the parent grouped this spawn under.
  readonly displayName = input.required<string>();
  // The linkshell's setup for this monster — supplies the window count.
  readonly setup = input<ActivityMonsterSetup | null>(null);
  // The parent's one-second clock.
  readonly now = input.required<number>();

  protected readonly view = computed(() => {
    const tod = this.tod();
    const now = this.now();
    const { tone, todMs, repopMs, closeMs } = todWindowState(tod, this.setup(), now);

    let statusLabel: string;
    let big: string;
    switch (tone) {
      case 'pending':
        statusLabel = 'Repops in';
        big = formatElapsed(repopMs! - now);
        break;
      case 'open':
        statusLabel = 'Window open · closes in';
        big = formatElapsed(closeMs! - now);
        break;
      case 'up':
        // Every window has passed (or, with no window grid, the repop has), so it has popped by
        // now. Says what passed and how long ago, rather than a bare "Up 46h".
        statusLabel = closeMs !== null ? 'Last window passed' : 'Repop passed';
        big = `${formatShortDuration(now - (closeMs ?? repopMs!))} ago`;
        break;
      default:
        // No time of death was recorded, so there is nothing to count down to.
        statusLabel = 'Repop unknown';
        big = TOD_NOT_ENTERED;
    }

    // The logged cooldown, or the gap between the two stored times when the label is missing.
    const cooldownMinutes = parseTodDurationMinutes(tod.cooldown, 'hours');
    const respawnMs = cooldownMinutes !== null
      ? cooldownMinutes * 60_000
      : todMs !== null && repopMs !== null ? repopMs - todMs : null;
    const details = [
      // A Day only for the HQ/NQ pairs, where the day count decides which one pops.
      hasHqVariant(tod.monsterName) ? `Day ${tod.dayNumber ?? '-'}` : null,
      respawnMs !== null && respawnMs > 0 ? `Respawn ${formatShortDuration(respawnMs)}` : null,
      closeMs !== null && repopMs !== null ? `Window ${formatShortDuration(closeMs - repopMs)}` : null
    ].filter((part): part is string => part !== null);

    // Bar: grey from the time of death to the repop, then the spawn window, with a marker at now.
    let bar: { respawnPct: number; nowPct: number; respawnDone: boolean } | null = null;
    if (todMs !== null && repopMs !== null) {
      const span = (closeMs ?? repopMs) - todMs;
      if (span > 0) {
        bar = {
          respawnPct: ((repopMs - todMs) / span) * 100,
          nowPct: Math.min(100, Math.max(0, ((now - todMs) / span) * 100)),
          // A monster with no window has no window segment to light up, so the whole bar does.
          respawnDone: closeMs === null && tone === 'up'
        };
      }
    }

    return {
      tone,
      // Every window has gone by, which turns the row red. Not a monster with no window grid: it
      // shares the 'up' tone once its repop passes, but it has no window to miss.
      windowPassed: tone === 'up' && closeMs !== null,
      statusLabel,
      big,
      details,
      bar,
      todLabel: this.formatTime(todMs) ?? TOD_NOT_ENTERED,
      repopLabel: this.formatTime(repopMs) ?? TOD_NOT_ENTERED,
      closeLabel: this.formatTime(closeMs)
    };
  });

  // "Oct 31, 10:27:15 PM" in the viewer's zone: no year (always this one or the next), but the
  // seconds stay, because a ToD is timed to the second.
  private formatTime(ms: number | null): string | null {
    if (ms === null) return null;
    return this.activity.formatInViewerZone(ms, 'monthDayTimeSeconds');
  }
}
