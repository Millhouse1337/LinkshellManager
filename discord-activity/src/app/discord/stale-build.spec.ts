import { describe, expect, it, vi } from 'vitest';
import { isChunkLoadError, reloadOnceForStaleBuild } from './stale-build';

function memoryStorage(): Pick<Storage, 'getItem' | 'setItem'> {
  const values = new Map<string, string>();
  return {
    getItem: key => values.get(key) ?? null,
    setItem: (key, value) => void values.set(key, value)
  };
}

const chunkError = new TypeError(
  'Failed to fetch dynamically imported module: https://example.test/discord-activity/chunk-OLD12345.js');

describe('isChunkLoadError', () => {
  it('recognises the browsers\' failed dynamic import messages', () => {
    expect(isChunkLoadError(chunkError)).toBe(true);
    expect(isChunkLoadError(new TypeError('Importing a module script failed.'))).toBe(true);
    expect(isChunkLoadError(new Error('error loading dynamically imported module'))).toBe(true);
  });

  it('ignores other errors', () => {
    expect(isChunkLoadError(new Error('You do not have permission.'))).toBe(false);
    expect(isChunkLoadError(undefined)).toBe(false);
  });
});

describe('reloadOnceForStaleBuild', () => {
  it('reloads once for a failed chunk load', () => {
    const reload = vi.fn();
    expect(reloadOnceForStaleBuild(chunkError, { storage: memoryStorage(), reload, now: 1_000_000 })).toBe(true);
    expect(reload).toHaveBeenCalledOnce();
  });

  it('does not reload again within a minute, so an outage cannot loop', () => {
    const storage = memoryStorage();
    const reload = vi.fn();
    reloadOnceForStaleBuild(chunkError, { storage, reload, now: 1_000_000 });
    expect(reloadOnceForStaleBuild(chunkError, { storage, reload, now: 1_030_000 })).toBe(false);
    expect(reloadOnceForStaleBuild(chunkError, { storage, reload, now: 1_061_000 })).toBe(true);
    expect(reload).toHaveBeenCalledTimes(2);
  });

  it('never reloads without storage to remember it in, or for other errors', () => {
    const reload = vi.fn();
    expect(reloadOnceForStaleBuild(chunkError, { storage: null, reload, now: 1 })).toBe(false);
    expect(reloadOnceForStaleBuild(new Error('x'), { storage: memoryStorage(), reload, now: 1 })).toBe(false);
    expect(reload).not.toHaveBeenCalled();
  });
});
