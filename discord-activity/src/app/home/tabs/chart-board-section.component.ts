import { CommonModule } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  HostListener,
  Injector,
  Input,
  afterNextRender,
  computed,
  inject,
  input,
  signal,
  viewChild
} from '@angular/core';
import { FormsModule } from '@angular/forms';

import { ChartBoardService } from '../../discord/chart-board.service';
import type {
  ActivityChartBoard,
  ActivityChartBoss,
  ActivityChartCreditInput,
  ActivityChartPopItem,
  ActivityChartPopItemOption,
  ChartItemKind
} from '../../discord/discord-activity.types';
import { ChartHoldingsSectionComponent } from './chart-holdings-section.component';
import { ChartKeyItemSectionComponent } from './chart-key-item-section.component';
import { ChartWishlistSectionComponent } from './chart-wishlist-section.component';

/** The parts of a board page a tab can show. */
export type ChartSection = 'cards' | 'holdings' | 'requests' | 'progress';

/** One tab in the Charts page header, and the parts of the board it shows. */
export interface ChartViewTab {
  key: string;
  label: string;
  sections: readonly ChartSection[];
}

/**
 * The tabs a board splits into, or none for a board that keeps one long page.
 * One function, read by the Charts page to draw the switch and to tell this component what to show,
 * so the two cannot disagree about which part sits under which tab.
 *
 * Chosen by board rather than inferred from features: each board's tabs are named for that board,
 * and two boards with the same features (Sea and Limbus both take drops) still want different ones.
 *
 * Keys are shared where the meaning is, so Sky Holdings -> Sea lands on Sea Holdings.
 */
export function chartViewTabs(board: ActivityChartBoard | null): ChartViewTab[] {
  switch (board?.board) {
    case 'Sky':
    case 'Sea':
      return [
        { key: 'items', label: 'Pop/Drop Items', sections: ['cards'] },
        { key: 'holdings', label: `${board.boardLabel} Holdings`, sections: ['holdings'] }
      ];
    // The zone cards each show their key item, so they are the Key Item tab; the old pop item
    // holdings stay under them, where they were.
    case 'Dynamis':
      return [
        { key: 'keyItem', label: 'Key Item', sections: ['cards', 'holdings'] },
        { key: 'requests', label: 'Item Request', sections: ['requests'] },
        { key: 'progress', label: 'Member Progress', sections: ['progress'] }
      ];
    case 'Limbus':
      return [
        { key: 'items', label: 'Drop Items', sections: ['cards'] },
        { key: 'holdings', label: 'Holdings', sections: ['holdings'] },
        { key: 'requests', label: 'Item Requests', sections: ['requests'] }
      ];
    // The tier ladder - each tier's pop item strip above its NMs - with the holdings table under it.
    case 'HENM':
      return [
        { key: 'items', label: 'Pop Item Holdings', sections: ['cards', 'holdings'] },
        { key: 'requests', label: 'Item Request', sections: ['requests'] }
      ];
    default:
      return [];
  }
}

/** One heading and the cards under it. Local, like ChartsSectionOption in charts-tab.component.ts. */
interface ChartBossGroup {
  label: string | null;
  bosses: ActivityChartBoss[];
}

/** One line of a boss card: an item name and how many of it the linkshell holds, all told. */
interface ChartConsolidatedItem {
  name: string;
  quantity: number;
  /** False for a catalog item nobody has a row for yet. It is still listed, at 0, so the card says
   *  what the boss takes and drops before anyone holds one. */
  tracked: boolean;
  /** The catalog option's source, shown beside the name — a HENM tier item's price. Null on a
   *  line no catalog option names. */
  note: string | null;
}

/** A card's lines of one kind, under that kind's own heading. */
interface ChartItemSection {
  kind: ChartItemKind;
  heading: string;
  items: ChartConsolidatedItem[];
  /** Whether this card takes adds of this kind - i.e. whether the section carries an add button. */
  takes: boolean;
}

