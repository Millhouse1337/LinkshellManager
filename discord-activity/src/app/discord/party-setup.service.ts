import { Injectable, inject, signal } from '@angular/core';

import { ActivityHttpClient } from './activity-http.client';
import { AuthService } from './auth.service';
import { formatActionError } from './discord-activity.helpers';
import type {
  ActivityPartySetupDetail,
  ActivityPartySetupEditorInput,
  ActivityPartySetupListResponse,
  ActivityPartySetupSignUpInput,
  BoardAddSlotInput,
  BoardMoveMemberInput,
  BoardRenameInput,
  BoardSlotRequirementInput,
  PartySignupNudge
} from './discord-activity.types';

// Discord Activity client for the raid-composition planner (Party Setup).
// Mirrors window-event.service.ts: standalone, injected directly into the
// party-setup tab AND the ToDs tab's inline sign-up panel (one root singleton,
// so both share the same loaded data). Detail is cached by setup id so several
// inline panels on the ToDs tab can be open at once without clobbering each
// other.
@Injectable({ providedIn: 'root' })
export class PartySetupService {
  private readonly auth = inject(AuthService);
  private readonly http = inject(ActivityHttpClient);

  readonly list = signal<ActivityPartySetupListResponse | null>(null);
  readonly detailsById = signal<Record<number, ActivityPartySetupDetail>>({});
  readonly busy = signal(false);

  detailFor(setupId: number): ActivityPartySetupDetail | null {
    return this.detailsById()[setupId] ?? null;
  }

  async loadList(linkshellId: number): Promise<void> {
    if (!linkshellId) {
      this.list.set(null);
      return;
    }

    this.busy.set(true);
    this.auth.setActionError(null);
    try {
      const result = await this.http.fetchActivityJson<ActivityPartySetupListResponse>(
        `/api/activity/party-setups?linkshellId=${linkshellId}`
      );
      this.list.set(result);
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Loading party setups failed.'));
    } finally {
      this.busy.set(false);
    }
  }

  async loadDetail(setupId: number): Promise<void> {
    if (!setupId) {
      return;
    }

    this.busy.set(true);
    this.auth.setActionError(null);
    try {
      const result = await this.http.fetchActivityJson<ActivityPartySetupDetail>(
        `/api/activity/party-setups/${setupId}`
      );
      this.detailsById.update(map => ({ ...map, [setupId]: result }));
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Loading the party setup failed.'));
    } finally {
      this.busy.set(false);
    }
  }

  async signUp(setupId: number, slotId: number, input: ActivityPartySetupSignUpInput): Promise<boolean> {
    return this.mutateSlot(
      setupId,
      `/api/activity/party-setups/${setupId}/slots/${slotId}/signup`,
      input,
      'Signed up.'
    );
  }

  async withdraw(setupId: number, slotId: number): Promise<boolean> {
    return this.mutateSlot(
      setupId,
      `/api/activity/party-setups/${setupId}/slots/${slotId}/withdraw`,
      undefined,
      'Slot released.'
    );
  }

  // ----- Per-EVENT board: party setups are reusable templates, so an event's
  // roster is scoped to the event (shared with the Discord board + web). Cached
  // by event id, separate from the template detailsById cache. -----
  readonly eventBoardsById = signal<Record<number, ActivityPartySetupDetail>>({});

  eventBoardFor(eventId: number): ActivityPartySetupDetail | null {
    return this.eventBoardsById()[eventId] ?? null;
  }

  async loadEventBoard(eventId: number): Promise<void> {
    if (!eventId) {
      return;
    }

    this.busy.set(true);
    this.auth.setActionError(null);
    try {
      const result = await this.http.fetchActivityJson<ActivityPartySetupDetail>(
        `/api/activity/events/${eventId}/party-board`
      );
      this.eventBoardsById.update(map => ({ ...map, [eventId]: result }));
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Loading the party board failed.'));
    } finally {
      this.busy.set(false);
    }
  }

