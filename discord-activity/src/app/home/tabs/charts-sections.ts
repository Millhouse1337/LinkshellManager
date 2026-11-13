// The Charts sub-nav list, kept apart from ChartsTabComponent so ActivityHomeComponent can read
// it (to turn a URL into a board) without pulling the whole Charts tab into the startup bundle;
// the tab itself loads through @defer.

/** One entry in the Charts sub-nav. Unbuilt boards render disabled rather than hidden. */
export interface ChartsSectionOption {
  key: string;
  label: string;
  ready: boolean;
}

// Mirrors the server's ChartBoardCatalog for the live boards. Kept as a literal rather than
// fetched because the sub-nav has to render before any board loads; the server still validates
// the board on every call, so a stale entry here is a 404, never a wrong board.
export const CHARTS_SECTIONS: readonly ChartsSectionOption[] = [
  { key: 'Sky', label: 'Sky', ready: true },
  { key: 'Sea', label: 'Sea', ready: true },
  { key: 'Dynamis', label: 'Dynamis', ready: true },
  { key: 'Limbus', label: 'Limbus', ready: true },
  { key: 'HENM', label: 'HENM', ready: true }
];

// The board a URL segment names (/charts/sea -> 'Sea'), case-insensitive; Sky when it names none.
export function chartsSectionFromSlug(slug: string | null | undefined): string {
  const value = (slug ?? '').toLowerCase();
  return CHARTS_SECTIONS.find(section => section.key.toLowerCase() === value)?.key ?? CHARTS_SECTIONS[0].key;
}