/**
 * One Charts board: its boss cards, then the Farming Credit Ledger.
 *
 * Board-agnostic — Sky's five gods, Sea's eight Jailers and Dynamis's ten zones render through this
 * same component, and the only thing that differs is what the server sent. Every boss is one card in
 * one grid, whatever its `kind`; the kind only picks the chrome (a MiniNm headlines stock rather than
 * items, the Final encounter carries a collapsed reward list), never the width.
 *
 * Both halves come from ONE payload. The cards show a per-item farmer count and the ledger shows a
 * per-boss fraction; they are two views of the same rows, so fetching them separately would let one
 * screen contradict itself.
 */
@Component({
  selector: 'app-chart-board-section',
  imports: [
    CommonModule,
    FormsModule,
    ChartHoldingsSectionComponent,
    ChartWishlistSectionComponent,
    ChartKeyItemSectionComponent
  ],
  templateUrl: './chart-board-section.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class ChartBoardSectionComponent {
  protected readonly charts = inject(ChartBoardService);

  /** Guards the reload: Angular calls these setters on every parent CD pass, not only on change. */
  private loadedKey: string | null = null;

  @Input({ required: true }) set linkshellId(value: number | null) {
    this.selectedLinkshellId.set(value);
    this.loadIfNeeded();
  }

  @Input({ required: true }) set board(value: string) {
    this.boardKey.set(value);
    this.loadIfNeeded();
  }

  protected readonly selectedLinkshellId = signal<number | null>(null);
  protected readonly boardKey = signal<string>('');

  private loadIfNeeded(): void {
    const linkshellId = this.selectedLinkshellId();
    const board = this.boardKey();
    if (linkshellId === null || !board) {
      return;
    }
    const key = `${linkshellId}:${board}`;
    if (key === this.loadedKey) {
      return;
    }
    this.loadedKey = key;
    // Switching boards resets any open editor — they belong to rows that are about to be replaced.
    this.closeAll();
    void this.charts.load(linkshellId, board);
  }

  protected readonly chart = computed(() => this.charts.board(this.boardKey()));

  protected readonly bosses = computed<ActivityChartBoss[]>(() => this.chart()?.bosses ?? []);

  /**
   * The bosses split into contiguous RUNS of the same group label.
   *
   * Runs rather than a group-by: the catalog's order IS the display order, and grouping by key would
   * silently reorder cards to match the first sighting of each label. A board that declares no groups
   * collapses to ONE run with a null label, so Sky and Sea emit exactly the DOM they did before this
   * existed — no heading element, no wrapper, the same grid items.
   */
  protected readonly bossGroups = computed<ChartBossGroup[]>(() => {
    const runs: ChartBossGroup[] = [];
    for (const boss of this.bosses()) {
      const label = boss.group ?? null;
      const open = runs[runs.length - 1];
      if (open && open.label === label) {
        open.bosses.push(boss);
      } else {
        runs.push({ label, bosses: [boss] });
      }
    }
    return runs;
  });

  /**
   * Each boss's rows folded down to one line per ITEM, with the quantities summed.
   *
   * Three people each holding a Gem of the South are three rows in the data — that is the fact the
   * holdings table below is built on — but on a card one of five in a row, they were three lines
   * saying the same word. A card answers "what does this boss need and how many do we have"; who
   * holds which copy is a question the wide table answers.
   *
   * Split by KIND, pop items first: what is traded to a boss and what falls off it are different
   * questions, and Brigandish Blade answers both. Each section starts with the catalog's own items
   * for that kind, at 0 until a row says otherwise, then anything else officers have typed in.
   * A section is shown when the card TAKES that kind - even empty, because it carries the add
   * button - or when old rows of that kind are still on it. So Faust shows no Pop heading, and
   * Kirin shows an empty Drop one.
   *
   * Keyed by boss and memoised, so the template can ask per card without re-folding on every pass.
   * Catalog order comes first; after that, first-seen spelling and position win.
   */
  protected readonly consolidatedItems = computed<Record<string, ChartItemSection[]>>(() => {
    const byBoss: Record<string, ChartItemSection[]> = {};

    for (const boss of this.bosses()) {
      byBoss[boss.boss] = (['Pop', 'Drop'] as const)
        .map(kind => {
          const order: ChartConsolidatedItem[] = [];
          const seen = new Map<string, ChartConsolidatedItem>();

          const options = (kind === 'Drop' ? boss.dropItemOptions : boss.popItemOptions) ?? [];
          for (const option of options) {
            const line: ChartConsolidatedItem = {
              name: option.name, quantity: 0, tracked: false, note: option.source?.trim() || null
            };
            seen.set(option.name.trim().toLowerCase(), line);
            order.push(line);
          }

          for (const item of boss.items.filter(row => (row.kind ?? 'Pop') === kind)) {
            const key = item.itemName.trim().toLowerCase();
            const found = seen.get(key);
            if (found) {
              found.quantity += item.quantity;
              found.tracked = true;
            } else {
              const line: ChartConsolidatedItem = {
                name: item.itemName, quantity: item.quantity, tracked: true, note: null
              };
              seen.set(key, line);
              order.push(line);
            }
          }

          return {
            kind,
            heading: kind === 'Drop' ? 'Drop Item' : 'Pop Item',
            items: order,
            takes: kind === 'Drop' ? boss.takesDropItems : boss.takesPopItems
          };
        })
        .filter(section => section.takes || section.items.length > 0);
    }

    return byBoss;
  });

  protected sectionsFor(boss: string): ChartItemSection[] {
    return this.consolidatedItems()[boss] ?? [];
  }

  /**
   * Whether a card shows an item table at all: it takes adds of some kind, or old rows still sit on
   * it. A HENM NM (its tier's strip holds the pop items) and a Dynamis zone show none.
   */
  protected tracksItems(boss: ActivityChartBoss): boolean {
    return boss.takesPopItems || boss.takesDropItems || boss.items.length > 0;
  }

  /** Distinct items with at least one row. A catalog line still at 0 lists nothing anyone holds. */
  protected trackedItemCount(boss: string): number {
    return this.sectionsFor(boss)
      .reduce((count, section) => count + section.items.filter(item => item.tracked).length, 0);
  }

  /** Group labels drawn as columns rather than rows. Empty for every board but Sky. */
  protected readonly pathColumns = computed<string[]>(() => this.chart()?.pathColumns ?? []);

  /**
   * The runs drawn as COLUMNS, in the order the board names them — not in run order, so a column's
   * position on the page is decided by the catalog rather than by where its first card happens to
   * sit. A label naming no run is skipped, which is a missing column rather than a crash.
   */
  protected readonly pathGroups = computed<ChartBossGroup[]>(() => {
    const runs = this.bossGroups();
    return this.pathColumns()
      .map(label => runs.find(run => run.label === label))
      .filter((run): run is ChartBossGroup => run !== undefined);
  });

  /** Everything else, in run order — Kirin's run on Sky, the whole board everywhere else. */
  protected readonly trailingGroups = computed<ChartBossGroup[]>(() => {
    const columns = this.pathColumns();
    return this.bossGroups().filter(run => run.label === null || !columns.includes(run.label));
  });

  /**
   * Rows every path column shares: one heading plus the cards in the longest path. Set as a CSS
   * variable so the grid can name its rows explicitly — that is what pins tier 1 of one column
   * against tier 1 of the next, rather than letting each column flow to its own height.
   */
  protected readonly pathRowCount = computed(
    () => 1 + this.pathGroups().reduce((most, run) => Math.max(most, run.bosses.length), 0)
  );

  /**
   * Which container the runs below the columns go in — and, on a board with no columns, the whole
   * board. Three shapes, and a board is exactly one of them:
   *
   *   chart-finale — sits under path columns and is sized in their widths (Sky, Sea)
   *   chart-rows   — centred rows the board chose the length of (Dynamis)
   *   chart-grid   — the plain stretch-to-fit grid everything else has always used
   */
  protected readonly trailingContainerClass = computed(() => {
    if (this.pathGroups().length) {
      return 'chart-finale';
    }
    return this.chart()?.centersRows ? 'chart-rows' : 'chart-grid';
  });


  protected readonly roster = computed(() => this.chart()?.roster ?? []);

  /**
   * Whether this member may edit — read off the payload, which is the server's own answer.
   *
   * Deliberately NOT derived from the membership's permission flags the way the older Items section
   * does it: that second copy of the rule is what drifted last time and showed officers a form the
   * server then rejected.
   */
  protected readonly canManage = computed(() => this.chart()?.canManage === true);

  protected readonly isEmpty = computed(() => this.bosses().every(boss => boss.totalItems === 0));

  // ---- add / edit form ----

  protected readonly features = computed(() => this.chart()?.features ?? {
    popItems: true, dropItems: false, wishlist: false, keyItems: false
  });

  // ---- tabs ----

  /**
   * The parts of the board the selected tab shows. The switch itself lives in the Charts page
   * header (ChartsTabComponent, via chartViewTabs); null means this board has no tabs, so every
   * part shows. The template HIDES the other parts rather than unmounting them, so an expanded
   * farmer list or a half-typed request survives a switch.
   */
  readonly visibleSections = input<readonly ChartSection[] | null>(null);

  protected shows(section: ChartSection): boolean {
    const visible = this.visibleSections();
    return visible === null || visible.includes(section);
  }

  private readonly injector = inject(Injector);
  private readonly formCard = viewChild<ElementRef<HTMLElement>>('formCard');

  protected readonly showForm = signal(false);
  protected readonly editingItemId = signal<number | null>(null);
  /**
   * Which kind the open form is adding. Each card's own add button opens THIS form pre-set to that
   * card and kind, rather than a form per card: the form has six fields, and squeezed into a card
   * one of four across it would wrap every label.
   *
   * On an edit it is the row's own kind, and the server ignores it there anyway - a row moves
   * between bosses, never between kinds.
   */
  protected readonly formKind = signal<ChartItemKind>('Pop');
  // Signals rather than plain fields, unlike the three below them: the pop item picker is derived
  // from which boss is selected and from what is already in the box, so both have to be readable by
  // a computed. Bound one-way with an explicit (ngModelChange) so the write is visible.
  protected readonly formBoss = signal('');
  protected readonly formItemName = signal('');
  /** A signal too: the holder dropdown filters itself against whatever is typed. */
  protected readonly formHolder = signal('');
  protected formQuantity = 1;
  protected formNotes = '';

  private defaultBoss(): string {
    return this.bosses()[0]?.boss ?? '';
  }

  /**
   * The Boss dropdown: only cards that take the kind being added, so switching boss cannot land a
   * pop item on Faust. The row's own boss always stays, so editing an old row never re-points it.
   */
  protected readonly formBossChoices = computed<ActivityChartBoss[]>(() => {
    const kind = this.formKind();
    const current = this.formBoss();
    return this.bosses().filter(boss =>
      boss.boss === current || (kind === 'Drop' ? boss.takesDropItems : boss.takesPopItems));
  });

  /**
   * The pop items the selected boss takes, straight off the payload — the server's catalog is the
   * only list, so this client never carries its own copy of what pops what.
   *
   * Empty for every board that does not spell its items out, which is every board but Sky today;
   * the form falls back to a free-text box for those.
   */
  protected readonly popItemOptions = computed<ActivityChartPopItemOption[]>(() => {
    const selected = this.formBoss();
    const card = this.bosses().find(boss => boss.boss === selected);
    // Which list depends on what the form is adding: what is traded TO a boss and what falls OFF it
    // are different questions with different answers, and they were sharing one box.
    return (this.formKind() === 'Drop' ? card?.dropItemOptions : card?.popItemOptions) ?? [];
  });

  /** "Pop item" or "Drop item" - the form's own label, so the template holds no branch. */
  protected readonly itemLabel = computed(() =>
    this.formKind() === 'Drop' ? 'Drop item' : 'Pop item');

  protected readonly picksPopItem = computed(() => this.popItemOptions().length > 0);

  /**
   * What the picker lists: the catalog's items, plus — at the top — the name this row is ALREADY
   * saved under when it is not one of them. Without that entry, opening Edit on a row typed before
   * the board had a list would silently re-point it at whatever the dropdown happened to show first.
   */
  protected readonly popItemChoices = computed<ActivityChartPopItemOption[]>(() => {
    const options = this.popItemOptions();
    const current = this.formItemName().trim();
    if (!current || options.some(option => this.sameItem(option.name, current))) {
      return options;
    }
    // Labelled here rather than server-side, unlike every catalog option: "this row already says
    // this" is a fact about the row being edited, not about the board.
    return [{ name: current, label: `${current} — already on this row` }, ...options];
  });

  private sameItem(left: string, right: string): boolean {
    return left.trim().toLowerCase() === right.trim().toLowerCase();
  }

  /**
   * A pop item belongs to ONE boss, so a name picked for the previous one is not an answer for the
   * new one — it is cleared rather than carried over, and the submit button stays disabled until
   * something is picked.
   */
  protected onBossChange(boss: string): void {
    this.formBoss.set(boss);
    const options = this.popItemOptions();
    if (options.length && !options.some(option => this.sameItem(option.name, this.formItemName()))) {
      this.formItemName.set('');
    }
  }

  // ---- who is holding it ----
  //
  // A dropdown of our own rather than a <datalist>: the browser decides how tall a datalist popup is
  // and no CSS reaches it, and a roster of mains plus two alts each ran off the bottom of the screen.
  // This one is capped at twelve rows and scrolls. It is still a free-text box underneath — an
  // unsynced character is a real member who can really be holding a pop item.

  protected readonly holderOpen = signal(false);

  /**
   * Every character a pop item could sit on: each member, then their own alts under them.
   *
   * Alts by their REAL names, from the same AppUser.AltCharacterName1/2 the profile registers — a
   * gem on somebody's alt sits there under the alt's name, and that is what an officer looks for.
   * `altOf` is the hint beside it, so it is obvious whose it is.
   */
  protected readonly holderChoices = computed<{ name: string; altOf: string | null }[]>(() =>
    this.roster().flatMap(member => [
      { name: member.characterName, altOf: null },
      ...(member.altCharacterNames ?? []).map(alt => ({ name: alt, altOf: member.characterName }))
    ])
  );

  /**
   * Filtered by whatever is typed, matching anywhere in the name — typing a MAIN's name still finds
   * their alts, because the hint carries it. An exact match shows the whole list again rather than
   * one row: having picked somebody, the useful next move is picking somebody else.
   */
  protected readonly holderMatches = computed(() => {
    const typed = this.formHolder().trim().toLowerCase();
    const choices = this.holderChoices();
    if (!typed || choices.some(choice => choice.name.toLowerCase() === typed)) {
      return choices;
    }
    return choices.filter(
      choice =>
        choice.name.toLowerCase().includes(typed) ||
        (choice.altOf?.toLowerCase().includes(typed) ?? false)
    );
  });

  protected pickHolder(name: string): void {
    this.formHolder.set(name);
    this.holderOpen.set(false);
  }

  // ---- farming credit, picked while the row is written ----
  //
  // The same set of names the per-row Credits drawer edits, offered up front so crediting a farm
  // night is one action instead of add-then-reopen-the-row. Both write the COMPLETE list, so this is
  // a second door onto one fact rather than a second place credit is stored.

  /** Ticked names, lower-cased so a roster entry and a stored credit match. */
  protected readonly formCredits = signal<Set<string>>(new Set());
  protected readonly formCreditsOpen = signal(false);

  protected readonly formCreditNames = computed(() =>
    this.roster()
      .filter(member => this.formCredits().has(member.characterName.trim().toLowerCase()))
      .map(member => member.characterName)
  );

  /** What the closed dropdown says, so an officer can see the answer without opening it. */
  protected readonly formCreditsSummary = computed(() => {
    const names = this.formCreditNames();
    if (names.length === 0) {
      return 'Nobody credited';
    }
    if (names.length === 1) {
      return names[0];
    }
    return names.length === this.roster().length
      ? `Everyone (${names.length})`
      : `${names.length} farmers`;
  });

  protected toggleFormCredits(): void {
    this.formCreditsOpen.update(open => !open);
  }

  protected isFormCredited(characterName: string): boolean {
    return this.formCredits().has(characterName.trim().toLowerCase());
  }

  protected toggleFormCredit(characterName: string): void {
    const key = characterName.trim().toLowerCase();
    this.formCredits.update(current => {
      const next = new Set(current);
      if (!next.delete(key)) {
        next.add(key);
      }
      return next;
    });
  }

  protected creditEveryoneOnForm(): void {
    this.formCredits.set(
      new Set(this.roster().map(member => member.characterName.trim().toLowerCase()))
    );
  }

  protected clearFormCredits(): void {
    this.formCredits.set(new Set());
  }

  private formCreditInputs(): ActivityChartCreditInput[] {
    return this.roster()
      .filter(member => this.isFormCredited(member.characterName))
      .map(member => ({ membershipId: member.membershipId, characterName: null }));
  }

  /**
   * Closes either open panel on a click outside it. Matched on the wrapper class rather than an
   * ElementRef so the control's own click — which bubbles to the document too — does not close what
   * it just opened.
   */
  @HostListener('document:click', ['$event'])
  protected closePanelsOnOutsideClick(event: Event): void {
    const target = event.target as HTMLElement | null;
    if (this.formCreditsOpen() && !target?.closest('.chart-multiselect')) {
      this.formCreditsOpen.set(false);
    }
    if (this.holderOpen() && !target?.closest('.chart-combo')) {
      this.holderOpen.set(false);
    }
  }

  /** Whether the open form is a fresh add for exactly this card and kind - its button then reads Close. */
  protected isAddingTo(boss: string, kind: ChartItemKind): boolean {
    return this.showForm() && this.editingItemId() === null
      && this.formKind() === kind && this.formBoss() === boss;
  }

  /**
   * A card's add button: opens the form on that card and kind, or closes it if the same button is
   * pressed again. Any other card's button re-opens it for that card instead.
   *
   * The form sits at the top of the board, which can be well above the card clicked, so it is
   * scrolled into view once it has rendered.
   */
  protected toggleFormFor(boss: string, kind: ChartItemKind): void {
    const closing = this.isAddingTo(boss, kind);
    this.resetForm();
    if (closing) {
      this.showForm.set(false);
      return;
    }
    this.formKind.set(kind);
    this.formBoss.set(boss);
    this.showForm.set(true);
    this.revealForm();
  }

  /** afterNextRender for the reason the event queue panel gives: a zero-delay timer can beat the render. */
  private revealForm(): void {
    afterNextRender(() => {
      this.formCard()?.nativeElement.scrollIntoView({ behavior: 'smooth', block: 'start' });
    }, { injector: this.injector });
  }

  protected beginEdit(item: ActivityChartPopItem): void {
    this.editingItemId.set(item.id);
    // The ROW's kind, so the picker offers the list it was created from. Set before formBoss, which
    // popItemOptions reads through.
    this.formKind.set(item.kind ?? 'Pop');
    this.formBoss.set(item.boss);
    this.formItemName.set(item.itemName);
    this.formHolder.set(item.heldByCharacterName ?? '');
    this.formQuantity = item.quantity;
    this.formNotes = item.notes ?? '';
    // Preloaded with who is already credited, because the form SUBMITS this list set-wise: opening
    // Edit to fix a quantity and saving would otherwise clear every farmer on the row.
    this.formCredits.set(new Set(item.credits.map(credit => credit.characterName.trim().toLowerCase())));
    this.formCreditsOpen.set(false);
    this.showForm.set(true);
    // Edit is pressed in the holdings table, below every card - the form is far off screen.
    this.revealForm();
  }

  protected resetForm(): void {
    this.editingItemId.set(null);
    this.formKind.set('Pop');
    this.formBoss.set(this.defaultBoss());
    this.formItemName.set('');
    this.formHolder.set('');
    this.formQuantity = 1;
    this.formNotes = '';
    this.formCredits.set(new Set());
    this.formCreditsOpen.set(false);
    this.holderOpen.set(false);
  }

  protected cancelForm(): void {
    this.resetForm();
    this.showForm.set(false);
  }

  protected async submitForm(): Promise<void> {
    const linkshellId = this.selectedLinkshellId();
    if (linkshellId === null || !this.formItemName().trim()) {
      return;
    }

    const input = {
      boss: this.formBoss() || this.defaultBoss(),
      itemName: this.formItemName().trim(),
      // The holder is a plain name: an unsynced character is a real member of the linkshell who can
      // really be holding a pop item, they just have no account behind the name.
      heldByCharacterName: this.formHolder().trim() || null,
      quantity: Number.isFinite(this.formQuantity) ? Math.max(0, this.formQuantity) : 0,
      notes: this.formNotes.trim() || null,
      // Membership ids rather than names, like the row editor: the server re-reads the name off the
      // roster, so a rename cannot orphan the credit. Always sent, including empty — on an edit this
      // list is the row's own credits preloaded, so sending it is what makes unticking one stick.
      credits: this.formCreditInputs(),
      // Honoured on add; ignored on update, where the row's own kind wins server-side.
      kind: this.formKind()
    };

    const editingId = this.editingItemId();
    try {
      if (editingId !== null) {
        await this.charts.updateItem(editingId, input);
      } else {
        await this.charts.addItem(linkshellId, this.boardKey(), input);
      }
      this.cancelForm();
    } catch {
      // Surfaced by the service through the shared action banner.
    }
  }

  // Removing a row, and editing the farmers on one, both live in the holdings table below — that is
  // where a row exists as itself. This component owns the FORM, so it keeps only what the form does.

  // ---- final encounter's reward list ----

  /**
   * Which finale has its rewards open, by boss name; null for none. Collapsed by default so a
   * finale card is the same height as the boss cards beside it.
   *
   * Keyed rather than a single boolean because a board can have more than one finale — Limbus ends
   * both Apollyon and Temenos — and one shared flag opened and closed all of them together.
   */
  protected readonly rewardsOpenBoss = signal<string | null>(null);

  protected toggleRewards(boss: string): void {
    this.rewardsOpenBoss.update(open => (open === boss ? null : boss));
  }

  // ---- a card's "I Have It" button ----

  /** The viewer's own membership, or null for somebody with none - they get no button. */
  protected readonly viewerMembershipId = computed(() => this.chart()?.viewerMembershipId ?? null);

  /** Whether the viewer's own row in the key item grid has this one ticked. */
  protected viewerHasKeyItem(keyItemName: string): boolean {
    const grid = this.chart()?.keyItems;
    const viewer = this.viewerMembershipId();
    const column = grid?.columns.findIndex(item => item.name === keyItemName) ?? -1;
    const row = grid?.rows.find(member => member.membershipId === viewer);
    return column >= 0 && row?.has[column] === true;
  }

  /**
   * Ticks or clears the viewer's OWN cell - the same write the Member Progress grid makes, and the
   * server applies the same self-serve rule to it.
   */
  protected async setOwnKeyItem(keyItemName: string, has: boolean): Promise<void> {
    const linkshellId = this.selectedLinkshellId();
    const membershipId = this.viewerMembershipId();
    if (linkshellId === null || membershipId === null) {
      return;
    }
    try {
      await this.charts.setKeyItem(linkshellId, this.boardKey(), { keyItemName, membershipId, has });
    } catch {
      // Surfaced by the service through the shared action banner.
    }
  }

  private closeAll(): void {
    this.cancelForm();
    this.rewardsOpenBoss.set(null);
  }
}