  // Returns true on signup, false on error, or a PartySignupNudge when the server
  // suggests an open slot in an earlier alliance (nothing committed yet).
  async signUpEvent(eventId: number, slotId: number, input: ActivityPartySetupSignUpInput): Promise<boolean | PartySignupNudge> {
    this.busy.set(true);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);
    try {
      const res = await this.http.postActivityJson<{ success?: boolean; nudge?: PartySignupNudge }>(
        `/api/activity/events/${eventId}/party-slots/${slotId}/signup`, input);
      if (res?.nudge) {
        return res.nudge;
      }
      await this.loadEventBoard(eventId);
      this.auth.setActionMessage('Signed up.');
      return true;
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Signing up failed.'));
      return false;
    } finally {
      this.busy.set(false);
    }
  }

  async withdrawEvent(eventId: number, slotId: number): Promise<boolean> {
    return this.mutateEventSlot(
      eventId,
      `/api/activity/events/${eventId}/party-slots/${slotId}/withdraw`,
      undefined,
      'Slot released.'
    );
  }

  // "Make Me Alliance Lead": the caller (who must already hold a slot in this
  // event) takes their alliance's lead (👑 by the alliance name). Reloads the board.
  async makeAllianceLead(eventId: number): Promise<boolean> {
    return this.mutateEventSlot(
      eventId,
      `/api/activity/events/${eventId}/make-alliance-lead`,
      undefined,
      "You're now the alliance lead."
    );
  }

  // ----- Officer board editing (drag-drop + per-slot job changes) -----
  // Each posts then reloads the event board, so the panel renders authoritative state
  // (e.g. a displaced occupant moving into "Also Attending").

  async editSlotRequirement(eventId: number, slotId: number, input: BoardSlotRequirementInput): Promise<boolean> {
    return this.mutateEventSlot(
      eventId, `/api/activity/events/${eventId}/board/slots/${slotId}/requirement`, input, 'Slot updated.');
  }

  async moveSlot(eventId: number, slotId: number, targetPartyId: number, targetIndex: number): Promise<boolean> {
    return this.mutateEventSlot(
      eventId, `/api/activity/events/${eventId}/board/slots/${slotId}/move`, { targetPartyId, targetIndex }, 'Slot moved.');
  }

  async moveMember(eventId: number, input: BoardMoveMemberInput): Promise<boolean> {
    return this.mutateEventSlot(
      eventId, `/api/activity/events/${eventId}/board/members/move`, input, 'Member moved.');
  }

  async addSlot(eventId: number, input: BoardAddSlotInput): Promise<boolean> {
    return this.mutateEventSlot(
      eventId, `/api/activity/events/${eventId}/board/slots`, input, 'Slot added.');
  }

  async deleteSlot(eventId: number, slotId: number): Promise<boolean> {
    return this.mutateEventSlot(
      eventId, `/api/activity/events/${eventId}/board/slots/${slotId}/delete`, undefined, 'Slot removed.');
  }

  async addParty(eventId: number, allianceId: number, name?: string | null): Promise<boolean> {
    return this.mutateEventSlot(
      eventId, `/api/activity/events/${eventId}/board/parties`, { allianceId, name: name ?? null }, 'Party added.');
  }

  async removeParty(eventId: number, partyId: number): Promise<boolean> {
    return this.mutateEventSlot(
      eventId, `/api/activity/events/${eventId}/board/parties/${partyId}/delete`, undefined, 'Party removed.');
  }

  async rename(eventId: number, input: BoardRenameInput): Promise<boolean> {
    return this.mutateEventSlot(
      eventId, `/api/activity/events/${eventId}/board/rename`, input, 'Renamed.');
  }

  private async mutateEventSlot(eventId: number, path: string, body: unknown, message: string): Promise<boolean> {
    this.busy.set(true);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);
    try {
      await this.http.postActivityAction(path, body);
      await this.loadEventBoard(eventId);
      this.auth.setActionMessage(message);
      return true;
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Updating the party board failed.'));
      return false;
    } finally {
      this.busy.set(false);
    }
  }

  // ----- Officer editor (create / edit / delete / assign) -----

  async create(input: ActivityPartySetupEditorInput): Promise<number | null> {
    this.busy.set(true);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);
    try {
      const result = await this.http.postActivityJson<{ id: number }>('/api/activity/party-setups', input);
      await this.loadList(input.linkshellId);
      this.auth.setActionMessage('Party setup created.');
      return result?.id ?? null;
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Creating the party setup failed.'));
      return null;
    } finally {
      this.busy.set(false);
    }
  }

  async update(id: number, input: ActivityPartySetupEditorInput): Promise<boolean> {
    this.busy.set(true);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);
    try {
      await this.http.postActivityJson(`/api/activity/party-setups/${id}`, input);
      await this.loadList(input.linkshellId);
      await this.loadDetail(id);
      this.auth.setActionMessage('Party setup updated.');
      return true;
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Updating the party setup failed.'));
      return false;
    } finally {
      this.busy.set(false);
    }
  }

  async remove(id: number, linkshellId: number): Promise<boolean> {
    this.busy.set(true);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);
    try {
      await this.http.postActivityJson(`/api/activity/party-setups/${id}/delete`);
      await this.loadList(linkshellId);
      this.auth.setActionMessage('Party setup deleted.');
      return true;
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Deleting the party setup failed.'));
      return false;
    } finally {
      this.busy.set(false);
    }
  }

  async assign(id: number, monsterName: string | null, linkshellId: number): Promise<boolean> {
    this.busy.set(true);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);
    try {
      await this.http.postActivityJson(`/api/activity/party-setups/${id}/assign`, { monsterName });
      await this.loadList(linkshellId);
      await this.loadDetail(id);
      this.auth.setActionMessage(monsterName ? `Assigned to ${monsterName}.` : 'Cleared monster assignment.');
      return true;
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Updating the assignment failed.'));
      return false;
    } finally {
      this.busy.set(false);
    }
  }

  private async mutateSlot(setupId: number, path: string, body: unknown, message: string): Promise<boolean> {
    this.busy.set(true);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);
    try {
      await this.http.postActivityAction(path, body);
      await this.loadDetail(setupId);
      this.auth.setActionMessage(message);
      return true;
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Updating the party setup failed.'));
      return false;
    } finally {
      this.busy.set(false);
    }
  }
}
