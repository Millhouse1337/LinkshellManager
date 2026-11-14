import { Injectable, inject, signal } from '@angular/core';

import { ActivityHttpClient } from './activity-http.client';
import { AuthService } from './auth.service';
import { datetimeLocalToUtcIso, formatActionError } from './discord-activity.helpers';
import { mutateThenRefresh } from './mutate-then-refresh';
import type {
  ActivityAddEventMemberInput,
  ActivityCreateEventInput,
  ActivityEditEventHistoryInput,
  ActivityEventAddMemberCandidate,
  ActivityEventCommentsResponse,
  ActivityEventHistoryResponse,
  ActivityEventHistoryWindowsResponse,
  ActivityLootInput,
  ActivityQuickJoinInput
} from './discord-activity.types';

@Injectable({ providedIn: 'root' })
export class EventService {
  private readonly auth = inject(AuthService);
  private readonly http = inject(ActivityHttpClient);

  readonly busyEventId = signal<number | null>(null);

  // Ad-hoc event signup. Slot-level claiming for an event's linked PartySetup
  // is done through PartySetupService.signUpEvent(...) — this method always posts
  // an ad-hoc body (user's chosen Main/Sub/Role).
  async signUpForEvent(
    eventId: number,
    _legacyJobId: number,
    adHocJob?: ActivityQuickJoinInput
  ): Promise<void> {
    this.busyEventId.set(eventId);
    const body: { jobName?: string; subJobName?: string; jobType?: string; characterName?: string } = {};
    if (adHocJob) {
      body.jobName = adHocJob.jobName;
      body.subJobName = adHocJob.subJobName;
      body.jobType = adHocJob.jobType;
      if (adHocJob.characterName) { body.characterName = adHocJob.characterName; }
    }

    try {
      await mutateThenRefresh(this.auth, {
        mutate: () => this.http.postActivityAction(`/api/activity/events/${eventId}/signup`, body),
        refresh: () => this.auth.refreshOverview(),
        success: 'Event signup updated.',
        failure: 'Signing up for the event failed.'
      });
    } finally {
      this.busyEventId.set(null);
    }
  }

  async unsignFromEvent(eventId: number): Promise<void> {
    this.busyEventId.set(eventId);

    try {
      await mutateThenRefresh(this.auth, {
        mutate: () => this.http.postActivityAction(`/api/activity/events/${eventId}/unsign`),
        refresh: () => this.auth.refreshOverview(),
        // Live events park the member in the Break Room (resume where they left off);
        // pre-start events cancel the signup. Neutral wording covers both.
        success: 'You\'ve withdrawn from the event.',
        failure: 'Removing the event signup failed.'
      });
    } finally {
      this.busyEventId.set(null);
    }
  }

  async loadAddMemberCandidates(eventId: number): Promise<ActivityEventAddMemberCandidate[]> {
    this.auth.setActionError(null);

    try {
      return await this.http.fetchActivityJson<ActivityEventAddMemberCandidate[]>(
        `/api/activity/events/${eventId}/add-member-candidates`
      );
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Loading event member candidates failed.'));
      return [];
    }
  }

  async addMemberToLiveEvent(eventId: number, input: ActivityAddEventMemberInput): Promise<void> {
    this.busyEventId.set(eventId);
    try {
      const outcome = await mutateThenRefresh(this.auth, {
        mutate: () => this.http.postActivityAction(`/api/activity/events/${eventId}/members`, {
          appUserId: input.appUserId,
          jobName: input.jobName,
          subJobName: input.subJobName,
          jobType: input.jobType
        }),
        refresh: () => this.auth.refreshOverview(),
        success: 'Member added to the live event.',
        failure: 'Adding the member to the live event failed.'
      });
      if (!outcome.ok) throw outcome.error;
    } finally {
      this.busyEventId.set(null);
    }
  }

