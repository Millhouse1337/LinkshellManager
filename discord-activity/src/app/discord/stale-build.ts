// Recovery for a page left open across a deploy.
//
// Each build names its script files by content hash. A page loaded before a deploy still knows the
// OLD names, and the first time it lazily loads one (the home screen, the Discord SDK) the server
// answers 404, because that file no longer exists. Reloading picks up the new index.html, which
// names the new files. Only once in a short while, so a real outage cannot cause a reload loop.

const STORAGE_KEY = 'lsm-stale-build-reload-at';
const RETRY_AFTER_MS = 60_000;

// Angular's code for "an @defer block's script failed to load". Each tab sits in an @defer block,
// and Angular drops the browser's own message and reports only this code, so it counts too.
const DEFER_LOADING_FAILED = -750;

// The messages browsers use when a dynamically imported script fails to load.
export function isChunkLoadError(error: unknown): boolean {
  if ((error as { code?: unknown } | null)?.code === DEFER_LOADING_FAILED) {
    return true;
  }
  const message = error instanceof Error ? error.message : typeof error === 'string' ? error : '';
  return /Failed to fetch dynamically imported module|error loading dynamically imported module|Importing a module script failed|Loading chunk [\w-]+ failed|^NG0750\b/i
    .test(message);
}

export interface StaleBuildDeps {
  storage: Pick<Storage, 'getItem' | 'setItem'> | null;
  reload: () => void;
  now: number;
}

// Reloads the page (and returns true) when `error` is a failed chunk load and no reload for that
// reason happened in the last minute. Otherwise does nothing and returns false.
export function reloadOnceForStaleBuild(error: unknown, deps: StaleBuildDeps = defaultDeps()): boolean {
  // Storage blocked (private mode, sandboxed iframe): without a way to remember the reload, do not
  // risk a loop. The error is shown instead.
  if (!isChunkLoadError(error) || !deps.storage) {
    return false;
  }
  try {
    const last = Number(deps.storage.getItem(STORAGE_KEY) ?? 0);
    if (last && deps.now - last < RETRY_AFTER_MS) {
      return false;
    }
    deps.storage.setItem(STORAGE_KEY, String(deps.now));
  } catch {
    return false;
  }
  deps.reload();
  return true;
}

function defaultDeps(): StaleBuildDeps {
  let storage: Storage | null = null;
  try {
    storage = window.sessionStorage;
  } catch {
    storage = null;
  }
  return { storage, reload: () => window.location.reload(), now: Date.now() };
}
