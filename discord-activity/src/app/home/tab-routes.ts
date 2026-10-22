import { TAB_NAMES, type TabName } from './activity-home.types';

// One web address per tab (under the app's /discord-activity/ base), so a reload stays on the
// same tab, Back works on the website, and old website URLs can redirect straight to a tab
// (Utils/LegacyUrlRedirects.cs keeps its targets in step with these slugs). The dashboard is
// the bare app URL; a few tabs read better under a different name than their internal key.
const SLUG_BY_TAB: Partial<Record<TabName, string>> = {
  dashboard: '',
  'timed-events': 'events',
  endgame: 'charts',
  linkshell: 'management'
};

// 'dkp-sheet' is a leftover key: the sheet is a view INSIDE the DKP tab (dkp/sheet) and has no
// panel of its own, so it never gets an address.
const ROUTABLE_TABS = TAB_NAMES.filter(tab => tab !== 'dkp-sheet');

export function slugForTab(tab: TabName): string {
  return SLUG_BY_TAB[tab] ?? tab;
}

// The tab a URL's first segment names, or null for an unknown name.
export function tabFromSlug(slug: string | null | undefined): TabName | null {
  const value = (slug ?? '').trim().toLowerCase();
  return ROUTABLE_TABS.find(tab => slugForTab(tab) === value) ?? null;
}