  async createEvent(input: ActivityCreateEventInput): Promise<void> {
    const outcome = await mutateThenRefresh(this.auth, {
      mutate: () => this.http.postActivityAction('/api/activity/events', {
        linkshellId: input.linkshellId,
        eventName: input.eventName,
        eventType: input.eventType || null,
        eventLocation: input.eventLocation || null,
        startTimeLocal: datetimeLocalToUtcIso(input.startTimeLocal),
        endTimeLocal: datetimeLocalToUtcIso(input.endTimeLocal),
        duration: input.duration ?? null,
        dkpPerHour: input.dkpPerHour ?? null,
        details: input.details || null,
        partySetupId: input.partySetupId ?? null,
        autoStart: input.autoStart ?? false,
        countsTowardActive: input.countsTowardActive ?? true,
        monsterName: input.monsterName ?? null,
        dayNumber: input.dayNumber ?? null,
        hnmOpenBonusOverride: input.hnmOpenBonusOverride ?? null,
        hnmCloseBonusOverride: input.hnmCloseBonusOverride ?? null,
        hnmClaimBonusOverride: input.hnmClaimBonusOverride ?? null,
        hnmKillBonusOverride: input.hnmKillBonusOverride ?? null,
        hnmPerWindowOverride: input.hnmPerWindowOverride ?? null
      }),
      refresh: () => this.auth.refreshOverview(),
      success: 'Event created.',
      failure: 'Creating the event failed.'
    });
    if (!outcome.ok) throw outcome.error;
  }

  async updateEvent(eventId: number, input: ActivityCreateEventInput): Promise<void> {
    const outcome = await mutateThenRefresh(this.auth, {
      mutate: () => this.http.postActivityAction(`/api/activity/events/${eventId}/update`, {
        linkshellId: input.linkshellId,
        eventName: input.eventName,
        eventType: input.eventType || null,
        eventLocation: input.eventLocation || null,
        startTimeLocal: datetimeLocalToUtcIso(input.startTimeLocal),
        endTimeLocal: datetimeLocalToUtcIso(input.endTimeLocal),
        duration: input.duration ?? null,
        dkpPerHour: input.dkpPerHour ?? null,
        details: input.details || null,
        partySetupId: input.partySetupId ?? null,
        autoStart: input.autoStart ?? false,
        countsTowardActive: input.countsTowardActive ?? true,
        monsterName: input.monsterName ?? null,
        dayNumber: input.dayNumber ?? null,
        hnmOpenBonusOverride: input.hnmOpenBonusOverride ?? null,
        hnmCloseBonusOverride: input.hnmCloseBonusOverride ?? null,
        hnmClaimBonusOverride: input.hnmClaimBonusOverride ?? null,
        hnmKillBonusOverride: input.hnmKillBonusOverride ?? null,
        hnmPerWindowOverride: input.hnmPerWindowOverride ?? null,
        repostLeadHours: input.repostLeadHours ?? null,
        moveTodId: input.moveTodId ?? null
      }),
      refresh: () => this.auth.refreshOverview(),
      success: input.moveTodId ? 'Event and ToD updated.' : 'Event updated.',
      failure: 'Updating the event failed.'
    });
    if (!outcome.ok) throw outcome.error;
  }

  async startEvent(eventId: number, absentParticipantIds?: number[]): Promise<void> {
    this.busyEventId.set(eventId);
    const body =
      absentParticipantIds && absentParticipantIds.length > 0 ? { absentParticipantIds } : undefined;

    try {
      await mutateThenRefresh(this.auth, {
        mutate: () => this.http.postActivityAction(`/api/activity/events/${eventId}/start`, body),
        refresh: () => this.auth.refreshOverview(),
        success: 'Event started.',
        failure: 'Starting the event failed.'
      });
    } finally {
      this.busyEventId.set(null);
    }
  }

  async endEvent(eventId: number): Promise<void> {
    this.busyEventId.set(eventId);
    try {
      await mutateThenRefresh(this.auth, {
        mutate: () => this.http.postActivityAction(`/api/activity/events/${eventId}/end`),
        refresh: () => this.auth.refreshOverview(),
        success: 'Event ended and moved to history.',
        failure: 'Ending the event failed.'
      });
    } finally {
      this.busyEventId.set(null);
    }
  }

