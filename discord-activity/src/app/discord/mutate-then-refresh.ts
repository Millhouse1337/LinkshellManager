import { formatActionError } from './discord-activity.helpers';

// Every save in the app has the same two halves: send the change, then reload the page data so
// the screen shows it. They used to share one try/catch, so when the SAVE worked but the RELOAD
// failed (a slow server, a dropped connection) the officer was told the save failed. Retrying a
// loot entry, a DKP audit or a treasury payment then recorded it twice.
//
// This keeps the halves apart: once the server accepted the change it is reported as saved, and a
// failed reload only adds "press Refresh". A plain function (not a service) so every per-domain
// service can use it and it can be unit tested.

// The banner channel, which AuthService provides.
export interface ActionFeedback {
  setActionError(message: string | null): void;
  setActionMessage(message: string | null): void;
}

export interface MutateThenRefreshOptions<T> {
  // The save itself. If this throws, nothing was saved.
  mutate: () => Promise<T>;
  // Reloads the screen's data. A failure here never turns a save into a failure.
  refresh?: (result: T) => Promise<unknown>;
  // The success banner. A function can word it from the server's reply; null shows none.
  success: string | null | ((result: T) => string | null);
  // The error banner when the save itself failed and the server gave no message.
  failure: string;
}

export type MutationOutcome<T> = { ok: true; result: T } | { ok: false; error: unknown };

export const REFRESH_FAILED_NOTE = "The page couldn't refresh, so press Refresh to see the change.";

export async function mutateThenRefresh<T>(
  feedback: ActionFeedback,
  options: MutateThenRefreshOptions<T>
): Promise<MutationOutcome<T>> {
  feedback.setActionError(null);
  feedback.setActionMessage(null);

  let result: T;
  try {
    result = await options.mutate();
  } catch (error) {
    feedback.setActionError(formatActionError(error, options.failure));
    return { ok: false, error };
  }

  const success = typeof options.success === 'function' ? options.success(result) : options.success;
  try {
    await options.refresh?.(result);
    feedback.setActionMessage(success);
  } catch {
    feedback.setActionMessage(success ? `${success} ${REFRESH_FAILED_NOTE}` : `Saved. ${REFRESH_FAILED_NOTE}`);
  }
  return { ok: true, result };
}
