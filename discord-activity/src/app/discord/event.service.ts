import { Injectable, inject, signal } from '@angular/core';

import { ActivityHttpClient } from './activity-http.client';
import { AuthService } from './auth.service';
import { datetimeLocalToUtcIso, formatActionError } from './discord-activity.helpers';
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
  // is done through PartySetupService.signUp(...) — this method always posts
  // an ad-hoc body (user's chosen Main/Sub/Role).
  async signUpForEvent(
    eventId: number,
    _legacyJobId: number,
    adHocJob?: ActivityQuickJoinInput
  ): Promise<void> {
    this.busyEventId.set(eventId);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);

    try {
      const body: { jobName?: string; subJobName?: string; jobType?: string; characterName?: string } = {};
      if (adHocJob) {
        body.jobName = adHocJob.jobName;
        body.subJobName = adHocJob.subJobName;
        body.jobType = adHocJob.jobType;
        if (adHocJob.characterName) { body.characterName = adHocJob.characterName; }
      }
      await this.http.postActivityAction(`/api/activity/events/${eventId}/signup`, body);
      await this.auth.refreshOverview();
      this.auth.setActionMessage('Event signup updated.');
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Signing up for the event failed.'));
    } finally {
      this.busyEventId.set(null);
    }
  }

  async unsignFromEvent(eventId: number): Promise<void> {
    this.busyEventId.set(eventId);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);

    try {
      await this.http.postActivityAction(`/api/activity/events/${eventId}/unsign`);
      await this.auth.refreshOverview();
      // Live events park the member in the Break Room (resume where they left off);
      // pre-start events cancel the signup. Neutral wording covers both.
      this.auth.setActionMessage('You\'ve withdrawn from the event.');
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Removing the event signup failed.'));
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
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);

    try {
      await this.http.postActivityAction(`/api/activity/events/${eventId}/members`, {
        appUserId: input.appUserId,
        jobName: input.jobName,
        subJobName: input.subJobName,
        jobType: input.jobType
      });
      await this.auth.refreshOverview();
      this.auth.setActionMessage('Member added to the live event.');
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Adding the member to the live event failed.'));
      throw error;
    } finally {
      this.busyEventId.set(null);
    }
  }

  async createEvent(input: ActivityCreateEventInput): Promise<void> {
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);

    try {
      await this.http.postActivityAction('/api/activity/events', {
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
        repeatOnTod: input.repeatOnTod ?? null,
        repostLeadHours: input.repostLeadHours ?? null,
        dayNumber: input.dayNumber ?? null,
        hnmOpenBonusOverride: input.hnmOpenBonusOverride ?? null,
        hnmCloseBonusOverride: input.hnmCloseBonusOverride ?? null,
        hnmClaimBonusOverride: input.hnmClaimBonusOverride ?? null,
        hnmKillBonusOverride: input.hnmKillBonusOverride ?? null,
        hnmPerWindowOverride: input.hnmPerWindowOverride ?? null
      });

      await this.auth.refreshOverview();
      this.auth.setActionMessage('Event created.');
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Creating the event failed.'));
      throw error;
    }
  }

  async updateEvent(eventId: number, input: ActivityCreateEventInput): Promise<void> {
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);

    try {
      await this.http.postActivityAction(`/api/activity/events/${eventId}/update`, {
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
        repeatOnTod: input.repeatOnTod ?? null,
        repostLeadHours: input.repostLeadHours ?? null,
        dayNumber: input.dayNumber ?? null,
        hnmOpenBonusOverride: input.hnmOpenBonusOverride ?? null,
        hnmCloseBonusOverride: input.hnmCloseBonusOverride ?? null,
        hnmClaimBonusOverride: input.hnmClaimBonusOverride ?? null,
        hnmKillBonusOverride: input.hnmKillBonusOverride ?? null,
        hnmPerWindowOverride: input.hnmPerWindowOverride ?? null
      });

      await this.auth.refreshOverview();
      this.auth.setActionMessage('Event updated.');
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Updating the event failed.'));
      throw error;
    }
  }

  async startEvent(eventId: number, absentParticipantIds?: number[]): Promise<void> {
    this.busyEventId.set(eventId);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);

    try {
      const body =
        absentParticipantIds && absentParticipantIds.length > 0 ? { absentParticipantIds } : undefined;
      await this.http.postActivityAction(`/api/activity/events/${eventId}/start`, body);
      await this.auth.refreshOverview();
      this.auth.setActionMessage('Event started.');
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Starting the event failed.'));
    } finally {
      this.busyEventId.set(null);
    }
  }

  async endEvent(eventId: number): Promise<void> {
    this.busyEventId.set(eventId);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);

    try {
      await this.http.postActivityAction(`/api/activity/events/${eventId}/end`);
      await this.auth.refreshOverview();
      this.auth.setActionMessage('Event ended and moved to history.');
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Ending the event failed.'));
    } finally {
      this.busyEventId.set(null);
    }
  }

  async cancelEvent(eventId: number): Promise<void> {
    this.busyEventId.set(eventId);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);

    try {
      await this.http.postActivityAction(`/api/activity/events/${eventId}/cancel`);
      await this.auth.refreshOverview();
      this.auth.setActionMessage('Event canceled.');
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Canceling the event failed.'));
    } finally {
      this.busyEventId.set(null);
    }
  }

  // Hard-delete an HNM camp outright — no ToD row at all, and the pop cycle keeps no record of it.
  // (End Camp accepts a blank ToD too, but that still logs a "Not entered" row for the camp.)
  // Works on a live camp too, unlike cancelEvent. Server enforces officer + HNM-only.
  async deleteEvent(eventId: number): Promise<void> {
    this.busyEventId.set(eventId);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);

    try {
      await this.http.postActivityAction(`/api/activity/events/${eventId}/delete`);
      await this.auth.refreshOverview();
      this.auth.setActionMessage('Camp deleted.');
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Deleting the camp failed.'));
    } finally {
      this.busyEventId.set(null);
    }
  }

  async takeBreak(eventId: number): Promise<void> {
    this.busyEventId.set(eventId);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);

    try {
      await this.http.postActivityAction(`/api/activity/events/${eventId}/break`);
      await this.auth.refreshOverview();
      this.auth.setActionMessage('You are now marked as on break.');
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Updating break status failed.'));
    } finally {
      this.busyEventId.set(null);
    }
  }

  async returnFromBreak(eventId: number): Promise<void> {
    this.busyEventId.set(eventId);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);

    try {
      await this.http.postActivityAction(`/api/activity/events/${eventId}/break/return`);
      await this.auth.refreshOverview();
      this.auth.setActionMessage('You returned from break and are awaiting verification.');
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Returning from break failed.'));
    } finally {
      this.busyEventId.set(null);
    }
  }

  async sendParticipantToBreak(eventId: number, participantId: number): Promise<void> {
    this.busyEventId.set(eventId);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);

    try {
      await this.http.postActivityAction(`/api/activity/events/${eventId}/break/force`, {
        participantId
      });
      await this.auth.refreshOverview();
      this.auth.setActionMessage('Member moved to the Break Room.');
    } catch (error) {
      this.auth.setActionError(
        formatActionError(error, 'Sending the member to the Break Room failed.')
      );
    } finally {
      this.busyEventId.set(null);
    }
  }

  async resumeParticipantFromBreak(eventId: number, participantId: number): Promise<void> {
    this.busyEventId.set(eventId);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);

    try {
      await this.http.postActivityAction(`/api/activity/events/${eventId}/break/resume/force`, {
        participantId
      });
      await this.auth.refreshOverview();
      this.auth.setActionMessage('Member resumed from break.');
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Resuming the member from break failed.'));
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
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);

    try {
      await this.http.postActivityAction(`/api/activity/events/${eventId}/verify`, {
        participantId,
        isVerified
      });
      await this.auth.refreshOverview();
      this.auth.setActionMessage(
        isVerified ? 'Participant marked present.' : 'Participant marked absent.'
      );
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Updating attendance verification failed.'));
    } finally {
      this.busyEventId.set(null);
    }
  }

  async resetParticipantVerification(eventId: number, participantId: number): Promise<void> {
    this.busyEventId.set(eventId);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);

    try {
      await this.http.postActivityAction(`/api/activity/events/${eventId}/verify/reset`, {
        participantId
      });
      await this.auth.refreshOverview();
      this.auth.setActionMessage('Attendance verification reset.');
    } catch (error) {
      this.auth.setActionError(
        formatActionError(error, 'Resetting attendance verification failed.')
      );
    } finally {
      this.busyEventId.set(null);
    }
  }

  async verifyReturn(eventId: number, ledgerEntryId: number): Promise<void> {
    this.busyEventId.set(eventId);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);

    try {
      await this.http.postActivityAction(`/api/activity/events/${eventId}/verify-return`, {
        ledgerEntryId
      });
      await this.auth.refreshOverview();
      this.auth.setActionMessage('Break return verified.');
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Verifying the break return failed.'));
    } finally {
      this.busyEventId.set(null);
    }
  }

  async denyReturn(eventId: number, ledgerEntryId: number): Promise<void> {
    this.busyEventId.set(eventId);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);

    try {
      await this.http.postActivityAction(`/api/activity/events/${eventId}/deny-return`, {
        ledgerEntryId
      });
      await this.auth.refreshOverview();
      this.auth.setActionMessage('Break return denied.');
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Denying the break return failed.'));
    } finally {
      this.busyEventId.set(null);
    }
  }

  async addLoot(eventId: number, input: ActivityLootInput): Promise<void> {
    this.busyEventId.set(eventId);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);

    try {
      await this.http.postActivityAction(`/api/activity/events/${eventId}/loot`, {
        itemName: input.itemName,
        itemWinner: input.itemWinner || null,
        winningDkpSpent: input.winningDkpSpent ?? null
      });
      await this.auth.refreshOverview();
      this.auth.setActionMessage('Loot entry added.');
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Adding loot failed.'));
      throw error;
    } finally {
      this.busyEventId.set(null);
    }
  }

  async quickJoinLiveEvent(eventId: number, input: ActivityQuickJoinInput): Promise<void> {
    this.busyEventId.set(eventId);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);

    try {
      await this.http.postActivityAction(`/api/activity/events/${eventId}/quick-join`, {
        jobName: input.jobName,
        subJobName: input.subJobName,
        jobType: input.jobType,
        characterName: input.characterName || undefined
      });
      await this.auth.refreshOverview();
      this.auth.setActionMessage('Live event join added.');
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Joining the live event failed.'));
      throw error;
    } finally {
      this.busyEventId.set(null);
    }
  }

  // ----- Past event (EventHistory) browse + edit -----

  async loadEventHistory(linkshellId: number): Promise<ActivityEventHistoryResponse | null> {
    this.auth.setActionError(null);
    try {
      return await this.http.fetchActivityJson<ActivityEventHistoryResponse>(
        `/api/activity/event-history?linkshellId=${linkshellId}`
      );
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Loading event history failed.'));
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
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);
    try {
      await this.http.postActivityAction(`/api/activity/event-history/${id}/edit`, input);
      await this.auth.refreshOverview();
      this.auth.setActionMessage('Event updated.');
      return true;
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Updating the event failed.'));
      return false;
    }
  }

  async deleteEventHistory(id: number): Promise<boolean> {
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);
    try {
      await this.http.postActivityAction(`/api/activity/event-history/${id}/delete`, {});
      await this.auth.refreshOverview();
      this.auth.setActionMessage('Event deleted — its DKP was reversed.');
      return true;
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Deleting the event failed.'));
      return false;
    }
  }

  async setEventHistoryParticipantDkp(id: number, participantId: number, amount: number): Promise<boolean> {
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);
    try {
      await this.http.postActivityAction(
        `/api/activity/event-history/${id}/participants/${participantId}/dkp`,
        { amount }
      );
      await this.auth.refreshOverview();
      this.auth.setActionMessage('Attendee DKP updated.');
      return true;
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Updating attendee DKP failed.'));
      return false;
    }
  }

  // Add a member to a closed event after the fact + grant DKP (wired into the ledger/balance).
  async addEventHistoryParticipant(
    id: number,
    input: { appUserId: string; dkp: number; jobType?: string | null; jobName?: string | null; subJobName?: string | null }
  ): Promise<boolean> {
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);
    try {
      await this.http.postActivityAction(`/api/activity/event-history/${id}/participants/add`, input);
      await this.auth.refreshOverview();
      this.auth.setActionMessage('Member added to the event and DKP granted.');
      return true;
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Adding the member failed.'));
      return false;
    }
  }

  async setEventHistoryParticipantActiveCredit(id: number, participantId: number, credited: boolean): Promise<boolean> {
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);
    try {
      await this.http.postActivityAction(
        `/api/activity/event-history/${id}/participants/${participantId}/active-credit`,
        { credited }
      );
      await this.auth.refreshOverview();
      this.auth.setActionMessage('Active credit updated.');
      return true;
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Updating active credit failed.'));
      return false;
    }
  }

  // Undo active credit for every attendee of an event (credited by accident).
  async clearEventHistoryActiveCredit(id: number): Promise<boolean> {
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);
    try {
      await this.http.postActivityAction(`/api/activity/event-history/${id}/active-credit/clear`, {});
      await this.auth.refreshOverview();
      this.auth.setActionMessage('Active credit removed for the whole event.');
      return true;
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Removing active credit failed.'));
      return false;
    }
  }

  async clearEventHistoryAbsences(id: number): Promise<boolean> {
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);
    try {
      await this.http.postActivityAction(`/api/activity/event-history/${id}/absences/clear`, {});
      await this.auth.refreshOverview();
      this.auth.setActionMessage('Absences undone — this event no longer counts toward active tracking.');
      return true;
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Undoing absences failed.'));
      return false;
    }
  }

  async removeEventHistoryParticipant(id: number, participantId: number): Promise<boolean> {
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);
    try {
      await this.http.postActivityAction(
        `/api/activity/event-history/${id}/participants/${participantId}/remove`
      );
      await this.auth.refreshOverview();
      this.auth.setActionMessage('Attendee removed and DKP refunded.');
      return true;
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Removing the attendee failed.'));
      return false;
    }
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
      const accessToken = this.auth.currentAccessToken();
      const headers: Record<string, string> = {};
      if (accessToken) headers['Authorization'] = `Bearer ${accessToken}`;
      const response = await fetch(`/api/addon/management/window-attendees/${attendeeId}`, {
        method: 'DELETE',
        headers,
        credentials: 'include'
      });
      if (!response.ok) {
        const text = await response.text();
        try {
          const payload = JSON.parse(text || '{}') as { error?: string };
          throw new Error(payload.error || `Remove failed (HTTP ${response.status}).`);
        } catch {
          throw new Error(text || `Remove failed (HTTP ${response.status}).`);
        }
      }
      return true;
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Removing the attendee failed.'));
      return false;
    }
  }

  // Prices ONE posted attendance window: what it pays each attendee, replacing the camp's default
  // open / close bonus for that window. `null` clears the price and puts the window back on the
  // default — a real instruction, which is why it is sent rather than omitted.
  //
  // Same route family and the same bearer + cookie shape as the remove above; the server refuses
  // camps that don't pay off this column (Manual Check In, timed events) and returns the reason,
  // which formatActionError surfaces verbatim.
  async setAttendanceWindowDkp(windowId: number, dkpAmount: number | null): Promise<boolean> {
    this.auth.setActionError(null);
    try {
      const accessToken = this.auth.currentAccessToken();
      const headers: Record<string, string> = { 'Content-Type': 'application/json' };
      if (accessToken) headers['Authorization'] = `Bearer ${accessToken}`;
      const response = await fetch(`/api/addon/management/attendance-windows/${windowId}/dkp`, {
        method: 'PATCH',
        headers,
        credentials: 'include',
        body: JSON.stringify({ dkpAmount })
      });
      if (!response.ok) {
        const text = await response.text();
        try {
          const payload = JSON.parse(text || '{}') as { error?: string };
          throw new Error(payload.error || `Update failed (HTTP ${response.status}).`);
        } catch {
          throw new Error(text || `Update failed (HTTP ${response.status}).`);
        }
      }
      return true;
    } catch (error) {
      this.auth.setActionError(formatActionError(error, "Setting this window's DKP failed."));
      return false;
    }
  }

  // Marks (or unmarks) ONE posted window as the camp's close — the only thing that decides the
  // close bonus now that it is no longer derived from "the newest window posted".
  //
  // The server enforces the one-close-per-camp rule and CLEARS the window's explicit price on the
  // way, so the close bonus can come through: an explicit amount replaces the computed value, and
  // the addon stamps one on every window it posts. That is why this cannot be a client-side flag
  // flip — see AddonApiController.SetClosingWindowAsync.
  async setAttendanceWindowClosing(windowId: number, isClosingWindow: boolean): Promise<boolean> {
    this.auth.setActionError(null);
    try {
      const accessToken = this.auth.currentAccessToken();
      const headers: Record<string, string> = { 'Content-Type': 'application/json' };
      if (accessToken) headers['Authorization'] = `Bearer ${accessToken}`;
      const response = await fetch(`/api/addon/management/attendance-windows/${windowId}/closing`, {
        method: 'PATCH',
        headers,
        credentials: 'include',
        body: JSON.stringify({ isClosingWindow })
      });
      if (!response.ok) {
        const text = await response.text();
        try {
          const payload = JSON.parse(text || '{}') as { error?: string };
          throw new Error(payload.error || `Update failed (HTTP ${response.status}).`);
        } catch {
          throw new Error(text || `Update failed (HTTP ${response.status}).`);
        }
      }
      return true;
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Marking the closing window failed.'));
      return false;
    }
  }
}