  async cancelEvent(eventId: number): Promise<void> {
    this.busyEventId.set(eventId);
    try {
      await mutateThenRefresh(this.auth, {
        mutate: () => this.http.postActivityAction(`/api/activity/events/${eventId}/cancel`),
        refresh: () => this.auth.refreshOverview(),
        success: 'Event canceled.',
        failure: 'Canceling the event failed.'
      });
    } finally {
      this.busyEventId.set(null);
    }
  }

  // Hard-delete an HNM camp outright — no ToD row at all, and the pop cycle keeps no record of it.
  // (End Camp accepts a blank ToD too, but that still logs a "Not entered" row for the camp.)
  // Works on a live camp too, unlike cancelEvent. Server enforces officer + HNM-only.
  async deleteEvent(eventId: number): Promise<void> {
    this.busyEventId.set(eventId);
    try {
      await mutateThenRefresh(this.auth, {
        mutate: () => this.http.postActivityAction(`/api/activity/events/${eventId}/delete`),
        refresh: () => this.auth.refreshOverview(),
        success: 'Camp deleted.',
        failure: 'Deleting the camp failed.'
      });
    } finally {
      this.busyEventId.set(null);
    }
  }

  async takeBreak(eventId: number): Promise<void> {
    this.busyEventId.set(eventId);
    try {
      await mutateThenRefresh(this.auth, {
        mutate: () => this.http.postActivityAction(`/api/activity/events/${eventId}/break`),
        refresh: () => this.auth.refreshOverview(),
        success: 'You are now marked as on break.',
        failure: 'Updating break status failed.'
      });
    } finally {
      this.busyEventId.set(null);
    }
  }

  async returnFromBreak(eventId: number): Promise<void> {
    this.busyEventId.set(eventId);
    try {
      await mutateThenRefresh(this.auth, {
        mutate: () => this.http.postActivityAction(`/api/activity/events/${eventId}/break/return`),
        refresh: () => this.auth.refreshOverview(),
        success: 'You returned from break and are awaiting verification.',
        failure: 'Returning from break failed.'
      });
    } finally {
      this.busyEventId.set(null);
    }
  }

  async sendParticipantToBreak(eventId: number, participantId: number): Promise<void> {
    this.busyEventId.set(eventId);
    try {
      await mutateThenRefresh(this.auth, {
        mutate: () => this.http.postActivityAction(`/api/activity/events/${eventId}/break/force`, {
          participantId
        }),
        refresh: () => this.auth.refreshOverview(),
        success: 'Member moved to the Break Room.',
        failure: 'Sending the member to the Break Room failed.'
      });
    } finally {
      this.busyEventId.set(null);
    }
  }

  async resumeParticipantFromBreak(eventId: number, participantId: number): Promise<void> {
    this.busyEventId.set(eventId);
    try {
      await mutateThenRefresh(this.auth, {
        mutate: () => this.http.postActivityAction(`/api/activity/events/${eventId}/break/resume/force`, {
          participantId
        }),
        refresh: () => this.auth.refreshOverview(),
        success: 'Member resumed from break.',
        failure: 'Resuming the member from break failed.'
      });
    } finally {
      this.busyEventId.set(null);
    }
  }

  async verifyParticipant(
    eventId: number,
    participantId: number,
    isVerified: boolean
  ): Promise<void> {
    this.busyEventId.set(eventId);
    try {
      await mutateThenRefresh(this.auth, {
        mutate: () => this.http.postActivityAction(`/api/activity/events/${eventId}/verify`, {
          participantId,
          isVerified
        }),
        refresh: () => this.auth.refreshOverview(),
        success: isVerified ? 'Participant marked present.' : 'Participant marked absent.',
        failure: 'Updating attendance verification failed.'
      });
    } finally {
      this.busyEventId.set(null);
    }
  }

