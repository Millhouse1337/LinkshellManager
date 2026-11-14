// A screen's reads (the roles list, Discord channels, monster setups, job ratings, ...) run when a
// card opens or the linkshell changes, not because someone pressed a button. They used to clear the
// red action banner when they started and write to it when they failed, so opening a card could
// wipe a real save error before it was read, or show a red "Loading ... failed" nobody asked for.
//
// This sends them to the amber load notice instead (AuthService.loadError), keyed by what they
// load: a failure puts the notice up, and the next good load of the same thing takes it down. The
// red banner is left to the user's own saves. A plain function so it can be unit tested.

// The load-notice channel, which AuthService provides.
export interface LoadNoticeFeedback {
  reportLoadError(key: string, error: unknown, fallback: string): void;
  clearLoadError(key: string): void;
}

// Returns what `load` returned, or null when it failed (the notice is then showing).
export async function loadWithNotice<T>(
  feedback: LoadNoticeFeedback,
  key: string,
  fallback: string,
  load: () => Promise<T>
): Promise<T | null> {
  try {
    const result = await load();
    feedback.clearLoadError(key);
    return result;
  } catch (error) {
    feedback.reportLoadError(key, error, fallback);
    return null;
  }
}
