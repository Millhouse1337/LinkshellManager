import { CommonModule } from '@angular/common';
import {
  ChangeDetectionStrategy, Component, ElementRef, Input,
  computed, effect, inject, signal, viewChild
} from '@angular/core';
import { FormsModule } from '@angular/forms';

import { DiscordActivityService } from '../../discord/discord-activity.service';
import { TreasuryService } from '../../discord/treasury.service';
import type {
  ActivityTreasuryEntry,
  ActivityTreasuryFilter,
  ActivityTreasuryKind,
  ActivityTreasuryMember,
  ActivityTreasuryMemberObligation
} from '../../discord/discord-activity.types';

/**
 * Which half of the balance sheet a tick belongs to. Both panels run through the same methods, and
 * this is the only thing that differs between them.
 */
type SettleSide = 'weOwe' | 'owedToUs';

/**
 * Management → Finances → Treasury.
 *
 * Extracted out of LinkshellTabComponent, which was a 993-line component covering four unrelated
 * domains; roughly a third of it was this.
 *
 * The design rule here: the structure underneath is real double-entry — every transaction has two
 * balanced halves, nothing is ever deleted, and the balance is derived — and none of that vocabulary
 * appears on screen. An officer picks one plain-English sentence from "What happened?" and the server
 * builds both halves. The words "debit" and "credit" appear in exactly one place in the whole product:
 * the collapsed "Show the bookkeeping details" panel.
 */