  async resetParticipantVerification(eventId: number, participantId: number): Promise<void> {
    this.busyEventId.set(eventId);
    try {
      await mutateThenRefresh(this.auth, {
        mutate: () => this.http.postActivityAction(`/api/activity/events/${eventId}/verify/reset`, {
          participantId
        }),
        refresh: () => this.auth.refreshOverview(),
        success: 'Attendance verification reset.',
        failure: 'Resetting attendance verification failed.'
      });
    } finally {
      this.busyEventId.set(null);
    }
  }

  async verifyReturn(eventId: number, ledgerEntryId: number): Promise<void> {
    this.busyEventId.set(eventId);
    try {
      await mutateThenRefresh(this.auth, {
        mutate: () => this.http.postActivityAction(`/api/activity/events/${eventId}/verify-return`, {
          ledgerEntryId
        }),
        refresh: () => this.auth.refreshOverview(),
        success: 'Break return verified.',
        failure: 'Verifying the break return failed.'
      });
    } finally {
      this.busyEventId.set(null);
    }
  }

  async denyReturn(eventId: number, ledgerEntryId: number): Promise<void> {
    this.busyEventId.set(eventId);
    try {
      await mutateThenRefresh(this.auth, {
        mutate: () => this.http.postActivityAction(`/api/activity/events/${eventId}/deny-return`, {
          ledgerEntryId
        }),
        refresh: () => this.auth.refreshOverview(),
        success: 'Break return denied.',
        failure: 'Denying the break return failed.'
      });
    } finally {
      this.busyEventId.set(null);
    }
  }

  async addLoot(eventId: number, input: ActivityLootInput): Promise<void> {
    this.busyEventId.set(eventId);
    try {
      const outcome = await mutateThenRefresh(this.auth, {
        mutate: () => this.http.postActivityAction(`/api/activity/events/${eventId}/loot`, {
          itemName: input.itemName,
          itemWinner: input.itemWinner || null,
          winningDkpSpent: input.winningDkpSpent ?? null
        }),
        refresh: () => this.auth.refreshOverview(),
        success: 'Loot entry added.',
        failure: 'Adding loot failed.'
      });
      if (!outcome.ok) throw outcome.error;
    } finally {
      this.busyEventId.set(null);
    }
  }

  async quickJoinLiveEvent(eventId: number, input: ActivityQuickJoinInput): Promise<void> {
    this.busyEventId.set(eventId);
    try {
      const outcome = await mutateThenRefresh(this.auth, {
        mutate: () => this.http.postActivityAction(`/api/activity/events/${eventId}/quick-join`, {
          jobName: input.jobName,
          subJobName: input.subJobName,
          jobType: input.jobType,
          characterName: input.characterName || undefined
        }),
        refresh: () => this.auth.refreshOverview(),
        success: 'Live event join added.',
        failure: 'Joining the live event failed.'
      });
      if (!outcome.ok) throw outcome.error;
    } finally {
      this.busyEventId.set(null);
    }
  }

  // ----- Past event (EventHistory) browse + edit -----

  // Reloaded by the refresh timer while Past Events is open, so it reports in its own notice and
  // never clears or sets the action banner (see AuthService.loadError).
  async loadEventHistory(linkshellId: number): Promise<ActivityEventHistoryResponse | null> {
    try {
      const history = await this.http.fetchActivityJson<ActivityEventHistoryResponse>(
        `/api/activity/event-history?linkshellId=${linkshellId}`
      );
      this.auth.clearLoadError('event-history');
      return history;
    } catch (error) {
      this.auth.reportLoadError('event-history', error, 'Loading event history failed.');
      return null;
    }
  }

  // The archived attendance windows for one closed camp. Loaded on demand rather than with the
  // history list: a page of 25-window wyrm camps carries more roster rows than the whole list.
  async loadEventHistoryWindows(id: number): Promise<ActivityEventHistoryWindowsResponse | null> {
    this.auth.setActionError(null);
    try {
      return await this.http.fetchActivityJson<ActivityEventHistoryWindowsResponse>(
        `/api/activity/event-history/${id}/windows`
      );
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Loading the attendance windows failed.'));
      return null;
    }
  }

