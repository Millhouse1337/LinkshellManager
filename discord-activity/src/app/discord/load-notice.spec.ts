import { describe, expect, it } from 'vitest';
import { loadWithNotice, type LoadNoticeFeedback } from './load-notice';

function notices(): LoadNoticeFeedback & { errors: Record<string, string> } {
  return {
    errors: {},
    reportLoadError(key, error, fallback) {
      this.errors[key] = error instanceof Error && error.message ? error.message : fallback;
    },
    clearLoadError(key) { delete this.errors[key]; }
  };
}

describe('loadWithNotice', () => {
  it('returns the data and takes down an earlier notice for the same thing', async () => {
    const feedback = notices();
    feedback.errors['linkshell-roles'] = 'Loading linkshell roles failed.';
    feedback.errors['monster-timings'] = 'Loading monster setups failed.';

    const result = await loadWithNotice(feedback, 'linkshell-roles', 'Loading linkshell roles failed.',
      async () => ({ roles: [1, 2] }));

    expect(result).toEqual({ roles: [1, 2] });
    // Only its own key: another card's notice stays up until that card loads.
    expect(feedback.errors).toEqual({ 'monster-timings': 'Loading monster setups failed.' });
  });

  it('returns null and puts up a notice under its key when the load fails', async () => {
    const feedback = notices();
    const result = await loadWithNotice(feedback, 'discord-channels', 'Loading Discord channels failed.',
      async () => { throw new Error('The bot is not in this server.'); });

    expect(result).toBeNull();
    expect(feedback.errors).toEqual({ 'discord-channels': 'The bot is not in this server.' });
  });

  it('uses the fallback wording when the error says nothing', async () => {
    const feedback = notices();
    await loadWithNotice(feedback, 'job-ratings', 'Loading job ratings failed.', async () => { throw 'boom'; });

    expect(feedback.errors).toEqual({ 'job-ratings': 'Loading job ratings failed.' });
  });

  it('counts an empty answer as a good load and takes the notice down', async () => {
    const feedback = notices();
    feedback.errors['admin-settings'] = 'old';
    const result = await loadWithNotice(feedback, 'admin-settings', 'Loading the admin settings failed.',
      async () => null);

    expect(result).toBeNull();
    expect(feedback.errors).toEqual({});
  });
});
