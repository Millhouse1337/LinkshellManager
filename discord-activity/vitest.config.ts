import { defineConfig } from 'vitest/config';

// Unit tests for the app's PURE helper functions (npm test). They run in plain Node: no browser,
// no Angular TestBed, no components. `ng build` never sees these files (tsconfig.app.json
// excludes *.spec.ts).
//
// The computer's time zone is pinned to New York on purpose. The app reads times in the
// viewer's PROFILE zone, and the bugs worth catching are the ones where code quietly uses the
// computer's zone instead, so the tests use profile zones (Tokyo, London, ...) that differ from
// it. Vitest sets this in each test worker before any test runs, on every machine and in CI.
export default defineConfig({
  test: {
    include: ['src/**/*.spec.ts'],
    environment: 'node',
    env: { TZ: 'America/New_York' }
  }
});
