import { Pipe, PipeTransform, inject } from '@angular/core';

import { DiscordActivityService } from '../discord/discord-activity.service';
import type { ZonedTimeStyle } from '../discord/zoned-time';

// `{{ value | zonedTime:'time' }}`: an instant on the viewer's PROFILE clock. Use it instead of
// Angular's `| date`, which writes the computer's zone (see zoned-time.ts for the styles).
//
// Not pure, because the answer also depends on the profile zone, which can change while the value
// stays the same (the profile is saved, or the overview arrives). It remembers its last answer and
// only formats again when the value, style or zone changed, so running on every check is cheap.
@Pipe({ name: 'zonedTime', pure: false })
export class ZonedTimePipe implements PipeTransform {
  private readonly activity = inject(DiscordActivityService);
  private lastValue: unknown = undefined;
  private lastStyle: ZonedTimeStyle | null = null;
  private lastZone = '';
  private lastText = '';

  transform(value: string | number | Date | null | undefined, style: ZonedTimeStyle = 'dateTime'): string {
    const zone = this.activity.viewerTimeZone();
    if (value !== this.lastValue || style !== this.lastStyle || zone !== this.lastZone) {
      this.lastValue = value;
      this.lastStyle = style;
      this.lastZone = zone;
      this.lastText = this.activity.formatInViewerZone(value, style) ?? '';
    }
    return this.lastText;
  }
}
