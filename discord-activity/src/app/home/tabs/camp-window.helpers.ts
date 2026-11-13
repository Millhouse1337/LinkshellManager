// Pure "what is this camp's window called" helpers, moved out of EventsTabComponent so they can
// be unit tested (npm test) without building the component. The component keeps one-line
// forwarders with the same names, so its template is unchanged.

import type { ActivityClaimShieldCapture, ActivityEvent } from '../../discord/discord-activity.types';

// Only the fields these helpers read, so a test can pass a small object instead of a whole event.
export type CampWindowFields = Pick<
  ActivityEvent,
  'windowCount' | 'attendancePostCount' | 'postsOpenAndClose' | 'hnmFocusWindow' | 'hnmWindowNumber'
> & { attendanceWindows?: ActivityEvent['attendanceWindows'] | null };

// How many roster reads this camp takes — 2 on a Standard king/dragon (Open + Close), against
// the 7 SPAWN windows windowCount reports. Everything that NAMES or COUNTS a posted window goes
// by this; only the "Window N of M" camp heading goes by windowCount.
//
// Falls back to windowCount for a payload from a server predating the split, which is exactly
// what the code here read before the two were separated.
export function campPostCount(event: CampWindowFields): number {
  return event.attendancePostCount ?? event.windowCount ?? 1;
}

// Whether the camp's posts are a named Open and Close. The server decides it from the camp's
// Window Method; a payload from a server predating the field falls back to the old "two posts"
// test, which is what this was before Window N could have exactly two windows.
export function campPostsOpenAndClose(event: CampWindowFields): boolean {
  return event.postsOpenAndClose ?? campPostCount(event) === 2;
}

// An Open/Close camp whose Close has landed. Both the addon and the server refuse further
// windows at that point, so the card must stop implying one is outstanding.
export function campClosePosted(event: CampWindowFields): boolean {
  return campPostsOpenAndClose(event)
      && (event.attendanceWindows ?? []).some(w => w.sequenceNumber === 2);
}

// "Window N of M" for a live camp. Uses hnmFocusWindow (what the Discord board shows), NOT
// hnmWindowNumber — the raw counter reads one lower and made the app disagree with the board.
//
// A 2-post camp reads "Open" / "Close" instead: those two are roster snapshots, not a numbered
// sequence, and calling them Window 1 and Window 2 reads like a count of the kings'/dragons'
// seven SPAWN windows, which they aren't. Mirrors HnmConfig.GetDefaultWindowLabel server-side
// and constants.window_label in the addon — all three must agree or one camp gets three names.
export function campWindowLabel(event: CampWindowFields): string {
  const focus = event.hnmFocusWindow ?? event.hnmWindowNumber ?? 1;
  // An Open/Close camp with at most two spawn windows has no "Window N of M" worth printing —
  // that includes a custom NM with no window grid at all, which would otherwise read "of 1".
  // A king/dragon keeps its "Window N of 7" heading, matching the Discord board.
  if (campPostsOpenAndClose(event) && (event.windowCount ?? 1) <= 2) {
    // Once the Close is in, the camp is done posting whatever the cadence counter says.
    // Without this a Close-only camp read "Open" — naming a window it will never have and
    // can no longer accept — because focus tracks the clock, not what was posted.
    return campClosePosted(event) || focus > 1 ? 'Close' : 'Open';
  }
  return `Window ${focus} of ${event.windowCount}`;
}

// Where an archived camp's monster popped: "W4 of 7", or just "W4" for a camp archived before
// the count was recorded. Empty when no pop window was recorded at all.
export function popWindowLabel(popWindow?: number | null, popWindowCount?: number | null): string {
  if (popWindow == null) return '';
  return popWindowCount ? `W${popWindow} of ${popWindowCount}` : `W${popWindow}`;
}

// Hover text for a Claim Shield capture: whose addon read the lottery, then the game's own line.
// Null when neither was recorded, so the template sets no title at all.
export function claimCaptureHover(
  capture: Pick<ActivityClaimShieldCapture, 'capturedByCharacterName' | 'capturedMessage'>
): string | null {
  const by = capture.capturedByCharacterName?.trim();
  const line = capture.capturedMessage?.trim();
  const parts = [by ? `Captured by ${by}` : '', line ?? ''].filter(part => part.length > 0);
  return parts.length > 0 ? parts.join('\n') : null;
}
