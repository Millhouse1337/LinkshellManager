import { Component, computed, effect, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';

import { DiscordActivityService } from '../../discord/discord-activity.service';
import { StarRatingComponent } from './star-rating.component';

interface RatingMember {
  appUserId: string;
  characterName: string;
  altCharacterName1?: string | null;
  altCharacterName2?: string | null;
}

interface TargetCharacter {
  slot: number;
  name: string;
}

// "Rate teammates" panel: pick another member (searchable filter), pick which of
// their characters (main / alt) to rate, then rate their gear + skill per job with
// 0–5 stars and leave one short comment about that character. Self-assessment lives
// on the profile job grid instead — this panel only ever rates OTHER players.
@Component({
  selector: 'app-job-ratings-panel',
  standalone: true,
  imports: [FormsModule, StarRatingComponent],
  template: `
    <div class="jr">
      <div class="jr__head">
        <h3>Rate teammates</h3>
        <p class="jr__hint">Rate another member's gear &amp; skill per job, and leave a short note. They see anonymous averages and an AI summary.</p>
      </div>

      <div class="jr__pick">
        <input
          type="search"
          class="jr__search"
          placeholder="Search members…"
          [ngModel]="filterTerm()"
          (ngModelChange)="filterTerm.set($event)"
          name="jrSearch" />
        <select [ngModel]="selectedTarget()" (ngModelChange)="selectTarget($event)" name="jrTarget">
          <option value="">Select a member…</option>
          @for (m of filteredMembers(); track m.appUserId) {
            <option [value]="m.appUserId">{{ m.characterName }}</option>
          }
        </select>
      </div>

      @if (selectedTarget()) {
        <!-- Character picker: only shown when the teammate has named alts. -->
        @if (targetCharacters().length > 1) {
          <div class="jr__chars">
            @for (c of targetCharacters(); track c.slot) {
              <button type="button" class="jr__char" [class.on]="selectedSlot() === c.slot" (click)="selectSlot(c.slot)">
                {{ c.name }}
              </button>
            }
          </div>
        }

        <div class="jr__grid">
          <div class="jr__row jr__row--head">
            <span>Job</span>
            <span>Gear</span>
            <span>Skill</span>
          </div>
          @for (name of jobNames(); track name; let i = $index) {
            <div class="jr__row" [class.jr__row--unleveled]="!levels()[i]">
              <span class="jr__job">
                {{ name }}
                @if (levels()[i]) {
                  <span class="jr__lvl">Lv {{ levels()[i] }}</span>
                } @else {
                  <span class="jr__lvl jr__lvl--none">—</span>
                }
              </span>
              <app-star-rating [value]="edit[i].gear" (valueChange)="setGear(i, $event)" />
              <app-star-rating [value]="edit[i].skill" (valueChange)="setSkill(i, $event)" />
            </div>
          }
        </div>

        <div class="jr__comment">
          <label for="jrComment">Comment about {{ selectedCharacterName() }} (optional)</label>
          <textarea
            id="jrComment"
            rows="3"
            maxlength="1000"
            placeholder="Honest, constructive feedback — shared anonymously."
            [ngModel]="commentDraft()"
            (ngModelChange)="commentDraft.set($event)"
            name="jrComment"></textarea>
          <div class="jr__commentActions">
            @if (commentSaved()) { <span class="jr__saved">Saved ✓</span> }
            <button type="button" class="btn sm primary" [disabled]="savingComment()" (click)="saveComment()">
              {{ savingComment() ? 'Saving…' : 'Save comment' }}
            </button>
          </div>
        </div>
      } @else {
        <p class="jr__empty">Pick a teammate above to rate their jobs.</p>
      }
    </div>
  `,
  styles: [`
    .jr { margin-top: 22px; max-width: 460px; margin-left: auto; margin-right: auto; text-align: center; }
    .jr__head h3 { margin: 0 0 2px; font-size: 14px; }
    .jr__hint { margin: 0 0 12px; font-size: 12px; color: var(--fg-3); }
    .jr__pick { display: flex; flex-direction: column; gap: 6px; align-items: stretch; max-width: 280px; margin: 0 auto 14px; }
    .jr__search, .jr__pick select { width: 100%; }
    .jr__chars { display: inline-flex; flex-wrap: wrap; gap: 6px; justify-content: center; margin: 0 auto 14px; }
    .jr__char {
      border: 1px solid var(--border); background: rgba(255,255,255,0.025); color: var(--fg-2);
      border-radius: 999px; padding: 3px 12px; font-size: 12px; font-weight: 600; cursor: pointer; font-family: inherit;
    }
    .jr__char.on { color: #fff; background: linear-gradient(180deg, #8d91ff 0%, #6268ff 100%); border-color: transparent; }
    .jr__grid { text-align: left; }
    .jr__row { display: grid; grid-template-columns: 1fr auto auto; gap: 14px; align-items: center; padding: 5px 0; border-top: 1px solid var(--border); }
    .jr__row--head { color: var(--fg-3); font-size: 11px; text-transform: uppercase; letter-spacing: .04em; border-top: 0; }
    .jr__row--head span:not(:first-child) { text-align: center; min-width: 92px; }
    .jr__job { font-weight: 600; font-size: 13px; display: inline-flex; align-items: baseline; gap: 7px; }
    .jr__lvl { font-weight: 600; font-size: 11px; color: var(--accent, #8d91ff); letter-spacing: .02em; }
    .jr__lvl--none { color: var(--fg-3); font-weight: 500; }
    .jr__row--unleveled { opacity: .55; }
    .jr__comment { margin-top: 14px; text-align: left; }
    .jr__comment label { display: block; font-size: 12px; color: var(--fg-2); margin-bottom: 4px; }
    .jr__comment textarea { width: 100%; resize: vertical; }
    .jr__commentActions { display: flex; align-items: center; justify-content: flex-end; gap: 10px; margin-top: 6px; }
    .jr__saved { font-size: 12px; color: var(--success, #5fbf7f); }
    .jr__empty { font-size: 12px; color: var(--fg-3); }
  `]
})
export class JobRatingsPanelComponent {
  private readonly activity = inject(DiscordActivityService);

  readonly linkshellId = input.required<number>();
  readonly jobNames = input.required<readonly string[]>();
  readonly members = input.required<RatingMember[]>();
  readonly currentAppUserId = input.required<string>();

  protected readonly filterTerm = signal('');
  protected readonly selectedTarget = signal('');
  protected readonly selectedSlot = signal(0);
  protected readonly commentDraft = signal('');
  protected readonly commentSaved = signal(false);
  protected readonly savingComment = signal(false);
  protected readonly edit: Record<number, { gear: number; skill: number }> = {};
  // The selected character's per-job levels (jobIndex -> level, 0 = unleveled),
  // shown next to each job so the rater knows what they're scoring.
  protected readonly levels = signal<Record<number, number>>({});

  // Other linkshell members (never yourself), filtered by the search box.
  protected readonly filteredMembers = computed<RatingMember[]>(() => {
    const me = this.currentAppUserId();
    const term = this.filterTerm().trim().toLowerCase();
    return this.members()
      .filter(m => m.appUserId && m.appUserId !== me)
      .filter(m => !term || m.characterName.toLowerCase().includes(term));
  });

  // The selected teammate's characters: main + each named alt (slot 0/1/2).
  protected readonly targetCharacters = computed<TargetCharacter[]>(() => {
    const target = this.selectedTarget();
    const member = this.members().find(m => m.appUserId === target);
    if (!member) { return []; }
    const chars: TargetCharacter[] = [{ slot: 0, name: member.characterName }];
    if (member.altCharacterName1?.trim()) { chars.push({ slot: 1, name: member.altCharacterName1.trim() }); }
    if (member.altCharacterName2?.trim()) { chars.push({ slot: 2, name: member.altCharacterName2.trim() }); }
    return chars;
  });

  protected selectedCharacterName(): string {
    return this.targetCharacters().find(c => c.slot === this.selectedSlot())?.name ?? 'this teammate';
  }

  constructor() {
    // (Re)load whenever the selected target, character slot, or linkshell changes.
    effect(() => {
      const target = this.selectedTarget();
      const slot = this.selectedSlot();
      const ls = this.linkshellId();
      if (target && ls) { void this.load(target, slot, ls); }
    });
  }

  protected selectTarget(appUserId: string): void {
    this.commentSaved.set(false);
    this.selectedSlot.set(0);
    this.selectedTarget.set(appUserId);
  }

  protected selectSlot(slot: number): void {
    this.commentSaved.set(false);
    this.selectedSlot.set(slot);
  }

  protected async load(target: string, slot: number, linkshellId: number): Promise<void> {
    const res = await this.activity.loadJobRatings(linkshellId, target, slot);
    if (!res) { return; }
    const levels: Record<number, number> = {};
    for (const j of res.jobs) {
      this.edit[j.jobIndex] = { gear: j.myGear, skill: j.mySkill };
      levels[j.jobIndex] = j.level ?? 0;
    }
    this.levels.set(levels);
    this.commentDraft.set(res.myComment ?? '');
  }

  private async save(jobIndex: number): Promise<void> {
    const e = this.edit[jobIndex];
    if (!e) { return; }
    const clamp = (v: number) => Math.max(0, Math.min(5, Math.trunc(Number(v) || 0)));
    // Relic is a self attribute (not surfaced here); always send false.
    await this.activity.rateJob(this.linkshellId(), this.selectedTarget(), jobIndex, clamp(e.gear), clamp(e.skill), false, this.selectedSlot());
  }

  protected setGear(jobIndex: number, value: number): void {
    this.edit[jobIndex] = { gear: value, skill: this.edit[jobIndex]?.skill ?? 0 };
    void this.save(jobIndex);
  }

  protected setSkill(jobIndex: number, value: number): void {
    this.edit[jobIndex] = { gear: this.edit[jobIndex]?.gear ?? 0, skill: value };
    void this.save(jobIndex);
  }

  protected async saveComment(): Promise<void> {
    this.savingComment.set(true);
    this.commentSaved.set(false);
    const ok = await this.activity.rateJobComment(this.linkshellId(), this.selectedTarget(), this.commentDraft().trim(), this.selectedSlot());
    this.savingComment.set(false);
    if (ok) { this.commentSaved.set(true); }
  }
}
