import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, Input, computed, effect, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';

import { DiscordActivityService } from '../../discord/discord-activity.service';
import { TreasuryService } from '../../discord/treasury.service';
import type {
  ActivityTreasuryEntry,
  ActivityTreasuryFilter,
  ActivityTreasuryKind,
  ActivityTreasuryMember
} from '../../discord/discord-activity.types';

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
  protected amount = 0;
  protected transactionDate = '';
  // A signal, not a plain field: the amount prefills from whoever is picked, so everything derived
  // from the chosen member has to actually re-run.
  protected readonly member = signal('');
  protected readonly memberPickerOpen = signal(false);
  protected note = '';
  protected reason = '';

  // Splitting one amount between several members. `pickedIds` holds membership rows rather than
  // names, because that is what the server can check against this linkshell's roster.
  protected readonly memberFilter = signal('');
  protected readonly pickedIds = signal<ReadonlySet<number>>(new Set());
  /** Names on the entry being fixed whose members have since left. Blocks submit rather than
      quietly recording a smaller payout than the one being replaced. */
  protected readonly unresolvedMembers = signal<readonly string[]>([]);

  // "Gil on Hand" — someone logged onto the mule and counted.
  protected readonly showCheckGil = signal(false);
  protected countedGil = 0;
  /** What the box shows: countedGil with thousands separators. 7750000 and 775000 look alike at a
      glance, and a miscount here records a real correction against the books, so the digits are
      grouped while they are being typed. The box is type="text" for this — a number input will not
      render commas. countedGil stays the plain number everything else reads. */
  protected countedGilText = '';

  protected readonly filters: { key: ActivityTreasuryFilter; label: string }[] = [
    { key: 'all', label: 'All' },
    { key: 'in', label: 'Gil in' },
    { key: 'out', label: 'Gil out' },
    { key: 'drafts', label: 'Drafts' },
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

  /** The picker, grouped so the everyday options come first. */
  protected readonly kindGroups = computed(() => {
    const labels: Record<string, string> = {
      Common: 'Everyday',
      Owed: 'Gil promised but not moved yet',
      Setup: 'Setup'
    };
    const groups: { label: string; kinds: ActivityTreasuryKind[] }[] = [];
    for (const kind of this.kinds()) {
      const label = labels[kind.group] ?? kind.group;
      let group = groups.find(candidate => candidate.label === label);
      if (!group) {
        group = { label, kinds: [] };
        groups.push(group);
      }
      group.kinds.push(kind);
    }
    return groups;
  });

  protected readonly selectedKind = computed(() =>
    this.kinds().find(kind => kind.key === this.transactionKind()) ?? null
  );

  /** Shares one amount between several members — show the picker instead of the single name box. */
  protected readonly isSplittable = computed(() => this.selectedKind()?.isSplittable === true);

  protected readonly showsSingleMember = computed(() =>
    this.selectedKind()?.showsMember === true && !this.isSplittable()
  );

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
      return 'Pick at least one member to share the gil with.';
    }
    if (Math.max(0, Math.floor(this.amount || 0)) < picked) {
      return 'That is not enough gil to give everyone at least 1.';
    }
    return '';
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
    const smallest = shares[shares.length - 1].share;
    const extra = shares.filter(entry => entry.share > smallest).length;
    const each = `That is ${shares.length} ${shares.length === 1 ? 'member' : 'members'}`
      + ` at ${smallest.toLocaleString()} gil each`;
    return extra === 0
      ? `${sentence} ${each}.`
      : `${sentence} ${each}, and the first ${extra} get 1 extra.`;
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
    if (amount <= 0 || this.splitBlocker()) {
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
      recipientMembershipIds: splitting ? [...this.pickedIds()] : null
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

  protected async discardDraft(entry: ActivityTreasuryEntry): Promise<void> {
    if (!window.confirm('Discard this draft? Nothing has been recorded yet.')) {
      return;
    }
    await this.treasury.discardDraft(entry.id);
  }

  protected async reverseEntry(entry: ActivityTreasuryEntry): Promise<void> {
    // Says plainly what reversing does, because "reverse" on its own reads like a delete.
    const why = window.prompt(
      'This adds an opposite entry that cancels this one out. The original stays in the list.\n\n'
        + 'Why is it being reversed?'
    );
    if (why === null || !why.trim()) {
      return;
    }
    await this.treasury.reverse(entry.id, why.trim());
  }

  protected toggleCheckGil(): void {
    this.countedGil = this.summary()?.cashOnHand ?? 0;
    this.countedGilText = this.countedGil.toLocaleString('en-US');
    this.showCheckGil.set(!this.showCheckGil());
  }

  /**
   * Regroups the counted-gil box on every keystroke, keeping the caret beside the digit it was
   * already beside. Rewriting the value is what strips anything that is not a digit — a minus sign
   * included, since "how much is actually there" is never negative — but it also sends the caret to
   * the end, which makes editing the middle of a number impossible. So the caret is restored by
   * counting digits rather than characters: commas move as the number regroups, digits do not.
   */
  protected onCountedGilInput(event: Event): void {
    const box = event.target as HTMLInputElement;
    const caretIndex = box.selectionStart ?? box.value.length;
    const digitsBeforeCaret = this.countDigits(box.value.slice(0, caretIndex));

    const digits = box.value.replace(/\D/g, '');
    this.countedGil = digits ? Number(digits) : 0;
    this.countedGilText = digits ? this.countedGil.toLocaleString('en-US') : '';

    box.value = this.countedGilText;
    const caret = this.caretAfterDigit(this.countedGilText, digitsBeforeCaret);
    box.setSelectionRange(caret, caret);
  }

  private countDigits(text: string): number {
    return (text.match(/\d/g) ?? []).length;
  }

  /** Where the caret sits so that `digit` digits are behind it. */
  private caretAfterDigit(text: string, digit: number): number {
    if (digit <= 0) {
      return 0;
    }
    let seen = 0;
    for (let index = 0; index < text.length; index++) {
      if (/\d/.test(text[index]) && ++seen === digit) {
        return index + 1;
      }
    }
    return text.length;
  }

  protected async submitCheckGil(): Promise<void> {
    const linkshellId = this.selectedLinkshellId();
    if (linkshellId === null) {
      return;
    }
    await this.treasury.checkGil(linkshellId, Math.max(0, Math.floor(this.countedGil || 0)));
    this.showCheckGil.set(false);
  }

  protected difference(): number {
    return Math.floor(this.countedGil || 0) - (this.summary()?.cashOnHand ?? 0);
  }

  private resetForm(): void {
    this.transactionKind.set(this.kinds()[0]?.key ?? '');
    this.amount = 0;
    this.transactionDate = this.toInputDate(new Date().toISOString());
    this.member.set('');
    this.memberPickerOpen.set(false);
    this.note = '';
    this.reason = '';
    this.memberFilter.set('');
    this.pickedIds.set(new Set());
    this.unresolvedMembers.set([]);
    this.editingId.set(null);
    this.fixingEntry.set(null);
  }

  private loadIntoForm(entry: ActivityTreasuryEntry): void {
    this.transactionKind.set(entry.transactionKind ?? this.kinds()[0]?.key ?? '');
    this.amount = entry.amount;
    this.transactionDate = this.toInputDate(entry.transactionDate);
    this.member.set(entry.counterpartyCharacterName ?? '');
    this.memberPickerOpen.set(false);
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