  async editEventHistory(id: number, input: ActivityEditEventHistoryInput): Promise<boolean> {
    const outcome = await mutateThenRefresh(this.auth, {
      mutate: () => this.http.postActivityAction(`/api/activity/event-history/${id}/edit`, input),
      refresh: () => this.auth.refreshOverview(),
      success: 'Event updated.',
      failure: 'Updating the event failed.'
    });
    return outcome.ok;
  }

  async deleteEventHistory(id: number): Promise<boolean> {
    const outcome = await mutateThenRefresh(this.auth, {
      mutate: () => this.http.postActivityAction(`/api/activity/event-history/${id}/delete`, {}),
      refresh: () => this.auth.refreshOverview(),
      success: 'Event deleted — its DKP was reversed.',
      failure: 'Deleting the event failed.'
    });
    return outcome.ok;
  }

  async setEventHistoryParticipantDkp(id: number, participantId: number, amount: number): Promise<boolean> {
    const outcome = await mutateThenRefresh(this.auth, {
      mutate: () => this.http.postActivityAction(
        `/api/activity/event-history/${id}/participants/${participantId}/dkp`,
        { amount }
      ),
      refresh: () => this.auth.refreshOverview(),
      success: 'Attendee DKP updated.',
      failure: 'Updating attendee DKP failed.'
    });
    return outcome.ok;
  }

  // Add a member to a closed event after the fact + grant DKP (wired into the ledger/balance).
  async addEventHistoryParticipant(
    id: number,
    input: { appUserId: string; dkp: number; jobType?: string | null; jobName?: string | null; subJobName?: string | null }
  ): Promise<boolean> {
    const outcome = await mutateThenRefresh(this.auth, {
      mutate: () => this.http.postActivityAction(`/api/activity/event-history/${id}/participants/add`, input),
      refresh: () => this.auth.refreshOverview(),
      success: 'Member added to the event and DKP granted.',
      failure: 'Adding the member failed.'
    });
    return outcome.ok;
  }

  async setEventHistoryParticipantActiveCredit(id: number, participantId: number, credited: boolean): Promise<boolean> {
    const outcome = await mutateThenRefresh(this.auth, {
      mutate: () => this.http.postActivityAction(
        `/api/activity/event-history/${id}/participants/${participantId}/active-credit`,
        { credited }
      ),
      refresh: () => this.auth.refreshOverview(),
      success: 'Active credit updated.',
      failure: 'Updating active credit failed.'
    });
    return outcome.ok;
  }

  // Undo active credit for every attendee of an event (credited by accident).
  async clearEventHistoryActiveCredit(id: number): Promise<boolean> {
    const outcome = await mutateThenRefresh(this.auth, {
      mutate: () => this.http.postActivityAction(`/api/activity/event-history/${id}/active-credit/clear`, {}),
      refresh: () => this.auth.refreshOverview(),
      success: 'Active credit removed for the whole event.',
      failure: 'Removing active credit failed.'
    });
    return outcome.ok;
  }

  async clearEventHistoryAbsences(id: number): Promise<boolean> {
    const outcome = await mutateThenRefresh(this.auth, {
      mutate: () => this.http.postActivityAction(`/api/activity/event-history/${id}/absences/clear`, {}),
      refresh: () => this.auth.refreshOverview(),
      success: 'Absences undone — this event no longer counts toward active tracking.',
      failure: 'Undoing absences failed.'
    });
    return outcome.ok;
  }

  async removeEventHistoryParticipant(id: number, participantId: number): Promise<boolean> {
    const outcome = await mutateThenRefresh(this.auth, {
      mutate: () => this.http.postActivityAction(
        `/api/activity/event-history/${id}/participants/${participantId}/remove`
      ),
      refresh: () => this.auth.refreshOverview(),
      success: 'Attendee removed and DKP refunded.',
      failure: 'Removing the attendee failed.'
    });
    return outcome.ok;
  }