@Component({
  selector: 'app-finances-section',
  imports: [CommonModule, FormsModule],
  templateUrl: './finances-section.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class FinancesSectionComponent {
  /** Enough of a long roster to pick from without the list becoming the whole panel. */
  private static readonly MAX_MEMBER_RESULTS = 50;

  protected readonly activity = inject(DiscordActivityService);
  protected readonly treasury = inject(TreasuryService);

  /** Which linkshell's treasury to show. Owned by the parent tab's linkshell picker. */
  @Input({ required: true }) set linkshellId(value: number | null) {
    this.selectedLinkshellId.set(value);
  }

  protected readonly selectedLinkshellId = signal<number | null>(null);

  protected readonly search = signal('');
  protected readonly filter = signal<ActivityTreasuryFilter>('all');
  protected readonly pageIndex = signal(0);

  // Form state. `editingId` null means a new entry; otherwise a draft being edited.
  protected readonly showForm = signal(false);
  protected readonly editingId = signal<number | null>(null);
  protected readonly fixingEntry = signal<ActivityTreasuryEntry | null>(null);
  // A signal, not a plain ngModel field: everything derived from the chosen option — the help text,
  // the preview, whether to show a member box or the who-gets-a-share list — reads it through a
  // computed, and a computed only re-runs when a SIGNAL it read changes. As a plain field it cached
  // the first option forever, so picking anything else left the rest of the form on "Sold an item".
  protected readonly transactionKind = signal('');
  // Which of the four things is being recorded. Drives the reason list, so it is a signal for the
  // same reason transactionKind is.
  protected readonly action = signal('');
  protected amount = 0;
  protected transactionDate = '';
  // A signal, not a plain field: the amount prefills from whoever is picked, so everything derived
  // from the chosen member has to actually re-run.
  protected readonly member = signal('');
  protected readonly memberPickerOpen = signal(false);
  /**
   * Whose mule the gil lands on, or comes off.
   *
   * A signal so the blocker and the holder picker's filtering re-run as it is typed — the same
   * reason `member` is one. Separate from `member` on purpose: on a Gil Out to a member the two are
   * genuinely different people, since the gil comes off the treasurer's mule and goes to the member.
   */
  protected readonly holder = signal('');
  protected readonly holderPickerOpen = signal(false);
  protected note = '';
  protected reason = '';

  // Splitting one amount between several members. `pickedIds` holds membership rows rather than
  // names, because that is what the server can check against this linkshell's roster.
  protected readonly memberFilter = signal('');
  protected readonly pickedIds = signal<ReadonlySet<number>>(new Set());
  /** Names on the entry being fixed whose members have since left. Blocks submit rather than
      quietly recording a smaller payout than the one being replaced. */
  protected readonly unresolvedMembers = signal<readonly string[]>([]);

  // Paying members off the "who we owe" list. Ticking SELECTS; the button records. Splitting it in
  // two is what makes a misclick harmless — a payout takes real gil out of the treasury and the only
  // way back is a reversal on the books — and it lets an officer tick three people before committing,
  // which is how a payout run actually happens.
  //
  // Keyed on the lowercased character name because that is what the obligations themselves are keyed
  // on: TreasuryBalanceService.ProjectByMember groups what-we-owe lines by name, so a name IS the
  // identity of a debt here. There is no id to hold instead.
  protected readonly pickedOwed = signal<ReadonlySet<string>>(new Set());
  /** The same, for the mirror panel: who has now paid the LINKSHELL. Held apart from pickedOwed so
      a tick on one side of the sheet can never be read as a tick on the other. */
  protected readonly pickedOwedToUs = signal<ReadonlySet<string>>(new Set());
  /**
   * Two-step confirm: Discord's iframe suppresses window.confirm(), so the button does the asking.
   *
   * Which SIDE is armed, not merely whether one is. Both panels are on screen at once, and a shared
   * boolean would arm the other one's button too — one press away from settling the wrong list.
   */
  protected readonly settleConfirming = signal<SettleSide | null>(null);
  /**
   * Whose mule a payout run comes out of, or lands on. One per side, because both panels are on
   * screen at once and the two answers are genuinely different people.
   *
   * ONE name for the whole run rather than one per tick: a payout is somebody sitting at a mule
   * handing gil out, and asking again for each name on the list would ask the same question eight
   * times. The server refuses the batch without it.
   */
  protected settleHolderWeOwe = '';
  protected settleHolderOwedToUs = '';

  // Cancelling an entry that is already on the books, and throwing away one that is not. Both used
  // to call into window.confirm/prompt, which the Discord iframe suppresses — see beginReverse.
  //
  // The reversal reason is a plain field rather than a signal: nothing is derived from it, it is
  // read once on submit, and [(ngModel)] keeps it current.
  protected readonly reversingEntry = signal<ActivityTreasuryEntry | null>(null);
  protected reverseReason = '';
  /** The draft whose Discard button is armed. One at a time, so a stray press cannot chain. */
  protected readonly discardingId = signal<number | null>(null);

  /**
   * The chip row. Deliberately short: five buttons an officer actually reaches for.
   *
   * Drafts went because a draft already wears a "Draft" tag in the list and there are rarely more
   * than one or two. Uncategorized went with the categories themselves — every hand-recorded entry
   * lands in the catch-all now, on purpose, so a filter for "the ones in the catch-all" would just
   * be "all of them". Fixed arrived because every corrected typo used to land in Reversed and bury
   * the entries someone genuinely called off.
   */
  protected readonly filters: { key: ActivityTreasuryFilter; label: string }[] = [
    { key: 'all', label: 'All' },
    { key: 'in', label: 'Gil in' },
    { key: 'out', label: 'Gil out' },
    { key: 'fixed', label: 'Fixed' },
    { key: 'reversed', label: 'Reversed' }
  ];



  constructor() {
    // Reload whenever the linkshell, page, search or filter changes. The list is paged and filtered
    // server-side, so none of this is done in the browser.
    effect(() => {
      const linkshellId = this.selectedLinkshellId();
      if (linkshellId === null) {
        return;
      }
      void this.treasury.load(linkshellId, this.pageIndex(), this.search(), this.filter());
    });

    // A different linkshell is a different set of debts. Ticks are dropped rather than carried
    // across, because they are held by name and two linkshells can have a member with the same one.
    effect(() => {
      this.selectedLinkshellId();
      this.pickedOwed.set(new Set());
      this.pickedOwedToUs.set(new Set());
      this.settleConfirming.set(null);
    });
  }

  protected readonly page = computed(() => this.treasury.page());
  protected readonly summary = computed(() => this.page()?.summary ?? null);
  protected readonly entries = computed(() => this.page()?.entries ?? []);
  protected readonly kinds = computed(() => this.page()?.kinds ?? []);
  protected readonly canManage = computed(() => this.page()?.canManage ?? false);

  protected readonly pageCount = computed(() => {
    const page = this.page();
    if (!page || page.pageSize <= 0) {
      return 1;
    }
    return Math.max(1, Math.ceil(page.totalEntries / page.pageSize));
  });

  /**
   * The picker's top level, from the server. The labels used to be a hardcoded map here AND in two
   * Razor views, which is three places one heading could drift.
   *
   * Setup is dropped: its two members are a once-in-a-linkshell's-life action and one the app does
   * for you, so they get their own buttons above the form rather than a slot in the picker.
   */
  protected readonly actions = computed(() =>
    (this.page()?.actions ?? []).filter(
      action => action.key !== 'Setup' && this.reasonsFor(action.key).length > 0
    )
  );

  /**
   * The kinds on offer under one action. Retired and app-recorded kinds never appear.
   *
   * Every action has exactly one now, so this is really "the kind the action means": it is what
   * setAction() resolves a pressed button to, and what decides whether a button is offered at all.
   * There is no reason SELECT any more, and no showReasonSelect() guarding one — picking the action
   * IS picking the kind, and NoPickableActionAsksASecondQuestion pins that it stays that way.
   */
  protected reasonsFor(actionKey: string): ActivityTreasuryKind[] {
    return this.kinds().filter(kind => kind.action === actionKey && kind.isPickable);
  }

  /**
   * Resolved against EVERY kind, pickable or not.
   *
   * Filtering to the pickable ones here is what used to make Fix on an app-recorded entry appear
   * broken: this returned null, and `submit()` guards on it, so the button did nothing and said
   * nothing.
   */
  protected readonly selectedKind = computed(() =>
    this.kinds().find(kind => kind.key === this.transactionKind()) ?? null
  );

  /** Shares one amount between several members — show the picker instead of the single name box. */
  protected readonly isSplittable = computed(() => this.selectedKind()?.isSplittable === true);

  protected readonly showsSingleMember = computed(() =>
    this.selectedKind()?.showsMember === true && !this.isSplittable()
  );

  /**
   * What that box is called. Per-option, because the same field means "Member" on one and "Who owes
   * us" on the next — and calling it "Member" on the owed-to-us pair sent officers hunting the
   * roster for a name that was never on it.
   */
  protected readonly memberLabel = computed(() => this.selectedKind()?.counterpartyLabel ?? 'Member');

  protected readonly members = computed(() => this.page()?.members ?? []);

  /** Handing over gil that was already promised — the app knows the amount, so it fills it in. */
  protected readonly settlesMemberDebt = computed(() =>
    this.selectedKind()?.settlesMemberDebt === true
  );

  /**
   * The roster for the single-member picker, annotated with what each person is still owed.
   *
   * When settling, anyone owed something is listed first — that is who the officer means — and
   * people who have LEFT the linkshell are added back in, because a departed member can still be
   * owed gil and still has to be settleable.
   */
  protected readonly memberOptions = computed(() => {
    const outstanding = this.summary()?.owedToMembers ?? [];
    const owedByName = new Map(
      outstanding.map(owed => [owed.characterName.toLowerCase(), owed.amount])
    );

    const rows = this.members().map(person => ({
      characterName: person.characterName,
      rank: person.rank ?? null,
      owed: owedByName.get(person.characterName.toLowerCase()) ?? 0
    }));

    if (!this.settlesMemberDebt()) {
      return rows;
    }

    const onRoster = new Set(rows.map(row => row.characterName.toLowerCase()));
    for (const owed of outstanding) {
      if (!onRoster.has(owed.characterName.toLowerCase())) {
        rows.push({ characterName: owed.characterName, rank: null, owed: owed.amount });
      }
    }
    return rows.sort((a, b) =>
      b.owed - a.owed || a.characterName.localeCompare(b.characterName)
    );
  });

  /** The typed name doubles as the search term, so there is one box rather than two. */
  protected readonly filteredMemberOptions = computed(() => {
    const term = this.member().trim().toLowerCase();
    const matches = term
      ? this.memberOptions().filter(person => person.characterName.toLowerCase().includes(term))
      : this.memberOptions();
    return matches.slice(0, FinancesSectionComponent.MAX_MEMBER_RESULTS);
  });

  /** What the currently-named member is still owed, for the hint beside the amount. */
  protected readonly owedToChosenMember = computed(() => {
    const name = this.member().trim().toLowerCase();
    if (!name) {
      return 0;
    }
    return this.memberOptions().find(person => person.characterName.toLowerCase() === name)?.owed ?? 0;
  });

  protected chooseMember(person: { characterName: string; owed: number }): void {
    this.member.set(person.characterName);
    this.memberPickerOpen.set(false);
    // Settling hands over what was already promised, so the amount is known rather than guessed.
    if (this.settlesMemberDebt() && person.owed > 0) {
      this.amount = person.owed;
    }
  }

  // Delayed, so a click on an option registers before the menu closes underneath it.
  protected closeMemberPicker(): void {
    setTimeout(() => this.memberPickerOpen.set(false), 120);
  }

  protected readonly filteredMembers = computed(() => {
    const term = this.memberFilter().trim().toLowerCase();
    const matches = term
      ? this.members().filter(member => member.characterName.toLowerCase().includes(term))
      : this.members();
    return matches.slice(0, FinancesSectionComponent.MAX_MEMBER_RESULTS);
  });

  protected readonly pickedMembers = computed(() => {
    const picked = this.pickedIds();
    return this.members().filter(member => picked.has(member.membershipId));
  });

  /**
   * Who gets what. Mirrors TreasurySplit.Allocate on the server exactly — sorted by name, with the
   * leftover gil going to the first few — so the preview never promises a different split than the
   * one that gets recorded.
   *
   * A method rather than a computed because `amount` is an ngModel field, not a signal.
   */
  protected shares(): { member: ActivityTreasuryMember; share: number }[] {
    const people = [...this.pickedMembers()].sort((a, b) =>
      a.characterName.localeCompare(b.characterName, undefined, { sensitivity: 'base' })
    );
    if (people.length === 0) {
      return [];
    }
    const total = Math.max(0, Math.floor(this.amount || 0));
    const base = Math.floor(total / people.length);
    const leftover = total % people.length;
    return people.map((member, index) => ({ member, share: base + (index < leftover ? 1 : 0) }));
  }

  protected toggleMember(membershipId: number): void {
    // A new Set each time: OnPush only sees the change if the reference changes.
    const next = new Set(this.pickedIds());
    if (!next.delete(membershipId)) {
      next.add(membershipId);
    }
    this.pickedIds.set(next);
  }

  protected clearPickedMembers(): void {
    this.pickedIds.set(new Set());
  }

  /** Everything that has to be true before a split can be recorded. */
  protected splitBlocker(): string {
    if (!this.isSplittable()) {
      return '';
    }
    const picked = this.pickedIds().size;
    if (this.unresolvedMembers().length > 0) {
      return 'Some of these members have left the linkshell. Remove them or pick who replaces them.';
    }
    if (picked === 0) {
      return 'Pick who this is for — one member, or several to split it.';
    }
    if (Math.max(0, Math.floor(this.amount || 0)) < picked) {
      return 'That is not enough gil to give everyone at least 1.';
    }
    return '';
  }

  /**
   * Everything that has to be true before the form can be submitted — the split rules above, plus
   * the one rule that has nothing to do with splitting.
   *
   * A kind that creates or settles gil owed to a member has nowhere to put that obligation without
   * a name: the who-we-owe list is keyed on the name, so a nameless one would sit in an anonymous
   * bucket forever. The server refuses it, and a refusal comes back as a banner that does not point
   * at the empty box — so the form says it itself, beside the box.
   */
  protected formBlocker(): string {
    const kind = this.selectedKind();
    if (kind?.requiresMember && !this.isSplittable() && !this.member().trim()) {
      return 'Name the member this is owed to.';
    }
    // The same argument, applied to the one figure that had no names behind it. A linkshell has no
    // bank: gil on hand is the sum of what sits on people's characters, so gil that arrives on
    // nobody's — or leaves nobody's — cannot be found again.
    if (kind?.requiresHolder && !this.holder().trim()) {
      return kind.bringsCashIn
        ? 'Say who is holding this gil.'
        : 'Say whose gil this is coming out of.';
    }
    return this.splitBlocker();
  }

  /** Whether the holder box shows at all. Exactly the options that move gil on hand. */
  protected readonly showsHolder = computed(() => this.selectedKind()?.requiresHolder === true);

  /**
   * What that box is called, which flips with the direction: "who's holding this gil" as it arrives,
   * "whose gil is this coming out of" as it leaves. Server-supplied, like every other label.
   */
  protected readonly holderLabel = computed(
    () => this.selectedKind()?.holderLabel ?? 'Who’s holding this gil'
  );

  /**
   * Roster names to offer, narrowed as the officer types.
   *
   * A menu rather than a bare text box because holders are nearly always members and a typo files
   * the same person under two names — the known limit every projection here shares. It is still a
   * free-text box underneath, because gil regularly sits on a mule that is not on the roster.
   */
  protected readonly filteredHolderOptions = computed(() => {
    const typed = this.holder().trim().toLowerCase();
    const names = this.members().map(member => member.characterName);
    const matches = typed
      ? names.filter(name => name.toLowerCase().includes(typed))
      : names;
    // One exact match left is the name already in the box: a menu offering only what is typed is a
    // menu with nothing to add.
    if (matches.length === 1 && matches[0].toLowerCase() === typed) {
      return [];
    }
    return matches.slice(0, 8);
  });

  protected chooseHolder(name: string): void {
    this.holder.set(name);
    this.holderPickerOpen.set(false);
  }

  /** Deferred, so a click on a menu row lands before blur closes the menu. */
  protected closeHolderPicker(): void {
    setTimeout(() => this.holderPickerOpen.set(false), 120);
  }

  /** The live preview under the form: plain words for what this is about to do. */
  protected preview(): string {
    const kind = this.selectedKind();
    if (!kind) {
      return '';
    }
    // replaceAll, not replace: two templates mention the amount twice ("takes {0} … clears {0}"),
    // and replace() would leave the second one rendering the literal "{0}".
    const sentence = kind.previewTemplate.replaceAll('{0}', (this.amount || 0).toLocaleString());
    if (!this.isSplittable()) {
      return sentence;
    }

    const shares = this.shares();
    if (shares.length === 0) {
      return sentence;
    }
    // One member is a normal answer here, not a split of one. "1 member at 250,000 gil each" is
    // arithmetic nobody asked for when there is nothing to divide.
    if (shares.length === 1) {
      return `${sentence} All of it goes to ${shares[0].member.characterName}.`;
    }

    const smallest = shares[shares.length - 1].share;
    const extra = shares.filter(entry => entry.share > smallest).length;
    const each = `That is ${shares.length} members at ${smallest.toLocaleString()} gil each`;
    return extra === 0
      ? `${sentence} ${each}.`
      : `${sentence} ${each}, and the first ${extra} ${extra === 1 ? 'gets' : 'get'} 1 extra.`;
  }

  /** Entries grouped into runs by the viewer's calendar day, so the list carries date headers. */
  protected readonly groupedEntries = computed(() => {
    const groups: { key: string; label: string; entries: ActivityTreasuryEntry[] }[] = [];
    let current: { key: string; label: string; entries: ActivityTreasuryEntry[] } | null = null;
    for (const entry of this.entries()) {
      const key = this.activity.localDayKey(entry.transactionDate) ?? 'unknown';
      if (!current || current.key !== key) {
        current = {
          key,
          label: this.activity.formatDate(entry.transactionDate) ?? 'Unknown date',
          entries: []
        };
        groups.push(current);
      }
      current.entries.push(entry);
    }
    return groups;
  });

  /**
   * "000142" -> "#142". Mirrors TreasuryLabels.EntryReference on the server: the number is stored
   * zero-padded so it sorts as text, but six digits is a formality nobody says out loud. Used for a
   * row's own number AND for the "Reverses #142" tag, so a reference always matches the row it
   * points at.
   */
  protected entryRef(entryNumber: string | null | undefined): string {
    const trimmed = (entryNumber ?? '').trim().replace(/^0+/, '');
    return trimmed ? `#${trimmed}` : '#';
  }

  /**
   * The form card's head. The form does three jobs off one set of fields, and only the submit
   * button used to say which — too late to be reassuring for someone who clicked Fix on the wrong
   * row. Naming the entry in the Fix case is the point: it is the one mood that acts on something
   * that already exists.
   */
  protected readonly formTitle = computed(() => {
    const fixing = this.fixingEntry();
    if (fixing) {
      return `Fix entry ${this.entryRef(fixing.entryNumber)}`;
    }
    return this.editingId() === null ? 'Record a transaction' : 'Edit draft';
  });

  /** A split's names, kept to a line: "Ashira, Millhouse, Zeid +5 more". */
  protected recipientSummary(entry: ActivityTreasuryEntry): string {
    const names = entry.recipients.map(recipient => recipient.characterName);
    return names.length <= 3
      ? names.join(', ')
      : `${names.slice(0, 3).join(', ')} +${names.length - 3} more`;
  }

  protected onSearch(value: string): void {
    this.search.set(value);
    this.pageIndex.set(0);
  }

  protected setFilter(filter: ActivityTreasuryFilter): void {
    this.filter.set(filter);
    this.pageIndex.set(0);
  }


  protected prevPage(): void {
    this.pageIndex.set(Math.max(0, this.pageIndex() - 1));
  }

  protected nextPage(): void {
    this.pageIndex.set(Math.min(this.pageCount() - 1, this.pageIndex() + 1));
  }

  protected toggleForm(): void {
    if (this.showForm()) {
      this.closeForm();
      return;
    }
    this.resetForm();
    this.showForm.set(true);
  }

  protected closeForm(): void {
    this.showForm.set(false);
    this.editingId.set(null);
    this.fixingEntry.set(null);
  }

  protected beginEditDraft(entry: ActivityTreasuryEntry): void {
    this.loadIntoForm(entry);
    this.editingId.set(entry.id);
    this.fixingEntry.set(null);
    this.showForm.set(true);
  }

  /** Fix: reverses the wrong entry and records the corrected one, in one action. */
  protected beginFix(entry: ActivityTreasuryEntry): void {
    this.loadIntoForm(entry);
    this.editingId.set(null);
    this.fixingEntry.set(entry);
    this.showForm.set(true);
  }

  protected async submit(confirm: boolean): Promise<void> {
    const linkshellId = this.selectedLinkshellId();
    if (linkshellId === null || !this.selectedKind()) {
      return;
    }
    const amount = Math.max(0, Math.floor(this.amount || 0));
    if (amount <= 0 || this.formBlocker()) {
      return;
    }

    const splitting = this.isSplittable();
    const base = {
      transactionKind: this.transactionKind(),
      amount,
      transactionDate: this.toIsoDate(this.transactionDate),
      memo: this.note.trim() || null,
      counterpartyAppUserId: null,
      // A split names its members on their own lines, so there is no single counterparty.
      counterpartyCharacterName: splitting ? null : this.member().trim() || null,
      recipientMembershipIds: splitting ? [...this.pickedIds()] : null,
      // NOT suppressed for a split, unlike the counterparty above: a split that moves gil on hand
      // moves it off ONE mule however many people share the proceeds.
      holderAppUserId: null,
      holderCharacterName: this.holder().trim() || null
    };

    const fixing = this.fixingEntry();
    if (fixing) {
      if (!this.reason.trim()) {
        return;
      }
      await this.treasury.fix(fixing.id, { ...base, reason: this.reason.trim() });
      this.closeForm();
      return;
    }

    const draftId = this.editingId();
    if (draftId !== null) {
      await this.treasury.updateDraft(draftId, { ...base, confirm });
    } else {
      await this.treasury.record(linkshellId, { ...base, confirm });
    }
    this.closeForm();
  }

  protected async confirmEntry(entry: ActivityTreasuryEntry): Promise<void> {
    await this.treasury.confirm(entry.id);
  }

  /**
   * Discard a draft. Two presses rather than window.confirm(): Discord Activities run in a
   * sandboxed iframe without `allow-modals`, so confirm() returns false and the first press did
   * nothing at all — silently, with no way to tell that from "the server said no".
   */
  protected async discardDraft(entry: ActivityTreasuryEntry): Promise<void> {
    if (this.discardingId() !== entry.id) {
      this.discardingId.set(entry.id);
      return;
    }
    this.discardingId.set(null);
    await this.treasury.discardDraft(entry.id);
  }

  /**
   * Reverse a confirmed entry: an inline panel, NOT window.prompt().
   *
   * prompt() is suppressed in Discord's sandboxed iframe exactly like confirm() is — it returns
   * null without drawing anything — so the old code took the "they cancelled" branch on every
   * single press and Reverse was a button that did nothing at all. The reason is not optional
   * either: the server refuses a reversal without one, so there has to be somewhere to type it.
   */
  protected beginReverse(entry: ActivityTreasuryEntry): void {
    this.discardingId.set(null);
    this.reverseReason = '';
    this.reversingEntry.set(entry);
  }

  protected cancelReverse(): void {
    this.reversingEntry.set(null);
    this.reverseReason = '';
  }

  protected async confirmReverse(): Promise<void> {
    const entry = this.reversingEntry();
    const why = this.reverseReason.trim();
    if (!entry || !why) {
      return;
    }
    await this.treasury.reverse(entry.id, why);
    this.cancelReverse();
  }

  // ---- ticking names off either half of the sheet -----------------------------------------------
  //
  // Two panels, one set of rules: "who we owe" pays members off, "owed to us by" records gil
  // arriving. Every method below takes the side it is working on, so the two cannot behave
  // differently — which matters most for the parts that make ticking safe (settle in full only, and
  // the expectedAmount re-check that stops a stale figure being settled).

  // --- Who's holding the gil -----------------------------------------------------------------
  //
  // The third figure on the sheet finally gets names behind it. Unlike the two lists below it, this
  // one is never ticked: gil leaves a mule by being SPENT, and every movement now names the mule it
  // moved through, so the list maintains itself.

  protected readonly holdersOpen = signal(false);

  protected readonly holdersTitle = 'Who’s holding the gil';
  protected readonly unnamedHolder = 'Nobody named';
  protected readonly holdersFootnote =
    'Recorded on each transaction as the gil moves — these add up to gil on hand.';

  protected readonly gilHolders = computed(() => this.summary()?.gilHolders ?? []);

  /**
   * One row's share of the total, for the bar behind it. Against the largest row rather than the
   * total, so a treasury split evenly between four people shows four full bars instead of four
   * quarter-stubs nobody can compare.
   *
   * Magnitudes: an overspent mule reads as a negative amount, and a bar of negative width is
   * nothing at all.
   */
  protected holderShare(amount: number): number {
    const largest = Math.max(...this.gilHolders().map(holder => Math.abs(holder.amount)), 0);
    return largest === 0 ? 0 : Math.round((Math.abs(amount) / largest) * 100);
  }

  protected readonly owedToMembers = computed(() => this.summary()?.owedToMembers ?? []);

  /** Who owes the LINKSHELL. The mirror list, ticked the same way. */
  protected readonly owedToUsBy = computed(() => this.summary()?.owedToUsBy ?? []);

  private rowsFor(side: SettleSide): readonly ActivityTreasuryMemberObligation[] {
    return side === 'weOwe' ? this.owedToMembers() : this.owedToUsBy();
  }

  private picksFor(side: SettleSide): ReadonlySet<string> {
    return side === 'weOwe' ? this.pickedOwed() : this.pickedOwedToUs();
  }

  /** Only rows that are BOTH ticked and still settleable — so a tick left over from a previous list,
      or one already settled by someone else, quietly stops counting. */
  protected settlePicks(side: SettleSide): readonly ActivityTreasuryMemberObligation[] {
    const picked = this.picksFor(side);
    return this.rowsFor(side).filter(
      owed => owed.canSettle && picked.has(owed.characterName.toLowerCase())
    );
  }

  protected settleTotal(side: SettleSide): number {
    return this.settlePicks(side).reduce((total, owed) => total + owed.amount, 0);
  }

  /**
   * Ticked past what the linkshell holds. Said, never blocked — the form has never refused a payout
   * for want of gil on hand either, and a rule only one surface enforced is worse than no rule.
   *
   * Only ever true on the paying side: gil arriving cannot overdraw anything.
   */
  protected settleShort(side: SettleSide): boolean {
    return side === 'weOwe' && this.settleTotal(side) > (this.summary()?.cashOnHand ?? 0);
  }

  protected canSettleAny(side: SettleSide): boolean {
    return this.canManage() && this.rowsFor(side).some(owed => owed.canSettle);
  }

  protected isOwedPicked(side: SettleSide, characterName: string): boolean {
    return this.picksFor(side).has(characterName.toLowerCase());
  }

  /** Which panel has its button armed, if either. One at a time: an armed confirmation belongs to
      the selection that armed it, and arming both at once would invite pressing the wrong one. */
  protected settleConfirmingSide(side: SettleSide): boolean {
    return this.settleConfirming() === side;
  }

  protected toggleOwed(side: SettleSide, characterName: string): void {
    const next = new Set(this.picksFor(side));
    const key = characterName.toLowerCase();
    if (!next.delete(key)) {
      next.add(key);
    }
    this.setPicks(side, next);
    // Changing the selection un-arms the button: a confirmation applies to what was on screen when
    // it was pressed, not to whatever the selection became afterwards.
    this.settleConfirming.set(null);
  }

  protected clearOwedPicks(side: SettleSide): void {
    this.setPicks(side, new Set());
    this.settleConfirming.set(null);
  }

  /** Which mule box this side reads. Paying out and taking in are different people. */
  protected settleHolder(side: SettleSide): string {
    return side === 'weOwe' ? this.settleHolderWeOwe : this.settleHolderOwedToUs;
  }

  protected setSettleHolder(side: SettleSide, value: string): void {
    if (side === 'weOwe') {
      this.settleHolderWeOwe = value;
    } else {
      this.settleHolderOwedToUs = value;
    }
    // Same rule as changing the selection: a confirmation applies to what was on screen when it was
    // pressed, so re-aiming the payout at a different mule un-arms the button.
    this.settleConfirming.set(null);
  }

  /** What the box is called on each side — gil leaving a mule, or arriving on one. */
  protected settleHolderLabel(side: SettleSide): string {
    return side === 'weOwe' ? 'Paying out of' : 'Received onto';
  }

  private setPicks(side: SettleSide, picks: ReadonlySet<string>): void {
    if (side === 'weOwe') {
      this.pickedOwed.set(picks);
    } else {
      this.pickedOwedToUs.set(picks);
    }
  }

  protected async settleOwed(side: SettleSide): Promise<void> {
    const linkshellId = this.selectedLinkshellId();
    const picks = this.settlePicks(side);
    if (linkshellId === null || picks.length === 0) {
      return;
    }

    // Named before the first press, not after the confirm: the whole point of the two-step is that
    // the second press does exactly what the first one described, and a form that only mentions a
    // missing box once it is armed asks for the answer at the wrong moment.
    const holder = this.settleHolder(side).trim();
    if (!holder) {
      return;
    }

    if (!this.settleConfirmingSide(side)) {
      this.settleConfirming.set(side);
      return;
    }

    // expectedAmount is what this panel is showing. The server settles what the books say and
    // refuses any row where the two differ, so a panel left open while another officer records more
    // gil cannot hand over — or take in — the newer, larger figure.
    const body = picks.map(owed => ({
      characterName: owed.characterName,
      expectedAmount: owed.amount
    }));
    if (side === 'weOwe') {
      await this.treasury.settleOwed(linkshellId, body, holder);
    } else {
      await this.treasury.settleOwedToUs(linkshellId, body, holder);
    }

    // Cleared rather than kept: whoever settled has dropped off the reloaded list, and anyone
    // skipped was skipped BECAUSE their figure moved — so their old tick is exactly the thing that
    // should not be re-submitted without being looked at again.
    this.clearOwedPicks(side);
  }

  /**
   * Switch action, and take that action's first reason.
   *
   * Keeps the current reason when it belongs to the action being switched to, which only happens
   * when the action did not really change — so re-clicking the active button is a no-op rather than
   * a silent reset of a half-filled form.
   */
  protected setAction(actionKey: string): void {
    this.action.set(actionKey);
    const reasons = this.reasonsFor(actionKey);
    if (reasons.length === 0) {
      return;
    }
    if (!reasons.some(reason => reason.key === this.transactionKind())) {
      this.transactionKind.set(reasons[0].key);
    }
  }


  private resetForm(): void {
    // By ACTION, never by position. `kinds()[0]` was the default until the catalog was reordered
    // into picker order, at which point "the first kind in the file" silently became the default
    // for every new entry — a coupling nothing in the form made visible.
    const first = this.actions()[0]?.key ?? '';
    this.action.set(first);
    this.transactionKind.set(this.reasonsFor(first)[0]?.key ?? '');
    this.amount = 0;
    this.transactionDate = this.toInputDate(new Date().toISOString());
    this.member.set('');
    this.memberPickerOpen.set(false);
    this.holder.set('');
    this.holderPickerOpen.set(false);
    this.note = '';
    this.reason = '';
    this.memberFilter.set('');
    this.pickedIds.set(new Set());
    this.unresolvedMembers.set([]);
    this.editingId.set(null);
    this.fixingEntry.set(null);
  }

  private loadIntoForm(entry: ActivityTreasuryEntry): void {
    // The entry's OWN kind, whatever it is — including one nobody may pick any more. Every kind is
    // on the wire for exactly this: the form has to be able to reproduce what it is correcting.
    const loaded = this.kinds().find(kind => kind.key === entry.transactionKind);
    this.transactionKind.set(loaded?.key ?? this.reasonsFor(this.actions()[0]?.key ?? '')[0]?.key ?? '');
    // Open on the action the entry belongs to rather than resetting to the first one.
    this.action.set(loaded?.action ?? this.actions()[0]?.key ?? '');
    this.amount = entry.amount;
    this.transactionDate = this.toInputDate(entry.transactionDate);
    this.member.set(entry.counterpartyCharacterName ?? '');
    this.memberPickerOpen.set(false);
    // Comes back so a Fix does not silently move the gil onto nobody's mule — the replacement is
    // built from this form, not from the original's lines.
    this.holder.set(entry.holderCharacterName ?? '');
    this.holderPickerOpen.set(false);
    this.note = entry.memo ?? '';
    this.reason = '';
    this.memberFilter.set('');

    // Fixing rebuilds the entry from this form, so a split has to come back in full. Anyone who has
    // left the linkshell cannot be re-picked — name them rather than dropping them, or fixing a
    // ten-way payout would quietly reverse all of it and re-record a fraction.
    const recipients = entry.recipients ?? [];
    this.pickedIds.set(new Set(
      recipients
        .map(recipient => recipient.membershipId)
        .filter((id): id is number => typeof id === 'number')
    ));
    this.unresolvedMembers.set(
      recipients.filter(recipient => recipient.membershipId == null)
        .map(recipient => recipient.characterName)
    );
  }

  protected dismissUnresolved(): void {
    this.unresolvedMembers.set([]);
  }

  // datetime-local wants naive local wall-clock; the server stores UTC instants.
  private toInputDate(iso: string | null | undefined): string {
    if (!iso) {
      return '';
    }
    const date = new Date(iso);
    if (Number.isNaN(date.getTime())) {
      return '';
    }
    const pad = (value: number) => String(value).padStart(2, '0');
    return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`
      + `T${pad(date.getHours())}:${pad(date.getMinutes())}`;
  }

  private toIsoDate(value: string): string | null {
    if (!value) {
      return null;
    }
    const date = new Date(value);
    return Number.isNaN(date.getTime()) ? null : date.toISOString();
  }
}
