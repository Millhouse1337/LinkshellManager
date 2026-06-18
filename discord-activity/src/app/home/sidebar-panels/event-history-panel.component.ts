import { DatePipe } from '@angular/common';
import { Component, Input, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';

import { DiscordActivityService } from '../../discord/discord-activity.service';
import type {
  ActivityEditEventHistoryInput,
  ActivityEventComment,
  ActivityEventHistory,
  ActivityEventHistoryAbsentee,
  ActivityEventHistoryParticipant
} from '../../discord/discord-activity.types';

// Past (closed) event browser + editor for the Activity. Mirrors the web
// EventHistory page: anyone can view; leaders/officers can edit metadata/DKP,
// correct an attendee's DKP, or remove an attendee (refunds DKP). Embedded in
// the Event System tab; loads for the given linkshell.
@Component({
  selector: 'app-event-history-panel',
  standalone: true,
  imports: [FormsModule, DatePipe],
  template: `
    <div class="evt-history">
      <div class="evt-history__head">
        <h3>Past events</h3>
        <button type="button" class="btn ghost sm" (click)="load()" [disabled]="loading()">
          {{ loading() ? 'Loading…' : 'Refresh' }}
        </button>
      </div>

      @if (!loading() && histories().length === 0) {
        <p class="notice">No completed events yet.</p>
      }

      @for (h of histories(); track h.id) {
        <div class="evt-history__row">
          <button type="button" class="evt-history__summary" (click)="toggle(h)">
            <span class="evt-history__name">{{ h.eventName || 'Event' }}</span>
            <span class="evt-history__meta">
              {{ h.eventType || '—' }} · {{ h.endTime ? (h.endTime | date:'MMM d, y') : 'completed' }}
              · {{ h.dkpPerHour ?? 0 }} dkp/h · {{ h.participants.length }} attendees
            </span>
            <span class="evt-history__chev">{{ expandedId() === h.id ? '▾' : '▸' }}</span>
          </button>

          @if (expandedId() === h.id) {
            <div class="evt-history__body">
              @if (canManage() && edit[h.id]; as draft) {
                <div class="evt-history__edit">
                  <div class="evt-history__edit-row">
                    <label><span>Name</span><input [(ngModel)]="draft.eventName" [name]="'eName' + h.id" /></label>
                    <label><span>Type</span><input [(ngModel)]="draft.eventType" [name]="'eType' + h.id" /></label>
                    <label><span>Location</span><input [(ngModel)]="draft.eventLocation" [name]="'eLoc' + h.id" /></label>
                  </div>
                  <div class="evt-history__edit-row2">
                    <label class="evt-dur"><span>Duration</span>
                      <span class="evt-dur__inputs">
                        <input type="number" min="0" [value]="durationPart(draft, 'h')" (change)="setDurationPart(draft, 'h', $any($event.target).value)" [name]="'eDurH' + h.id" /><small>h</small>
                        <input type="number" min="0" max="59" [value]="durationPart(draft, 'm')" (change)="setDurationPart(draft, 'm', $any($event.target).value)" [name]="'eDurM' + h.id" /><small>m</small>
                        <input type="number" min="0" max="59" [value]="durationPart(draft, 's')" (change)="setDurationPart(draft, 's', $any($event.target).value)" [name]="'eDurS' + h.id" /><small>s</small>
                      </span>
                    </label>
                    <label><span>DKP / hour</span><input type="number" [(ngModel)]="draft.dkpPerHour" [name]="'eRate' + h.id" /></label>
                    <button type="button" class="btn primary sm" (click)="saveEvent(h)">Save event</button>
                  </div>
                  <p class="evt-history__hint">Changing DKP / hour rescales every attendee's earned DKP and balance.</p>
                </div>
              }

              <div class="evt-history__attendees">
                @if (canManage()) {
                  <div class="evt-att evt-att--head">
                    <span>Attendee</span>
                    <span class="evt-att__c" title="Counts toward this member's Active status">Active credit</span>
                    <span class="evt-att__c" title="Absent = no active credit; counts toward the inactive threshold">Absent</span>
                    <span class="evt-att__c">DKP</span>
                    <span></span>
                  </div>
                }
                @for (p of h.participants; track p.id) {
                  @if (canManage()) {
                    <div class="evt-att">
                      <span class="evt-history__who">
                        {{ p.characterName }}
                        <small>{{ p.jobName }}@if (p.subJobName) {/{{ p.subJobName }}}</small>
                      </span>
                      <span class="evt-att__c">
                        <input type="checkbox" [checked]="p.activeCredit !== false"
                               (change)="toggleCredit(h, p, $any($event.target).checked)"
                               title="Active-status credit for this event" />
                      </span>
                      <span class="evt-att__c">
                        <input type="checkbox" [checked]="p.activeCredit === false"
                               (change)="toggleCredit(h, p, !$any($event.target).checked)"
                               title="Absent = no active credit (counts toward the inactive threshold)" />
                      </span>
                      <span class="evt-att__c">
                        <input type="number" [step]="dkpStep()" min="0" [(ngModel)]="dkpDraft[p.id]" [name]="'pDkp' + p.id" />
                      </span>
                      <span class="evt-att__actions">
                        <button type="button" class="btn ghost sm" (click)="saveDkp(h, p)">Save</button>
                        <button type="button" class="btn sm danger-outline" (click)="removeParticipant(h, p)">Remove</button>
                      </span>
                    </div>
                  } @else {
                    <div class="evt-history__attendee">
                      <span class="evt-history__who">
                        {{ p.characterName }}
                        <small>{{ p.jobName }}@if (p.subJobName) {/{{ p.subJobName }}}</small>
                      </span>
                      <span class="evt-history__dkp">
                        <span class="tag" [class.success]="p.activeCredit !== false">{{ p.activeCredit !== false ? 'Credited' : 'No credit' }}</span>
                        {{ p.eventDkp ?? 0 }}
                      </span>
                    </div>
                  }
                }
              </div>

              @if (canManage() && (h.absentees?.length ?? 0) > 0) {
                <div class="evt-history__attendees evt-history__absentees">
                  <div class="evt-abs-head">Not at this event · absent ({{ h.absentees!.length }}) — add one to credit them &amp; grant DKP</div>
                  @for (a of h.absentees!; track a.appUserId) {
                    <div class="evt-abs">
                      <span class="evt-history__who">{{ a.characterName || 'Member' }}</span>
                      <span class="evt-att__c"><input type="checkbox" checked disabled title="Absent until you add them to the event" /></span>
                      <input type="number" class="evt-abs__dkp" [step]="dkpStep()" min="0"
                             [(ngModel)]="absDkpDraft[h.id + ':' + a.appUserId]" [name]="'absDkp' + h.id + a.appUserId" placeholder="DKP" />
                      <button type="button" class="btn primary sm" (click)="addAbsentee(h, a)">Add + DKP</button>
                    </div>
                  }
                </div>
              }

              @if (canManage() && h.participants.length > 0) {
                <div class="evt-history__bulk">
                  @if (confirmingClearCreditId() === h.id) {
                    <span class="evt-history__bulk-q">Remove active credit from all {{ h.participants.length }} attendees?</span>
                    <button type="button" class="btn sm danger-outline" (click)="clearActiveCredit(h)">Confirm undo</button>
                    <button type="button" class="btn ghost sm" (click)="cancelClearActiveCredit()">Cancel</button>
                  } @else if (confirmingClearAbsencesId() === h.id) {
                    <span class="evt-history__bulk-q">Stop this event counting toward active tracking (undo absences for members who missed it)?</span>
                    <button type="button" class="btn sm danger-outline" (click)="clearAbsences(h)">Confirm undo</button>
                    <button type="button" class="btn ghost sm" (click)="cancelClearAbsences()">Cancel</button>
                  } @else {
                    <button type="button" class="btn ghost sm" (click)="requestClearActiveCredit(h)"
                            title="Uncheck active credit for every attendee — for an event credited by accident.">
                      Undo active credit (whole event)
                    </button>
                    <button type="button" class="btn ghost sm" (click)="requestClearAbsences(h)"
                            title="Stop this event counting toward active tracking so members who missed it aren't marked absent for it.">
                      Undo absences (whole event)
                    </button>
                  }
                </div>
              }

              <div class="evt-disc">
                <div class="evt-disc__head">💬 Discussion</div>
                @for (c of commentsFor(h.id); track c.id) {
                  <div class="evt-disc__row">
                    <span class="evt-disc__who">
                      <strong [class.evt-disc__anon-name]="c.isAnonymous">{{ c.author }}</strong>
                      <small>{{ c.createdAt | date:'MMM d, h:mm a' }}</small>
                    </span>
                    <span class="evt-disc__body">{{ c.body }}</span>
                    @if (c.canDelete) {
                      <button type="button" class="btn ghost sm" (click)="removeComment(h, c.id)" title="Delete">✕</button>
                    }
                  </div>
                }
                @if (commentsFor(h.id).length === 0) {
                  <p class="evt-disc__empty">No comments yet — start the discussion.</p>
                }
                <div class="evt-disc__compose">
                  <textarea rows="2" [(ngModel)]="commentDraft[h.id]" [name]="'cd' + h.id"
                            placeholder="Add to the discussion…" maxlength="2000"></textarea>
                  <div class="evt-disc__actions">
                    <label class="evt-disc__anon"><input type="checkbox" [(ngModel)]="anonDraft[h.id]" [name]="'ca' + h.id" /> Post anonymously</label>
                    <button type="button" class="btn primary sm" (click)="postComment(h)">Post</button>
                  </div>
                </div>
              </div>

              @if (canManage()) {
                <div class="evt-history__danger">
                  @if (confirmingDeleteId() === h.id) {
                    <span class="evt-history__bulk-q">Delete this event permanently? Reverses all DKP it awarded/spent and removes its attendance, loot &amp; comments — can't be undone.</span>
                    <button type="button" class="btn sm danger-outline" (click)="deleteEvent(h)">Confirm delete</button>
                    <button type="button" class="btn ghost sm" (click)="cancelDelete()">Cancel</button>
                  } @else {
                    <button type="button" class="btn sm danger-outline" (click)="requestDelete(h)"
                            title="Permanently delete this event and reverse its DKP.">Delete event</button>
                  }
                </div>
              }
            </div>
          }
        </div>
      }
    </div>
  `,
  styles: [`
    .evt-history { margin-top: 12px; }
    .evt-history__head { display: flex; align-items: center; justify-content: space-between; gap: 8px; margin-bottom: 8px; }
    .evt-history__head h3 { margin: 0; font-size: 14px; }
    .evt-history__row { border: 1px solid var(--border); border-radius: var(--r-md); margin-bottom: 8px; overflow: hidden; }
    .evt-history__summary {
      display: flex; align-items: center; gap: 10px; width: 100%; text-align: left;
      padding: 10px 12px; background: var(--surface); border: 0; color: inherit; cursor: pointer;
    }
    .evt-history__summary:hover { background: var(--surface-2); }
    .evt-history__name { font-weight: 600; }
    .evt-history__meta { color: var(--fg-3); font-size: 12px; margin-left: auto; }
    .evt-history__chev { color: var(--fg-3); }
    .evt-history__body { padding: 12px; border-top: 1px solid var(--border); background: var(--bg-elev); }
    .evt-history__edit { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 8px 12px; margin-bottom: 12px; }
    .evt-history__edit label { display: flex; flex-direction: column; gap: 4px; font-size: 12px; color: var(--fg-2); }
    .evt-history__edit-row { grid-column: 1 / -1; display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 8px 12px; }
    .evt-history__edit-row2 { grid-column: 1 / -1; display: flex; gap: 12px; align-items: flex-end; }
    .evt-history__edit-row2 label { flex: 1; }
    .evt-history__edit-row2 button { flex: 0 0 auto; white-space: nowrap; }
    .evt-dur__inputs { display: flex; align-items: center; gap: 4px; }
    .evt-dur__inputs input { width: 52px; text-align: right; }
    .evt-dur__inputs small { color: var(--fg-3); margin-right: 4px; }
    .evt-history__hint { grid-column: 1 / -1; margin: 0; font-size: 11px; color: var(--fg-3); }
    .evt-history__attendee { display: flex; align-items: center; justify-content: space-between; gap: 10px; padding: 6px 0; border-top: 1px solid var(--border); }
    .evt-history__who small { color: var(--fg-3); margin-left: 6px; }
    .evt-history__dkp { display: inline-flex; align-items: center; gap: 6px; flex-wrap: wrap; justify-content: flex-end; }
    .evt-history__dkp input { width: 84px; text-align: right; }
    .evt-history__credit { display: inline-flex; align-items: center; gap: 4px; font-size: 12px; color: var(--fg-2); white-space: nowrap; }
    /* Manager attendee editor: aligned grid with Active credit / DKP headers.
       The actions column is a FIXED width (not auto) so the header row and every
       attendee row share identical tracks — otherwise each row's auto column
       sizes independently and the shared 1fr shifts the headers out of line. */
    .evt-att { display: grid; grid-template-columns: 1fr 96px 70px 92px 150px; gap: 10px; align-items: center; padding: 6px 0; border-top: 1px solid var(--border); }
    .evt-att--head { border-top: 0; padding-bottom: 2px; color: var(--fg-3); font-size: 11px; text-transform: uppercase; letter-spacing: .04em; }
    .evt-att__c { text-align: center; }
    .evt-att__c input[type=number] { width: 84px; text-align: right; }
    .evt-att__c input[type=checkbox] { cursor: pointer; }
    .evt-att__actions { display: inline-flex; gap: 6px; justify-content: flex-end; }
    /* Absentees (not at the event): name | Absent | DKP | Add. */
    .evt-history__absentees { margin-top: 8px; }
    .evt-abs-head { padding: 4px 0 2px; color: var(--fg-3); font-size: 11px; }
    .evt-abs { display: grid; grid-template-columns: 1fr 70px 92px auto; gap: 10px; align-items: center; padding: 6px 0; border-top: 1px solid var(--border); }
    .evt-abs__dkp { width: 84px; text-align: right; }
    .evt-history__bulk { display: flex; align-items: center; flex-wrap: wrap; gap: 8px; margin-top: 10px; }
    .evt-history__bulk-q { font-size: 12px; color: var(--fg-2); }
    .evt-history__danger { display: flex; align-items: center; flex-wrap: wrap; gap: 8px; margin-top: 12px; padding-top: 12px; border-top: 1px solid var(--border); }
    .evt-disc { margin-top: 14px; border-top: 1px solid var(--border); padding-top: 10px; }
    .evt-disc__head { font-size: 13px; font-weight: 600; margin-bottom: 8px; }
    .evt-disc__row { display: flex; align-items: baseline; gap: 8px; padding: 5px 0; border-top: 1px solid var(--border); font-size: 13px; }
    .evt-disc__who { display: inline-flex; flex-direction: column; min-width: 96px; }
    .evt-disc__who small { color: var(--fg-3); font-size: 11px; }
    .evt-disc__anon-name { color: var(--fg-3); font-style: italic; }
    .evt-disc__body { flex: 1; white-space: pre-wrap; word-break: break-word; }
    .evt-disc__empty { font-size: 12px; color: var(--fg-3); margin: 4px 0; }
    .evt-disc__compose { margin-top: 8px; }
    .evt-disc__compose textarea { width: 100%; resize: vertical; }
    .evt-disc__actions { display: flex; align-items: center; justify-content: space-between; gap: 10px; margin-top: 6px; }
    .evt-disc__anon { display: inline-flex; align-items: center; gap: 5px; font-size: 12px; color: var(--fg-2); }
  `]
})
export class EventHistoryPanelComponent {
  protected readonly activity = inject(DiscordActivityService);

  protected readonly histories = signal<ActivityEventHistory[]>([]);
  protected readonly canManage = signal(false);
  protected readonly loading = signal(false);
  protected readonly expandedId = signal<number | null>(null);

  // Per-event edit drafts + per-attendee DKP drafts (keyed by id).
  protected readonly edit: Record<number, ActivityEditEventHistoryInput> = {};
  protected readonly dkpDraft: Record<number, number> = {};
  // DKP to grant when adding an absentee, keyed by `${historyId}:${appUserId}`.
  protected readonly absDkpDraft: Record<string, number> = {};

  // Post-event discussion: comments per event + compose drafts.
  protected readonly comments = signal<Record<number, ActivityEventComment[]>>({});
  protected readonly commentDraft: Record<number, string> = {};
  protected readonly anonDraft: Record<number, boolean> = {};

  private _linkshellId = 0;
  @Input() set linkshellId(value: number | null | undefined) {
    const id = value ?? 0;
    if (id === this._linkshellId) return;
    this._linkshellId = id;
    void this.load();
  }
  get linkshellId(): number { return this._linkshellId; }

  async load(): Promise<void> {
    if (!this._linkshellId) { this.histories.set([]); return; }
    this.loading.set(true);
    const res = await this.activity.loadEventHistory(this._linkshellId);
    this.loading.set(false);
    if (res) {
      this.histories.set(res.histories);
      this.canManage.set(res.canManage);
    }
  }

  toggle(h: ActivityEventHistory): void {
    if (this.expandedId() === h.id) { this.expandedId.set(null); return; }
    this.expandedId.set(h.id);
    this.edit[h.id] = {
      eventName: h.eventName ?? null,
      eventType: h.eventType ?? null,
      eventLocation: h.eventLocation ?? null,
      details: null,
      duration: h.duration ?? null,
      dkpPerHour: h.dkpPerHour ?? null
    };
    for (const p of h.participants) { this.dkpDraft[p.id] = p.eventDkp ?? 0; }
    void this.loadComments(h.id);
  }

  protected commentsFor(historyId: number): ActivityEventComment[] {
    return this.comments()[historyId] ?? [];
  }

  // Duration is stored as decimal hours but edited as whole hours / minutes /
  // seconds. Derive a single unit from the draft, and write a unit back by
  // recombining all three into decimal hours (null when the total is zero).
  protected durationPart(draft: ActivityEditEventHistoryInput, unit: 'h' | 'm' | 's'): number {
    const total = Math.max(0, Math.round((draft.duration ?? 0) * 3600));
    if (unit === 'h') return Math.floor(total / 3600);
    if (unit === 'm') return Math.floor((total % 3600) / 60);
    return total % 60;
  }

  protected setDurationPart(draft: ActivityEditEventHistoryInput, unit: 'h' | 'm' | 's', value: string | number): void {
    const n = Math.max(0, Math.floor(Number(value) || 0));
    let h = this.durationPart(draft, 'h');
    let m = this.durationPart(draft, 'm');
    let s = this.durationPart(draft, 's');
    if (unit === 'h') { h = n; } else if (unit === 'm') { m = n; } else { s = n; }
    const totalSeconds = h * 3600 + m * 60 + s;
    draft.duration = totalSeconds > 0 ? totalSeconds / 3600 : null;
  }

  // Step the DKP inputs by this linkshell's rounding increment (Quarter → 0.25,
  // Half → 0.5) so manual edits stay on the same grid as earned DKP.
  protected dkpStep(): number {
    const ls = this.activity.overview()?.linkshells?.find(l => l.id === this._linkshellId);
    return ls?.settings?.dkpRoundingIncrement === 'Half' ? 0.5 : 0.25;
  }

  protected async loadComments(historyId: number): Promise<void> {
    const res = await this.activity.loadEventComments(historyId);
    if (res) { this.comments.update(map => ({ ...map, [historyId]: res.comments })); }
  }

  protected async postComment(h: ActivityEventHistory): Promise<void> {
    const body = (this.commentDraft[h.id] ?? '').trim();
    if (!body) { return; }
    const ok = await this.activity.addEventComment(h.id, body, !!this.anonDraft[h.id]);
    if (ok) {
      this.commentDraft[h.id] = '';
      this.anonDraft[h.id] = false;
      await this.loadComments(h.id);
    }
  }

  protected async removeComment(h: ActivityEventHistory, commentId: number): Promise<void> {
    if (await this.activity.deleteEventComment(commentId)) { await this.loadComments(h.id); }
  }

  async saveEvent(h: ActivityEventHistory): Promise<void> {
    if (await this.activity.editEventHistory(h.id, this.edit[h.id] ?? {})) { await this.load(); }
  }

  async saveDkp(h: ActivityEventHistory, p: ActivityEventHistoryParticipant): Promise<void> {
    if (await this.activity.setEventHistoryParticipantDkp(h.id, p.id, Number(this.dkpDraft[p.id] ?? 0))) { await this.load(); }
  }

  async removeParticipant(h: ActivityEventHistory, p: ActivityEventHistoryParticipant): Promise<void> {
    if (await this.activity.removeEventHistoryParticipant(h.id, p.id)) { await this.load(); }
  }

  async toggleCredit(h: ActivityEventHistory, p: ActivityEventHistoryParticipant, credited: boolean): Promise<void> {
    if (await this.activity.setEventHistoryParticipantActiveCredit(h.id, p.id, credited)) { await this.load(); }
  }

  // Add an absentee to the closed event and grant the entered DKP (wired into the ledger).
  async addAbsentee(h: ActivityEventHistory, a: ActivityEventHistoryAbsentee): Promise<void> {
    const dkp = Number(this.absDkpDraft[`${h.id}:${a.appUserId}`] ?? 0);
    if (await this.activity.addEventHistoryParticipant(h.id, { appUserId: a.appUserId, dkp })) {
      await this.load();
    }
  }

  // Two-step inline confirm (native confirm() is unreliable in the Activity iframe)
  // for undoing active credit across the whole event.
  protected readonly confirmingClearCreditId = signal<number | null>(null);
  protected requestClearActiveCredit(h: ActivityEventHistory): void { this.confirmingClearCreditId.set(h.id); this.confirmingClearAbsencesId.set(null); }
  protected cancelClearActiveCredit(): void { this.confirmingClearCreditId.set(null); }
  async clearActiveCredit(h: ActivityEventHistory): Promise<void> {
    this.confirmingClearCreditId.set(null);
    if (await this.activity.clearEventHistoryActiveCredit(h.id)) { await this.load(); }
  }

  protected readonly confirmingClearAbsencesId = signal<number | null>(null);
  protected requestClearAbsences(h: ActivityEventHistory): void { this.confirmingClearAbsencesId.set(h.id); this.confirmingClearCreditId.set(null); }
  protected cancelClearAbsences(): void { this.confirmingClearAbsencesId.set(null); }
  async clearAbsences(h: ActivityEventHistory): Promise<void> {
    this.confirmingClearAbsencesId.set(null);
    if (await this.activity.clearEventHistoryAbsences(h.id)) { await this.load(); }
  }

  // Delete the whole event (reverses its DKP) — leader/officer only, behind a
  // two-step confirm since it's destructive and can't be auto-undone.
  protected readonly confirmingDeleteId = signal<number | null>(null);
  protected requestDelete(h: ActivityEventHistory): void { this.confirmingDeleteId.set(h.id); }
  protected cancelDelete(): void { this.confirmingDeleteId.set(null); }
  async deleteEvent(h: ActivityEventHistory): Promise<void> {
    this.confirmingDeleteId.set(null);
    if (await this.activity.deleteEventHistory(h.id)) {
      this.expandedId.set(null);
      await this.load();
    }
  }
}