  // ----- Post-event discussion -----

  async loadEventComments(historyId: number): Promise<ActivityEventCommentsResponse | null> {
    this.auth.setActionError(null);
    try {
      return await this.http.fetchActivityJson<ActivityEventCommentsResponse>(
        `/api/activity/event-history/${historyId}/comments`
      );
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Loading the discussion failed.'));
      return null;
    }
  }

  async addEventComment(historyId: number, body: string, isAnonymous: boolean): Promise<boolean> {
    this.auth.setActionError(null);
    try {
      await this.http.postActivityAction(`/api/activity/event-history/${historyId}/comments`, { body, isAnonymous });
      return true;
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Posting the comment failed.'));
      return false;
    }
  }

  async deleteEventComment(commentId: number): Promise<boolean> {
    this.auth.setActionError(null);
    try {
      await this.http.postActivityAction(`/api/activity/event-history/comments/${commentId}/delete`);
      return true;
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Deleting the comment failed.'));
      return false;
    }
  }

  // Removes a single AppUserEventWindow row (a participant's attendance for one
  // posted HNM window). The server cleans up the matching ledger entry too.
  async removeAttendanceWindowAttendee(attendeeId: number): Promise<boolean> {
    this.auth.setActionError(null);
    try {
      await this.http.sendActivityRequest('DELETE', `/api/addon/management/window-attendees/${attendeeId}`);
      return true;
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Removing the attendee failed.'));
      return false;
    }
  }

  // Deletes a whole posted window on a live camp (its attendee rows and their ledger entries go
  // with it). The server refuses an ended camp and, on an Open/Close camp, the Open while the Close
  // is filed, and returns why.
  async deleteAttendanceWindow(windowId: number): Promise<boolean> {
    this.auth.setActionError(null);
    try {
      await this.http.sendActivityRequest('DELETE', `/api/addon/management/windows/${windowId}`);
      return true;
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Deleting the window failed.'));
      return false;
    }
  }

  // Prices ONE posted attendance window: what it pays each attendee, replacing the camp's default
  // (the open on window 1, the regular rate elsewhere). The close bonus still adds on top when the
  // window is the close. `null` clears the price and puts the window back on the default — a real
  // instruction, which is why it is sent rather than omitted.
  //
  // Same route family and the same bearer + cookie shape as the remove above; the server refuses
  // camps that don't pay off this column (Manual Check In, timed events) and returns the reason,
  // which formatActionError surfaces verbatim.
  async setAttendanceWindowDkp(windowId: number, dkpAmount: number | null): Promise<boolean> {
    this.auth.setActionError(null);
    try {
      await this.http.sendActivityRequest('PATCH', `/api/addon/management/attendance-windows/${windowId}/dkp`, { dkpAmount });
      return true;
    } catch (error) {
      this.auth.setActionError(formatActionError(error, "Setting this window's DKP failed."));
      return false;
    }
  }

  // Sets this CAMP's kill bonus -- what the Kill tab's "Kill DKP" box edits.
  //
  // A separate call from setAttendanceWindowDkp on purpose, because it writes a different column
  // for a different reason: the kill roster is worth 0 as a WINDOW and pays through the camp's
  // kill bonus at End Camp, so pricing the window would stack a payment on top of the bonus. The
  // window route refuses a kill window outright; this one writes Event.HnmKillBonusOverride.
  //
  // null clears the override and puts the camp back on the linkshell's kill bonus.
  async setCampKillBonus(eventId: number, killBonus: number | null): Promise<boolean> {
    this.auth.setActionError(null);
    try {
      await this.http.sendActivityRequest('PATCH', `/api/addon/management/events/${eventId}/kill-bonus`, { killBonus });
      return true;
    } catch (error) {
      this.auth.setActionError(formatActionError(error, "Setting the camp's kill bonus failed."));
      return false;
    }
  }

