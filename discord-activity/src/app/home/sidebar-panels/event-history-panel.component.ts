import { DatePipe } from '@angular/common';
import { Component, Input, computed, effect, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';

import { AutoRefreshService } from '../../discord/auto-refresh.service';
import { DiscordActivityService } from '../../discord/discord-activity.service';
import type {
  ActivityEditEventHistoryInput,
  ActivityEventComment,
  ActivityEventHistory,
  ActivityEventHistoryAbsentee,
  ActivityEventHistoryParticipant,
  ActivityEventHistoryTagRoster,
  ActivityEventHistoryWindow,
  ActivityEventHistoryWindowsResponse
} from '../../discord/discord-activity.types';

// Past (closed) event browser + editor for the Activity. Mirrors the web
// EventHistory page: anyone can view; leaders/officers can edit metadata/DKP,
// correct an attendee's DKP/active-credit, remove an attendee (refunds DKP), or
// add a roster member who missed it (grants DKP). Embedded in the Event System
// tab; loads for the given linkshell.
@Component({
  selector: 'app-event-history-panel',
  standalone: true,
  imports: [FormsModule, DatePipe],
  template: `
    <div class="evt-history">
      <!-- No Refresh button of its own: the header's Refresh drives this panel too, via the
           refreshSignal effect below. -->
      <div class="evt-history__head">
        <h3>Past events</h3>
      </div>

      @if (!loading() && histories().length === 0) {
        <p class="notice">No completed events yet.</p>
      }

      @if (histories().length > 0) {
        <div class="evt-tools">
          <span class="evt-search">
            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><circle cx="11" cy="11" r="7"/><path d="m21 21-4.3-4.3"/></svg>
            <input type="search" name="evtSearch" [ngModel]="search()" (ngModelChange)="onSearch($event)"
                   placeholder="Search by name, type or location…" aria-label="Search past events" />
          </span>
          <span class="evt-tally">
            {{ filtered().length }} of {{ histories().length }}
          </span>
        </div>
      }

      @if (histories().length > 0 && filtered().length === 0) {
        <p class="notice">No past events match “{{ search() }}”.</p>
      }

      <!-- Fixed-height scroller: a linkshell accumulates hundreds of closed events, and the
           page itself shouldn't grow without bound. Paged underneath so the scroller never
           holds more than one page. -->
      <div class="evt-scroll" [class.is-empty]="pageItems().length === 0">
      @for (h of pageItems(); track h.id) {
        <div class="event-card" [class.event-card--open]="expandedId() === h.id">
          <button type="button" class="event-head" (click)="toggle(h)">
            <span class="event-title">
              <span class="ico">
                <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="4" width="18" height="18" rx="2"/><path d="M16 2v4M8 2v4M3 10h18"/></svg>
              </span>
              <strong>{{ h.eventName || 'Event' }}</strong>
            </span>

            <span class="event-meta">
              <span class="chip">
                <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="4" width="18" height="18" rx="2"/><path d="M16 2v4M8 2v4M3 10h18"/></svg>
                {{ h.endTime ? (h.endTime | date:'MMM d, y') : 'Completed' }}
              </span>
              <span class="chip">
                <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M16 21v-1a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v1"/><circle cx="9" cy="7" r="4"/></svg>
                {{ h.participants.length }} {{ h.participants.length === 1 ? 'attendee' : 'attendees' }}
              </span>
              <!-- Hidden on an HNM: those camps are priced per WINDOW (open / close / kill /
                   claim bonuses) and never by the hour, so the chip reads "0 DKP/h" on a camp
                   that paid perfectly well. Mirrors Views/EventHistory/Details.cshtml. -->
              @if (!isHnm(h)) {
                <span class="chip">
                  <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/></svg>
                  {{ h.dkpPerHour ?? 0 }} DKP/h
                </span>
              }
              <!-- Only a windowed camp has one, so it doubles as the "this was an HNM" marker in
                   the collapsed list. -->
              @if ((h.archivedWindowCount ?? 0) > 0) {
                <span class="chip chip--win" title="Attendance windows this camp posted. Open the card to see each window's roster.">
                  <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="3" width="18" height="18" rx="2"/><path d="M3 9h18M9 21V9"/></svg>
                  {{ h.archivedWindowCount }} {{ h.archivedWindowCount === 1 ? 'window' : 'windows' }}
                </span>
              }
            </span>

            <span class="badge">Past Event</span>
            <span class="chevron" [class.is-open]="expandedId() === h.id" aria-hidden="true">
              <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"><path d="m6 9 6 6 6-6"/></svg>
            </span>
          </button>

          @if (expandedId() === h.id) {
            <div class="event-body">
              @if (canManage() && edit[h.id]; as draft) {
                <section class="panel">
                  <h3>
                    <span class="ico"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="4" width="18" height="18" rx="2"/><path d="M16 2v4M8 2v4M3 10h18"/></svg></span>
                    Event Details
                  </h3>
                  <div class="form-grid">
                    <label>Name<input [(ngModel)]="draft.eventName" [name]="'eName' + h.id" /></label>
                    <label>Type<input [(ngModel)]="draft.eventType" [name]="'eType' + h.id" /></label>
                    <!-- Starts a new row so it sits directly left of Duration. Location and
                         Duration are what an officer corrects together — where the camp was and
                         how long it ran — while Name and Type are what identifies it. -->
                    <label class="row-start">Location<input [(ngModel)]="draft.eventLocation" [name]="'eLoc' + h.id" /></label>
                    <label>Duration
                      <div class="duration">
                        <input type="number" min="0" [value]="durationPart(draft, 'h')" (change)="setDurationPart(draft, 'h', $any($event.target).value)" [name]="'eDurH' + h.id" /><span>h</span>
                        <input type="number" min="0" max="59" [value]="durationPart(draft, 'm')" (change)="setDurationPart(draft, 'm', $any($event.target).value)" [name]="'eDurM' + h.id" /><span>m</span>
                        <input type="number" min="0" max="59" [value]="durationPart(draft, 's')" (change)="setDurationPart(draft, 's', $any($event.target).value)" [name]="'eDurS' + h.id" /><span>s</span>
                      </div>
                    </label>
                    <!-- Omitted on an HNM rather than disabled. draft.dkpPerHour is still
                         seeded from the stored rate in toggle(), so Save Event round-trips the
                         existing value unchanged instead of rescaling every attendee's DKP. -->
                    @if (!isHnm(h)) {
                      <label>DKP per hour<input type="number" [(ngModel)]="draft.dkpPerHour" [name]="'eRate' + h.id" /></label>
                    }
                    <button type="button" class="primary" (click)="saveEvent(h)">
                      <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M19 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h11l5 5v11a2 2 0 0 1-2 2z"/><path d="M17 21v-8H7v8M7 3v5h8"/></svg>
                      Save Event
                    </button>
                  </div>
                  @if (!isHnm(h)) {
                    <p class="hint">Changing DKP per hour rescales every attendee's earned DKP and balance.</p>
                  } @else {
                    <p class="hint">This camp pays per window, not by the hour.</p>
                  }
                </section>
              }

              <!-- HNM camps archive the attendance windows they posted; a timed event has none,
                   and neither does a camp closed before the archive existed. Contents load on
                   expand rather than with the list — a wyrm camp is 25 windows of roster. -->
              @if ((h.archivedWindowCount ?? 0) > 0) {
                <section class="panel">
                  <div class="section-head">
                    <h3>
                      <span class="ico"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="3" width="18" height="18" rx="2"/><path d="M3 9h18M9 21V9"/></svg></span>
                      Attendance Windows <em class="count">{{ h.archivedWindowCount }}</em>
                    </h3>
                    @if (windowArchiveFor(h.id); as archive) {
                      <span class="tag" title="Distinct characters scanned across every window. Can exceed the attendee list — the addon records who was standing there, not who joined on the site.">
                        {{ archive.distinctAttendeeCount }} scanned
                      </span>
                    }
                  </div>
                  <p class="hint" style="margin-bottom:12px">
                    What this camp actually posted, kept from before the event closed. A DKP figure
                    shows only where an officer priced that window explicitly — the camp's own
                    open/close bonuses aren't recoverable once the board is gone.
                  </p>
                  @if (windowsLoading() === h.id) {
                    <p class="hint">Loading windows…</p>
                  } @else if (windowsFor(h.id).length === 0) {
                    <p class="hint">No window record survived for this event.</p>
                  } @else {
                    <div class="win-list">
                      @for (w of windowsFor(h.id); track w.id) {
                        <div class="win">
                          <button type="button" class="win-head" (click)="toggleWindow(h.id, w.id)">
                            <strong>{{ w.label }}</strong>
                            <span class="tag">{{ w.sequenceNumber }} of {{ windowCountFor(h.id) }}</span>
                            @if (w.isClosingWindow) {
                              <span class="tag accent" title="The window the officer marked as closing the camp out — the one the close bonus paid on.">Closing</span>
                            }
                            <!-- No "Kill" chip: the row's own label already reads Kill, so the chip
                                 said the same word twice. Closing above is not the same case — a
                                 closing window is numbered and named like any other, so nothing
                                 else on the row says the close bonus paid on it. -->
                            @if (w.dkpAmount != null) {
                              <span class="tag success">{{ w.dkpAmount }} DKP</span>
                            }
                            <span class="win-meta">
                              {{ w.attendees.length }} present · {{ w.postedAt | date:'MMM d, h:mm a' }}
                            </span>
                            <span class="win-chev" [class.is-open]="isWindowOpen(h.id, w.id)" aria-hidden="true">
                              <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"><path d="m6 9 6 6 6-6"/></svg>
                            </span>
                          </button>
                          @if (isWindowOpen(h.id, w.id)) {
                            <div class="win-body">
                              @if (w.attendees.length === 0) {
                                <span class="muted">Nobody was scanned in this window.</span>
                              } @else {
                                @for (a of w.attendees; track $index) {
                                  <span class="win-att" [title]="a.zone || 'Zone not recorded'">
                                    {{ a.characterName }}
                                    @if (a.mainCharacterName) { <small>alt of {{ a.mainCharacterName }}</small> }
                                  </span>
                                }
                              }
                            </div>
                          }
                        </div>
                      }
                      <!-- The Tag roster, listed with the windows because that is where an officer
                           looks for "what did this camp record" — but it is NOT one of them, and
                           none of the counts above include it. -->
                      @if (tagRosterFor(h.id); as tags) {
                        <div class="win">
                          <button type="button" class="win-head" (click)="toggleWindow(h.id, tagRosterRowId)">
                            <strong>Tag</strong>
                            <span class="tag" title="Who landed an action on the mob, from this camp's Claim Shield. Earns the tag bonus; not a window, so it counts toward no window total.">not a window</span>
                            <span class="win-meta">
                              {{ tags.taggers.length }} tagged · {{ tags.postedAt | date:'MMM d, h:mm a' }}
                            </span>
                            <span class="win-chev" [class.is-open]="isWindowOpen(h.id, tagRosterRowId)" aria-hidden="true">
                              <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"><path d="m6 9 6 6 6-6"/></svg>
                            </span>
                          </button>
                          @if (isWindowOpen(h.id, tagRosterRowId)) {
                            <div class="win-body">
                              @for (t of tags.taggers; track t.characterName) {
                                <span class="win-att">{{ t.characterName }}</span>
                              }
                            </div>
                          }
                        </div>
                      }
                    </div>
                  }
                </section>
              }

              @if (canManage()) {
                <div class="split">
                  <section class="panel">
                    <div class="section-head">
                      <h3>
                        <span class="ico"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M16 21v-1a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v1"/><circle cx="9" cy="7" r="4"/><path d="M22 21v-1a4 4 0 0 0-3-3.85"/><path d="M16 3.13a4 4 0 0 1 0 7.75"/></svg></span>
                        Attendees <em class="count">{{ h.participants.length }}</em>
                      </h3>
                      <div class="section-head__actions">
                        @if (confirmingClearCreditId() === h.id) {
                          <span class="confirm-q">Remove active credit from all {{ h.participants.length }} attendees?</span>
                          <button type="button" class="danger sm" (click)="clearActiveCredit(h)">Confirm</button>
                          <button type="button" class="ghost sm" (click)="cancelClearActiveCredit()">Cancel</button>
                        } @else if (confirmingClearAbsencesId() === h.id) {
                          <span class="confirm-q">Stop this event counting toward active tracking?</span>
                          <button type="button" class="danger sm" (click)="clearAbsences(h)">Confirm</button>
                          <button type="button" class="ghost sm" (click)="cancelClearAbsences()">Cancel</button>
                        } @else {
                          <button type="button" class="ghost sm" (click)="requestClearActiveCredit(h)"
                                  title="Uncheck active credit for every attendee — for an event credited by accident.">↶ Undo Active Credit</button>
                          <button type="button" class="ghost sm" (click)="requestClearAbsences(h)"
                                  title="Stop this event counting toward active tracking so members who missed it aren't marked absent for it.">⊘ Undo Absences</button>
                        }
                      </div>
                    </div>

                    <div class="table-wrap">
                      <table class="evt-table">
                        <thead>
                          <tr>
                            <th>Attendee</th>
                            <th>Job</th>
                            @if (showWindowsColumn(h)) {
                              <th class="ctr" title="Attendance windows this member was scanned in. On a windowed camp this — not the duration — is what their DKP was computed from.">Windows</th>
                            }
                            <th class="ctr">Active Credit</th>
                            <th class="ctr">Absent</th>
                            <th>DKP Awarded</th>
                            <th></th>
                          </tr>
                        </thead>
                        <tbody>
                          @for (p of h.participants; track p.id) {
                            <tr>
                              <td class="evt-who">
                                <span class="avatar" [style.background]="avatarBg(p.characterName)">{{ initial(p.characterName) }}</span>
                                {{ p.characterName }}
                                @if (p.altNames?.length) { <small class="alts">{{ p.altNames!.join(', ') }}</small> }
                              </td>
                              <td>
                                @if (p.jobName) { <span class="job">{{ p.jobName }}@if (p.subJobName) {/{{ p.subJobName }}}</span> }
                                @else { <span class="muted">—</span> }
                              </td>
                              @if (showWindowsColumn(h)) {
                                <td class="ctr">
                                  @if (p.windowsAttended != null) {
                                    <span class="win-count">{{ p.windowsAttended }} of {{ windowCountFor(h.id) }}</span>
                                  } @else {
                                    <span class="muted">—</span>
                                  }
                                </td>
                              }
                              <td class="ctr">
                                <input type="checkbox" [checked]="p.activeCredit !== false"
                                       (change)="toggleCredit(h, p, $any($event.target).checked)"
                                       title="Active-status credit for this event" />
                              </td>
                              <td class="ctr">
                                <button type="button" class="toggle" [class.is-on]="p.activeCredit === false"
                                        (click)="toggleCredit(h, p, p.activeCredit === false)"
                                        title="Absent = no active credit (counts toward the inactive threshold)"></button>
                              </td>
                              <td>
                                <input class="dkp" type="number" [step]="dkpStep()" min="0"
                                       [(ngModel)]="dkpDraft[p.id]" [name]="'pDkp' + p.id" />
                              </td>
                              <td class="evt-actions">
                                <button type="button" class="mini" (click)="saveDkp(h, p)">Save</button>
                                <button type="button" class="danger" (click)="removeParticipant(h, p)">Remove</button>
                              </td>
                            </tr>
                          }
                        </tbody>
                      </table>
                    </div>
                  </section>

                  <section class="panel">
                    <h3>
                      <span class="ico"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M16 21v-1a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v1"/><circle cx="9" cy="7" r="4"/><path d="M22 21v-1a4 4 0 0 0-3-3.85"/><path d="M16 3.13a4 4 0 0 1 0 7.75"/></svg></span>
                      Absent Members <em class="count">{{ h.absentees?.length ?? 0 }}</em>
                    </h3>
                    @if ((h.absentees?.length ?? 0) === 0) {
                      <div class="empty">
                        <span class="empty__ico"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"><path d="M16 21v-1a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v1"/><circle cx="9" cy="7" r="4"/><path d="M22 21v-1a4 4 0 0 0-3-3.85"/><path d="M16 3.13a4 4 0 0 1 0 7.75"/></svg></span>
                        <strong>No absent members</strong>
                        <span>All attendees were present.</span>
                      </div>
                    } @else {
                      <p class="hint" style="margin-top:0;margin-bottom:12px">Roster members who missed this event. Add one to credit them &amp; grant DKP.</p>
                      <div class="absent-list">
                        @for (a of h.absentees!; track a.appUserId) {
                          <div class="absent-row">
                            <span class="avatar sm" [style.background]="avatarBg(a.characterName)">{{ initial(a.characterName) }}</span>
                            <span class="absent-row__who">
                              <span class="absent-name">{{ a.characterName || 'Member' }}</span>
                              @if (a.altNames?.length) { <small class="alts">{{ a.altNames!.join(', ') }}</small> }
                            </span>
                            <button type="button" class="primary mini" (click)="addAbsentee(h, a)">Add to Event</button>
                          </div>
                        }
                      </div>
                    }
                  </section>
                </div>
              } @else {
                <section class="panel">
                  <h3>
                    <span class="ico"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M16 21v-1a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v1"/><circle cx="9" cy="7" r="4"/><path d="M22 21v-1a4 4 0 0 0-3-3.85"/><path d="M16 3.13a4 4 0 0 1 0 7.75"/></svg></span>
                    Attendees <em class="count">{{ h.participants.length }}</em>
                  </h3>
                  @if (h.participants.length === 0) {
                    <p class="hint">No attendees recorded.</p>
                  } @else {
                    <div class="ro-list">
                      @for (p of h.participants; track p.id) {
                        <div class="ro-row">
                          <span class="evt-who">
                            <span class="avatar sm" [style.background]="avatarBg(p.characterName)">{{ initial(p.characterName) }}</span>
                            {{ p.characterName }}
                            @if (p.altNames?.length) { <small class="alts">{{ p.altNames!.join(', ') }}</small> }
                            <small>{{ p.jobName }}@if (p.subJobName) {/{{ p.subJobName }}}</small>
                            @if (p.windowsAttended != null) {
                              <small>{{ p.windowsAttended }} of {{ windowCountFor(h.id) }} windows</small>
                            }
                          </span>
                          <span class="ro-dkp">
                            <span class="tag" [class.success]="p.activeCredit !== false">{{ p.activeCredit !== false ? 'Credited' : 'No credit' }}</span>
                            {{ p.eventDkp ?? 0 }}
                          </span>
                        </div>
                      }
                    </div>
                  }
                </section>
              }

              <section class="panel discussion">
                <h3>
                  <span class="ico"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 11.5a8.38 8.38 0 0 1-.9 3.8 8.5 8.5 0 0 1-7.6 4.7 8.38 8.38 0 0 1-3.8-.9L3 21l1.9-5.7a8.38 8.38 0 0 1-.9-3.8 8.5 8.5 0 0 1 4.7-7.6 8.38 8.38 0 0 1 3.8-.9h.5a8.48 8.48 0 0 1 8 8v.5z"/></svg></span>
                  Discussion <em class="count">{{ commentsFor(h.id).length }}</em>
                </h3>
                @for (c of commentsFor(h.id); track c.id) {
                  <div class="disc-row">
                    <span class="disc-who">
                      <strong [class.disc-anon]="c.isAnonymous">{{ c.author }}</strong>
                      <small>{{ c.createdAt | date:'MMM d, h:mm a' }}</small>
                    </span>
                    <span class="disc-body">{{ c.body }}</span>
                    @if (c.canDelete) {
                      <button type="button" class="ghost sm disc-del" (click)="removeComment(h, c.id)" title="Delete">✕</button>
                    }
                  </div>
                }
                @if (commentsFor(h.id).length === 0) {
                  <p class="hint">No comments yet — start the discussion.</p>
                }
                <textarea rows="2" [(ngModel)]="commentDraft[h.id]" [name]="'cd' + h.id"
                          placeholder="Add to the discussion…" maxlength="2000"></textarea>
                <div class="discussion-foot">
                  <label class="anon"><input type="checkbox" [(ngModel)]="anonDraft[h.id]" [name]="'ca' + h.id" /> Post anonymously</label>
                  <button type="button" class="primary" (click)="postComment(h)">
                    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="m22 2-7 20-4-9-9-4Z"/><path d="M22 2 11 13"/></svg>
                    Post
                  </button>
                </div>
              </section>

              @if (canManage()) {
                <div class="delete-bar">
                  @if (confirmingDeleteId() === h.id) {
                    <span class="confirm-q">Delete this event permanently? Reverses all DKP it awarded/spent and removes its attendance, loot &amp; comments — can't be undone.</span>
                    <button type="button" class="danger" (click)="deleteEvent(h)">Confirm delete</button>
                    <button type="button" class="ghost sm" (click)="cancelDelete()">Cancel</button>
                  } @else {
                    <button type="button" class="delete" (click)="requestDelete(h)" title="Permanently delete this event and reverse its DKP.">
                      <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 6h18M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2m3 0v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6"/></svg>
                      Delete Event
                    </button>
                  }
                </div>
              }
            </div>
          }
        </div>
      }
      </div>

      @if (totalPages() > 1) {
        <div class="evt-pager">
          <button type="button" class="ghost sm" [disabled]="currentPage() === 1" (click)="goToPage(currentPage() - 1)">‹ Prev</button>
          <span class="evt-pager__at">Page {{ currentPage() }} of {{ totalPages() }}</span>
          <button type="button" class="ghost sm" [disabled]="currentPage() === totalPages()" (click)="goToPage(currentPage() + 1)">Next ›</button>
        </div>
      }
    </div>
  `,
  styles: [`
    .evt-history { margin-top: 12px; }
    .evt-history__head { display: flex; align-items: center; justify-content: space-between; gap: 8px; margin-bottom: 10px; }
    .evt-history__head h3 { margin: 0; font-size: 14px; }

    /* Search + tally */
    .evt-tools { display: flex; align-items: center; gap: 10px; margin-bottom: 10px; flex-wrap: wrap; }
    .evt-search {
      display: inline-flex; align-items: center; gap: 8px; flex: 1 1 220px; min-width: 0;
      padding: 0 11px; border: 1px solid var(--border); border-radius: 8px; background: var(--surface);
    }
    .evt-search:focus-within { border-color: var(--accent); }
    .evt-search svg { width: 15px; height: 15px; flex: 0 0 auto; color: var(--fg-3); }
    /* Beat the component-wide input rule (full-width, own border) — the wrapper owns the frame. */
    .evt-search input {
      flex: 1 1 auto; min-width: 0; width: auto;
      border: 0; background: transparent; padding: 9px 0;
    }
    .evt-search input:focus { outline: none; border: 0; }
    .evt-search input::-webkit-search-cancel-button { cursor: pointer; }
    .evt-tally { color: var(--fg-3); font-size: 12px; white-space: nowrap; }

    /* Scroller — one page of cards at a time, so the panel keeps a stable height however many
       events a linkshell accumulates. Framed and inset so it reads as a bounded container
       rather than the page simply running off the bottom. */
    .evt-scroll {
      max-height: 840px; overflow-y: auto; overscroll-behavior: contain;
      padding: 12px; border: 1px solid var(--border); border-radius: 12px;
      background: rgba(0, 0, 0, .18);
    }
    .evt-scroll.is-empty { display: none; }
    .evt-scroll > .event-card:last-child { margin-bottom: 0; }
    /* Slim scrollbar so the frame doesn't gain a chunky native gutter. */
    .evt-scroll::-webkit-scrollbar { width: 10px; }
    .evt-scroll::-webkit-scrollbar-thumb { background: var(--border-2); border-radius: 999px; border: 3px solid transparent; background-clip: content-box; }
    .evt-scroll::-webkit-scrollbar-thumb:hover { background: var(--fg-4); background-clip: content-box; }

    /* Pager */
    .evt-pager { display: flex; align-items: center; justify-content: center; gap: 12px; margin-top: 10px; }
    .evt-pager__at { color: var(--fg-3); font-size: 12px; min-width: 92px; text-align: center; }
    .evt-pager button:disabled { opacity: .45; cursor: default; }
    .evt-pager button:disabled:hover { background: rgba(79, 124, 255, .10); }

    /* Reset / shared buttons */
    button { font: inherit; }
    .ghost, .mini {
      border: 1px solid rgba(79, 124, 255, .35); background: rgba(79, 124, 255, .10);
      color: #cdd9ff; border-radius: 8px; padding: 7px 12px; cursor: pointer;
      display: inline-flex; align-items: center; gap: 6px; white-space: nowrap;
      transition: background .12s ease, border-color .12s ease;
    }
    .ghost:hover, .mini:hover { background: rgba(79, 124, 255, .18); }
    .ghost.sm, .mini.sm { padding: 5px 10px; font-size: 12px; }
    .mini { padding: 6px 12px; font-size: 12.5px; }
    .danger {
      color: var(--danger); background: var(--danger-weak);
      border: 1px solid rgba(255, 107, 107, .35); border-radius: 8px; padding: 7px 12px;
      cursor: pointer; display: inline-flex; align-items: center; gap: 6px; white-space: nowrap;
    }
    .danger:hover { background: rgba(255, 107, 107, .18); }
    .danger.sm { padding: 5px 10px; font-size: 12px; }
    .primary {
      background: linear-gradient(180deg, var(--accent), var(--accent-strong)); border: 0; color: #fff;
      border-radius: 8px; padding: 10px 16px; font-weight: 700; cursor: pointer;
      display: inline-flex; align-items: center; gap: 7px; white-space: nowrap;
      box-shadow: 0 6px 16px rgba(79, 124, 255, .28);
    }
    .primary:hover { filter: brightness(1.06); }
    .primary.mini { padding: 7px 13px; font-size: 12.5px; box-shadow: none; }
    .primary svg, .danger svg, .ghost svg, .mini svg { width: 15px; height: 15px; flex: 0 0 auto; }

    /* Card */
    .event-card {
      margin-bottom: 16px; padding: 18px;
      background: linear-gradient(180deg, var(--bg-elev), var(--bg));
      border: 1px solid var(--border-2); border-radius: 12px;
      box-shadow: 0 12px 30px rgba(0, 0, 0, .4);
    }
    .event-head {
      display: flex; align-items: center; gap: 14px; width: 100%;
      background: transparent; border: 0; padding: 0; color: var(--fg); cursor: pointer; text-align: left;
    }
    .event-title { display: inline-flex; align-items: center; gap: 10px; min-width: 150px; font-size: 17px; }
    .event-title strong { font-weight: 700; }
    .ico { color: var(--accent); display: inline-flex; }
    .ico svg { width: 18px; height: 18px; }
    .event-meta { display: flex; gap: 8px; flex: 1; flex-wrap: wrap; }
    .chip {
      display: inline-flex; align-items: center; gap: 6px; font-size: 12px; color: var(--fg-3);
      border: 1px solid var(--border); background: rgba(255, 255, 255, .03); border-radius: 8px; padding: 6px 10px;
    }
    .chip svg { width: 13px; height: 13px; opacity: .85; }
    .chip--win { color: #cdd9ff; border-color: rgba(79, 124, 255, .35); background: var(--accent-weak); }
    .badge {
      color: #cdd9ff; background: var(--accent-weak); border: 1px solid rgba(79, 124, 255, .35);
      border-radius: 8px; padding: 6px 12px; font-size: 12px; font-weight: 600; white-space: nowrap;
    }
    .chevron {
      width: 34px; height: 34px; flex: 0 0 auto; border-radius: 50%; color: var(--fg);
      background: var(--bg-elev); border: 1px solid var(--border);
      display: inline-flex; align-items: center; justify-content: center; transition: transform .18s ease;
    }
    .chevron svg { width: 18px; height: 18px; }
    .chevron.is-open { transform: rotate(180deg); }

    /* Ceiling for the Attendees / Absent Members lists — see .table-wrap. */
    .event-body { margin-top: 16px; --list-h: 30rem; }

    /* Panels */
    .panel {
      padding: 16px; border: 1px solid var(--border); border-radius: 10px;
      background: rgba(255, 255, 255, .015); margin-bottom: 14px;
    }
    .panel:last-child { margin-bottom: 0; }
    .panel h3 { margin: 0 0 14px; font-size: 16px; display: inline-flex; align-items: center; gap: 9px; }
    .count {
      font-style: normal; color: var(--accent); background: var(--accent-weak);
      border-radius: 999px; padding: 1px 9px; font-size: 12px; font-weight: 600;
    }
    .hint { color: var(--fg-3); margin: 10px 0 0; font-size: 12px; }
    .muted { color: var(--fg-3); }

    /* Inputs */
    label { display: grid; gap: 6px; color: var(--fg-3); font-size: 12px; }
    input, select, textarea {
      width: 100%; color: var(--fg); background: var(--surface); border: 1px solid var(--border);
      border-radius: 8px; padding: 9px 11px; font: inherit;
    }
    input:focus, select:focus, textarea:focus { outline: none; border-color: var(--accent); }
    input[type=checkbox] { width: auto; accent-color: var(--accent); cursor: pointer; }

    .form-grid { display: grid; grid-template-columns: 1fr 1fr 1fr; gap: 14px 20px; align-items: end; }
    /* Breaks the row: auto-placement can't use column 1 of a row that already has a field in it,
       so this lands on the next one. */
    .form-grid > .row-start { grid-column: 1; }
    /* Pinned to the last column rather than merely right-aligned. Without it the button follows
       whatever field precedes it, so adding or dropping the DKP-per-hour field (which an HNM camp
       does not render) moved Save into a different column. */
    .form-grid .primary { grid-column: 3; justify-self: end; }
    .duration { display: grid; grid-template-columns: 1fr auto 1fr auto 1fr auto; gap: 6px; align-items: center; }
    .duration input { text-align: right; }
    .duration span { color: var(--fg-3); font-size: 12px; }

    /* Split */
    /* margin-bottom matches .panel's: the grid zeroes the margin on the panels inside it, so
       without one here the Discussion panel below sits flush against the split. */
    .split { display: grid; grid-template-columns: 1.25fr .75fr; gap: 14px; margin-bottom: 14px; }
    .split:last-child { margin-bottom: 0; }
    .split .panel { margin-bottom: 0; }
    .section-head { display: flex; align-items: center; justify-content: space-between; gap: 12px; flex-wrap: wrap; }
    .section-head h3 { margin: 0; }
    .section-head__actions { display: inline-flex; align-items: center; gap: 8px; flex-wrap: wrap; }
    .confirm-q { font-size: 12px; color: var(--fg-2); }

    /* Table */
    /* Attendees and Absent Members each scroll inside --list-h instead of growing
       without bound: a 169-member absent list ran the card thousands of pixels tall
       and dragged Attendees with it, since a grid row is as tall as its tallest item.
       max-height (not height) so a small event still collapses to just its rows. */
    .table-wrap { margin-top: 12px; overflow: auto; max-height: var(--list-h); }
    .evt-table { width: 100%; border-collapse: collapse; }
    .evt-table th, .evt-table td {
      padding: 10px 8px; border-top: 1px solid rgba(255, 255, 255, .06); color: var(--fg-3);
      text-align: left; vertical-align: middle;
    }
    /* Pinned while the rows scroll under it. Needs an opaque background (the panel's
       is translucent) and an inset line, since border-collapse drops sticky borders. */
    .evt-table thead th {
      border-top: 0; font-size: 11px; text-transform: uppercase; letter-spacing: .04em;
      position: sticky; top: 0; z-index: 1;
      background: var(--surface-2); box-shadow: inset 0 -1px 0 rgba(255, 255, 255, .06);
    }
    .evt-table th.ctr, .evt-table td.ctr { text-align: center; }
    .evt-who { color: var(--fg); font-weight: 600; white-space: nowrap; }
    .avatar {
      display: inline-flex; align-items: center; justify-content: center;
      width: 28px; height: 28px; margin-right: 10px; border-radius: 50%; vertical-align: middle;
      font-size: 12px; font-weight: 700; color: #fff; text-shadow: 0 1px 2px rgba(0,0,0,.4);
    }
    .avatar.sm { width: 24px; height: 24px; margin-right: 8px; font-size: 11px; }
    .job {
      display: inline-block; padding: 3px 9px; border-radius: 999px; font-size: 11.5px; font-weight: 600;
      color: #cdd9ff; background: var(--accent-weak); border: 1px solid rgba(79, 124, 255, .28);
    }
    .dkp { max-width: 96px; text-align: right; }
    .evt-actions { white-space: nowrap; text-align: right; }
    .evt-actions .danger { margin-left: 6px; }

    /* Toggle switch (Absent) */
    .toggle {
      display: inline-block; width: 42px; height: 22px; border-radius: 999px; background: var(--surface-3);
      border: 0; position: relative; cursor: pointer; vertical-align: middle; padding: 0; transition: background .15s ease;
    }
    .toggle::after {
      content: ""; position: absolute; width: 18px; height: 18px; top: 2px; left: 3px;
      border-radius: 50%; background: var(--fg); transition: left .15s ease;
    }
    .toggle.is-on { background: var(--accent); }
    .toggle.is-on::after { left: 21px; }
    span.toggle { cursor: default; opacity: .85; }

    /* Absent Members panel */
    .empty {
      min-height: 180px; border: 1px dashed var(--border); border-radius: 10px;
      display: grid; place-content: center; justify-items: center; gap: 6px; text-align: center; color: var(--fg-3);
    }
    .empty__ico { color: var(--fg-4); }
    .empty__ico svg { width: 30px; height: 30px; }
    .empty strong { color: var(--fg-2); font-weight: 600; }
    /* padding-right keeps the scrollbar off the "Add to Event" buttons. */
    .absent-list {
      display: flex; flex-direction: column; gap: 8px;
      overflow-y: auto; max-height: var(--list-h); padding-right: 4px;
    }
    .absent-row { display: flex; align-items: center; gap: 10px; padding: 9px 10px; border: 1px solid var(--border); border-radius: 8px; background: rgba(255,255,255,.015); }
    .absent-row__who { display: flex; align-items: center; gap: 7px; flex: 1; min-width: 0; flex-wrap: wrap; }
    .absent-name { color: var(--fg); font-weight: 600; }
    .absent-row .primary { flex: 0 0 auto; }
    .alts { color: var(--fg-3); font-size: 11px; font-weight: 400; }

    /* Read-only attendees (non-managers) — same card, so the same ceiling. */
    .ro-list { display: flex; flex-direction: column; overflow-y: auto; max-height: var(--list-h); }
    .ro-row { display: flex; align-items: center; justify-content: space-between; gap: 10px; padding: 8px 0; border-top: 1px solid rgba(255,255,255,.06); }
    .ro-row:first-child { border-top: 0; }
    .ro-row .evt-who small { color: var(--fg-3); margin-left: 6px; font-weight: 400; }
    .ro-dkp { display: inline-flex; align-items: center; gap: 8px; color: var(--fg-2); }
    .tag { font-size: 11px; padding: 2px 8px; border-radius: 999px; border: 1px solid var(--border); color: var(--fg-3); }
    .tag.success { color: var(--success); border-color: rgba(67,209,122,.4); background: var(--success-weak); }
    .tag.accent { color: #cdd9ff; border-color: rgba(79,124,255,.4); background: var(--accent-weak); }
    .tag.warn { color: var(--warning); border-color: rgba(245,158,11,.4); background: var(--warning-weak); }

    /* Archived attendance windows. One collapsible row per posted window; the roster inside is a
       name-chip wrap rather than a table, because a wyrm camp is 25 of these and 25 nested tables
       would bury the rest of the card. */
    .win-list { display: flex; flex-direction: column; gap: 6px; }
    .win { border: 1px solid var(--border); border-radius: 8px; background: rgba(255, 255, 255, .015); }
    .win-head {
      display: flex; align-items: center; gap: 8px; flex-wrap: wrap; width: 100%;
      background: transparent; border: 0; color: var(--fg); text-align: left; cursor: pointer;
      padding: 9px 12px; font: inherit;
    }
    .win-head:hover { background: rgba(79, 124, 255, .06); border-radius: 8px; }
    .win-head strong { font-weight: 600; font-size: 13px; }
    .win-meta { margin-left: auto; color: var(--fg-3); font-size: 12px; white-space: nowrap; }
    .win-chev { display: inline-flex; color: var(--fg-3); transition: transform .18s ease; }
    .win-chev svg { width: 15px; height: 15px; }
    .win-chev.is-open { transform: rotate(180deg); }
    .win-body {
      display: flex; flex-wrap: wrap; gap: 6px;
      padding: 9px 12px; border-top: 1px solid var(--border);
    }
    .win-att {
      font-size: 12px; padding: 3px 9px; border-radius: 999px;
      border: 1px solid var(--border); background: var(--surface); color: var(--fg);
    }
    .win-att small { color: var(--fg-4); margin-left: 5px; }
    .win-count { font-variant-numeric: tabular-nums; font-size: 12.5px; white-space: nowrap; }

    /* Discussion */
    .discussion p { color: var(--fg-3); }
    .disc-row { display: flex; align-items: baseline; gap: 10px; padding: 8px 0; border-top: 1px solid rgba(255,255,255,.06); font-size: 13px; }
    .disc-who { display: inline-flex; flex-direction: column; min-width: 110px; }
    .disc-who small { color: var(--fg-3); font-size: 11px; }
    .disc-anon { color: var(--fg-3); font-style: italic; }
    .disc-body { flex: 1; white-space: pre-wrap; word-break: break-word; color: var(--fg); }
    .disc-del { flex: 0 0 auto; }
    .discussion textarea { min-height: 70px; resize: vertical; margin-top: 10px; }
    .discussion-foot { display: flex; align-items: center; justify-content: space-between; gap: 10px; margin-top: 10px; }
    .anon { display: inline-flex; align-items: center; gap: 6px; color: var(--fg-3); font-size: 12px; }
    .anon input { margin: 0; }

    /* Delete */
    .delete-bar { display: flex; align-items: center; flex-wrap: wrap; gap: 8px; margin-top: 14px; }
    .delete {
      color: var(--danger); background: var(--danger-weak); border: 1px solid rgba(255, 107, 107, .35);
      border-radius: 8px; padding: 9px 14px; font-weight: 700; cursor: pointer;
      display: inline-flex; align-items: center; gap: 7px;
    }
    .delete:hover { background: rgba(255, 107, 107, .18); }
    .delete svg { width: 15px; height: 15px; }

    @media (max-width: 860px) {
      .form-grid, .split { grid-template-columns: 1fr; }
      /* One column, so the two column pins above have nothing to pin to — column 3 of a 1-column
         grid is an implicit track, which would push Save off to the side of everything else. */
      .form-grid > .row-start, .form-grid .primary { grid-column: auto; }
      .event-head { flex-wrap: wrap; }
    }
  `]
})
export class EventHistoryPanelComponent {
  protected readonly activity = inject(DiscordActivityService);
  private readonly autoRefresh = inject(AutoRefreshService);

  // Null until the effect's first run. Closed-event history is NOT part of the overview payload, so
  // the header Refresh used to leave this panel stale — which is why it carried a Refresh button of
  // its own. Reloading off refreshSignal covers the header button, the background timer and the
  // live change-feed alike, so the local button is redundant and gone.
  private lastRefreshTick: number | null = null;

  constructor() {
    effect(() => {
      const tick = this.autoRefresh.refreshSignal();
      const isFirstRun = this.lastRefreshTick === null;
      this.lastRefreshTick = tick;
      // Skip the first run: the linkshellId input setter owns the initial load, and reloading here
      // as well would fire two identical requests on every mount.
      if (isFirstRun || !this._linkshellId) return;
      queueMicrotask(() => void this.load());
    });
  }

  protected readonly histories = signal<ActivityEventHistory[]>([]);
  protected readonly canManage = signal(false);
  protected readonly loading = signal(false);
  protected readonly expandedId = signal<number | null>(null);

  // ----- Search + pagination -----
  // The server hands back the linkshell's whole closed-event history in one call, so both are
  // client-side over that list. Search covers the three fields shown on the collapsed row.
  // Deliberately small: a page has to fit the scroller without the pager being a rarity only
  // linkshells with 10+ closed events ever see. Search is what narrows a long history.
  private static readonly PageSize = 5;
  protected readonly search = signal('');
  private readonly requestedPage = signal(1);

  protected readonly filtered = computed(() => {
    const query = this.search().trim().toLowerCase();
    const all = this.histories();
    if (!query) { return all; }
    return all.filter(h =>
      (h.eventName ?? '').toLowerCase().includes(query)
      || (h.eventType ?? '').toLowerCase().includes(query)
      || (h.eventLocation ?? '').toLowerCase().includes(query));
  });

  protected readonly totalPages = computed(() =>
    Math.max(1, Math.ceil(this.filtered().length / EventHistoryPanelComponent.PageSize)));

  // Clamped rather than stored: filtering down to fewer pages while sitting on a high page
  // would otherwise strand the user on an empty view.
  protected readonly currentPage = computed(() =>
    Math.min(Math.max(1, this.requestedPage()), this.totalPages()));

  protected readonly pageItems = computed(() => {
    const start = (this.currentPage() - 1) * EventHistoryPanelComponent.PageSize;
    return this.filtered().slice(start, start + EventHistoryPanelComponent.PageSize);
  });

  protected onSearch(value: string): void {
    this.search.set(value ?? '');
    this.requestedPage.set(1);
  }

  protected goToPage(page: number): void {
    this.requestedPage.set(Math.min(Math.max(1, page), this.totalPages()));
    // An event expanded on the page we just left would otherwise stay open behind the scenes
    // and re-appear when the user pages back — collapse so each page starts clean.
    this.expandedId.set(null);
  }

  // Per-event edit drafts + per-attendee DKP drafts (keyed by id).
  protected readonly edit: Record<number, ActivityEditEventHistoryInput> = {};
  protected readonly dkpDraft: Record<number, number> = {};

  // Archived attendance windows per closed camp, fetched on expand. A key present with an empty
  // window list means "asked, nothing there" — distinct from an absent key ("not asked yet"), so
  // a camp with no surviving record isn't re-requested every time it's opened.
  protected readonly windowArchives = signal<Record<number, ActivityEventHistoryWindowsResponse>>({});
  protected readonly windowsLoading = signal<number | null>(null);
  // Which window rows have their roster expanded, keyed "historyId:windowId" so two cards can't
  // share open state.
  protected readonly openWindows = signal<Record<string, boolean>>({});

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

  // An HNM camp is paid per window by HnmStandardCampFinalizer / WdCampFinalizer -- open, close,
  // kill and claim bonuses -- and DkpPerHour is never read for one. Every DKP/hour surface on the
  // card is gated on this so it does not advertise a rate nobody set. Same test as the Razor
  // page's `isHnm` in Views/EventHistory/Details.cshtml; the two must move together.
  protected isHnm(h: ActivityEventHistory): boolean {
    return (h.eventType ?? '').trim().toLowerCase() === 'hnm';
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
    if ((h.archivedWindowCount ?? 0) > 0) { void this.loadWindows(h.id); }
  }

  // ----- Archived attendance windows -----

  protected windowArchiveFor(historyId: number): ActivityEventHistoryWindowsResponse | null {
    return this.windowArchives()[historyId] ?? null;
  }

  protected windowsFor(historyId: number): ActivityEventHistoryWindow[] {
    return this.windowArchives()[historyId]?.windows ?? [];
  }

  protected tagRosterFor(historyId: number): ActivityEventHistoryTagRoster | null {
    return this.windowArchives()[historyId]?.tagRoster ?? null;
  }

  // The Tag row shares the windows' open/closed map, so it needs an id no real window can hold.
  // Window ids are database keys and always positive.
  protected readonly tagRosterRowId = -1;

  // The denominator for "3 of 25". Falls back to the archived count while the fetch is still in
  // flight so the attendee column doesn't flash a nonsense "3 of 0".
  protected windowCountFor(historyId: number): number {
    const archive = this.windowArchives()[historyId];
    if (archive && archive.windowCount > 0) return archive.windowCount;
    return this.histories().find(h => h.id === historyId)?.archivedWindowCount ?? 0;
  }

  // The Windows column only earns its place on a windowed camp. Keyed off the event rather than
  // off any one attendee: a member who was scanned in zero windows still needs the cell, or the
  // row would come up short a column.
  protected showWindowsColumn(h: ActivityEventHistory): boolean {
    return (h.archivedWindowCount ?? 0) > 0 || h.participants.some(p => p.windowsAttended != null);
  }

  protected isWindowOpen(historyId: number, windowId: number): boolean {
    return this.openWindows()[`${historyId}:${windowId}`] === true;
  }

  protected toggleWindow(historyId: number, windowId: number): void {
    const key = `${historyId}:${windowId}`;
    this.openWindows.update(map => ({ ...map, [key]: !map[key] }));
  }

  private async loadWindows(historyId: number): Promise<void> {
    if (this.windowArchives()[historyId]) return;   // already fetched, empty result included
    this.windowsLoading.set(historyId);
    const res = await this.activity.loadEventHistoryWindows(historyId);
    this.windowsLoading.set(null);
    if (res) { this.windowArchives.update(map => ({ ...map, [historyId]: res })); }
  }

  protected initial(name?: string | null): string {
    const n = (name ?? '').trim();
    return n ? n.charAt(0).toUpperCase() : '?';
  }

  // Deterministic per-name avatar gradient so each member keeps a stable colour.
  protected avatarBg(name?: string | null): string {
    const n = (name ?? '').trim();
    let hash = 0;
    for (let i = 0; i < n.length; i++) { hash = (hash * 31 + n.charCodeAt(i)) % 360; }
    return `linear-gradient(135deg, hsl(${hash} 62% 55%), hsl(${(hash + 38) % 360} 58% 38%))`;
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

  // Add an absentee to the closed event, granting the event's standard earned DKP
  // (duration × rate; the server rounds it) so one click credits them like an
  // attendee. The amount can be fine-tuned afterward in the Attendees table, where
  // they now appear with an editable DKP field. Wired into the ledger server-side.
  async addAbsentee(h: ActivityEventHistory, a: ActivityEventHistoryAbsentee): Promise<void> {
    const dkp = Math.max(0, (h.duration ?? 0) * (h.dkpPerHour ?? 0));
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
