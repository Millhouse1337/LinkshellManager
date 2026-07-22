// Presentation helpers for the dashboard Rules / Announcements cards — mirrors the
// web Utils/RuleContent.cs (keep the category list, badge mapping, and bullet parsing
// in sync across the two). Turns free-text details into paragraph/bullet blocks and
// maps an optional category to an accent class + icon.

// Preset category choices for the dropdown (plus a free-text "Other"). Mirrors
// RuleContent.CategoryOptions (C#).
export const RULE_CATEGORY_OPTIONS: readonly string[] = [
  'Overview', 'Focus', 'Conduct', 'Standards', 'DKP', 'Attendance',
  'Loot', 'Policy', 'Event', 'Update', 'Reminder', 'General'
];

export interface RuleBlock {
  isList: boolean;
  text: string;
  items: string[];
}

const BULLET_MARKERS = ['-', '•', '*', '·', '–', '—', '▪', '●', '◦'];

// Splits free-text details into blocks: runs of bullet lines become one list; other
// non-blank lines become paragraphs. Mirrors RuleContent.Parse.
export function parseRuleDetails(details: string | null | undefined): RuleBlock[] {
  const blocks: RuleBlock[] = [];
  if (!details || !details.trim()) { return blocks; }

  const lines = details.replace(/\r\n/g, '\n').replace(/\r/g, '\n').split('\n');
  let paragraph: string[] = [];
  let bullets: string[] = [];

  const flushParagraph = () => {
    if (paragraph.length > 0) { blocks.push({ isList: false, text: paragraph.join(' '), items: [] }); paragraph = []; }
  };
  const flushBullets = () => {
    if (bullets.length > 0) { blocks.push({ isList: true, text: '', items: bullets.slice() }); bullets = []; }
  };

  for (const raw of lines) {
    const line = raw.trim();
    if (line.length === 0) { flushParagraph(); continue; }
    if (line.length > 1 && BULLET_MARKERS.indexOf(line[0]) >= 0 && /\s/.test(line[1])) {
      flushParagraph();
      bullets.push(line.slice(1).trim());
    } else {
      flushBullets();
      paragraph.push(line);
    }
  }
  flushParagraph();
  flushBullets();
  return blocks;
}

const PALETTE = ['acc-blue', 'acc-purple', 'acc-green', 'acc-gold', 'acc-cyan', 'acc-orange'];

// Accent CSS class + icon glyph for a card. Mirrors RuleContent.Badge.
export function categoryBadge(category: string | null | undefined, index: number): { accent: string; icon: string } {
  const key = (category ?? '').trim().toLowerCase();
  if (key.length > 0) {
    if (key.includes('dkp')) { return { accent: 'acc-blue', icon: '🪙' }; }       // 🪙
    if (key.includes('loot')) { return { accent: 'acc-orange', icon: '📦' }; }     // 📦
    if (key.includes('conduct')) { return { accent: 'acc-green', icon: '🤝' }; }    // 🤝
    if (key.includes('standard') || key.includes('community')) { return { accent: 'acc-gold', icon: '⚖️' }; } // ⚖️
    if (key.includes('attend')) { return { accent: 'acc-cyan', icon: '👥' }; }      // 👥
    if (key.includes('focus')) { return { accent: 'acc-purple', icon: '🎯' }; }     // 🎯
    if (key.includes('policy') || key.includes('trial')) { return { accent: 'acc-purple', icon: '📜' }; } // 📜
    if (key.includes('event')) { return { accent: 'acc-cyan', icon: '🗓️' }; } // 🗓️
    if (key.includes('overview') || key.includes('rule') || key.includes('system')) { return { accent: 'acc-blue', icon: '🛡️' }; } // 🛡️
  }
  const i = ((index % PALETTE.length) + PALETTE.length) % PALETTE.length;
  return { accent: PALETTE[i], icon: '✦' }; // ✦
}