  // Sets this CAMP's close bonus -- what the Closing window cell on the ticked window edits. A camp
  // has one close, so its bonus is a property of the camp: this writes Event.HnmCloseBonusOverride.
  //
  // null clears the override and puts the camp back on the linkshell's close bonus.
  async setCampCloseBonus(eventId: number, closeBonus: number | null): Promise<boolean> {
    this.auth.setActionError(null);
    try {
      await this.http.sendActivityRequest('PATCH', `/api/addon/management/events/${eventId}/close-bonus`, { closeBonus });
      return true;
    } catch (error) {
      this.auth.setActionError(formatActionError(error, "Setting the camp's close bonus failed."));
      return false;
    }
  }

  // Add New Post (always a Misc post): files a new Misc post on the camp with the picked members.
  // dkpAmount is the post's own price per member; null leaves it on the linkshell's Misc post rate.
  async addCampMiscPost(eventId: number, name: string | null, appUserIds: string[], dkpAmount: number | null): Promise<boolean> {
    this.auth.setActionError(null);
    try {
      await this.http.sendActivityRequest('POST', `/api/addon/management/events/${eventId}/misc-posts`, { name, appUserIds, dkpAmount });
      return true;
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Adding the misc post failed.'));
      return false;
    }
  }

  // Sets the camp's OPEN bonus — what window 1 adds on top of its own amount. null puts the camp
  // back on the linkshell's open bonus.
  async setCampOpenBonus(eventId: number, openBonus: number | null): Promise<boolean> {
    this.auth.setActionError(null);
    try {
      await this.http.sendActivityRequest('PATCH', `/api/addon/management/events/${eventId}/open-bonus`, { openBonus });
      return true;
    } catch (error) {
      this.auth.setActionError(formatActionError(error, "Setting the camp's open bonus failed."));
      return false;
    }
  }

  // Prices ONE misc post linked to a live camp: what it pays each person on it, replacing the
  // linkshell's Misc post rate. null puts it back on that rate.
  async setMiscPostDkp(snapshotId: number, dkpAmount: number | null): Promise<boolean> {
    this.auth.setActionError(null);
    try {
      await this.http.sendActivityRequest('PATCH', `/api/addon/management/attendance-snapshots/${snapshotId}/dkp`, { dkpAmount });
      return true;
    } catch (error) {
      this.auth.setActionError(formatActionError(error, "Setting this misc post's DKP failed."));
      return false;
    }
  }

  // Undoes linking a capture to a live camp: it goes back to Unlinked Snapshots, unfiled, and stops
  // paying anyone on the camp.
  async unlinkCampSnapshot(snapshotId: number): Promise<boolean> {
    this.auth.setActionError(null);
    try {
      await this.http.sendActivityRequest('POST', `/api/activity/window-events/snapshots/${snapshotId}/unlink`);
      return true;
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Unlinking this snapshot failed.'));
      return false;
    }
  }

  // Deletes a snapshot linked to a live camp, entries and all (the same endpoint the Attendance
  // tab's Delete uses). Returns false, with the error shown, when the server refuses.
  async deleteCampSnapshot(snapshotId: number): Promise<boolean> {
    this.auth.setActionError(null);
    try {
      await this.http.sendActivityRequest('POST', `/api/activity/window-events/snapshots/${snapshotId}/delete`);
      return true;
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Deleting this snapshot failed.'));
      return false;
    }
  }

  // Marks (or unmarks) ONE posted window as the camp's close. On a numbered camp that is the only
  // thing that pays the close bonus, which adds to whatever the window already pays.
  //
  // The server enforces the one-close-per-camp rule, which is why this cannot be a client-side flag
  // flip — see AddonApiController.SetClosingWindowAsync.
  async setAttendanceWindowClosing(windowId: number, isClosingWindow: boolean): Promise<boolean> {
    this.auth.setActionError(null);
    try {
      await this.http.sendActivityRequest('PATCH', `/api/addon/management/attendance-windows/${windowId}/closing`, { isClosingWindow });
      return true;
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Marking the closing window failed.'));
      return false;
    }
  }
}
