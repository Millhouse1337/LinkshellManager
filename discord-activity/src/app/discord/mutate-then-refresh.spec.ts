import { describe, expect, it } from 'vitest';
import { REFRESH_FAILED_NOTE, mutateThenRefresh, type ActionFeedback } from './mutate-then-refresh';

function banners(): ActionFeedback & { error: string | null; message: string | null } {
  return {
    error: 'old error',
    message: 'old message',
    setActionError(value) { this.error = value; },
    setActionMessage(value) { this.message = value; }
  };
}

describe('mutateThenRefresh', () => {
  it('reports a save and its reload as saved', async () => {
    const feedback = banners();
    const outcome = await mutateThenRefresh(feedback, {
      mutate: async () => 7,
      refresh: async () => undefined,
      success: 'Loot entry added.',
      failure: 'Adding loot failed.'
    });
    expect(outcome).toEqual({ ok: true, result: 7 });
    expect(feedback.error).toBeNull();
    expect(feedback.message).toBe('Loot entry added.');
  });

  it('still reports the save as saved when only the reload fails, so nobody retries it', async () => {
    const feedback = banners();
    const outcome = await mutateThenRefresh(feedback, {
      mutate: async () => undefined,
      refresh: async () => { throw new Error('network down'); },
      success: 'DKP audit recorded.',
      failure: 'Submitting the DKP audit failed.'
    });
    expect(outcome.ok).toBe(true);
    expect(feedback.error).toBeNull();
    expect(feedback.message).toBe(`DKP audit recorded. ${REFRESH_FAILED_NOTE}`);
  });

  it('reports a failed save with the server message and never reloads', async () => {
    const feedback = banners();
    let refreshed = false;
    const outcome = await mutateThenRefresh(feedback, {
      mutate: async () => { throw new Error('Not enough DKP.'); },
      refresh: async () => { refreshed = true; },
      success: 'Saved.',
      failure: 'Saving failed.'
    });
    expect(outcome.ok).toBe(false);
    expect(refreshed).toBe(false);
    expect(feedback.error).toBe('Not enough DKP.');
    expect(feedback.message).toBeNull();
  });

  it('uses the fallback when the error has no message', async () => {
    const feedback = banners();
    await mutateThenRefresh(feedback, {
      mutate: async () => { throw 'boom'; },
      success: 'Saved.',
      failure: 'Saving failed.'
    });
    expect(feedback.error).toBe('Saving failed.');
  });

  it('words the success from the server reply', async () => {
    const feedback = banners();
    await mutateThenRefresh(feedback, {
      mutate: async () => ({ paid: 2 }),
      success: result => `Paid ${result.paid} members.`,
      failure: 'Paying failed.'
    });
    expect(feedback.message).toBe('Paid 2 members.');
  });
});
