import { Injectable, inject, signal } from '@angular/core';

import { ActivityHttpClient } from './activity-http.client';
import { AuthService } from './auth.service';
import { mutateThenRefresh } from './mutate-then-refresh';
import type {
  ActivityPartySetupDetail,
  ActivityPartySetupEditorInput,
  ActivityPartySetupListResponse,
  ActivityPartySetupSignUpInput,
  BoardAddSlotInput,
  BoardMoveMemberInput,
  BoardRenameInput,
  BoardSlotRequirementInput
} from './discord-activity.types';
import { ResponseOrder } from './refresh-order';

// Discord Activity client for the raid-composition planner (Party Setup).
// Mirrors window-event.service.ts: standalone, injected directly into the
// party-setup tab AND the ToDs tab's inline view-only panel (one root singleton,
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

  // The list is loaded by the tab, the refresh timer and every write; only the newest reply for the
  // current linkshell is shown (see refresh-order.ts).
  private readonly listOrder = new ResponseOrder();

  async loadList(linkshellId: number): Promise<void> {
    if (!linkshellId) {
      this.listOrder.invalidate();
      this.list.set(null);
      return;
    }

    this.busy.set(true);
    try {
      const ticket = this.listOrder.begin(linkshellId);
      const result = await this.http.fetchActivityJson<ActivityPartySetupListResponse>(
        `/api/activity/party-setups?linkshellId=${linkshellId}`
      );
      if (this.listOrder.accept(ticket, linkshellId)) this.list.set(result);
      this.auth.clearLoadError('party-setups');
    } catch (error) {
      // Loaders report in their own banner, never the action one (see AuthService.loadError).
      this.auth.reportLoadError('party-setups', error, 'Loading party setups failed.');
    } finally {
      this.busy.set(false);
    }
  }

  async loadDetail(setupId: number): Promise<void> {
    if (!setupId) {
      return;
    }

    this.busy.set(true);
    try {
      const result = await this.http.fetchActivityJson<ActivityPartySetupDetail>(
        `/api/activity/party-setups/${setupId}`
      );
      this.detailsById.update(map => ({ ...map, [setupId]: result }));
      this.auth.clearLoadError('party-setup');
    } catch (error) {
      this.auth.reportLoadError('party-setup', error, 'Loading the party setup failed.');
    } finally {
      this.busy.set(false);
    }
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
    try {
      const result = await this.http.fetchActivityJson<ActivityPartySetupDetail>(
        `/api/activity/events/${eventId}/party-board`
      );
      this.eventBoardsById.update(map => ({ ...map, [eventId]: result }));
      this.auth.clearLoadError('party-board');
    } catch (error) {
      this.auth.reportLoadError('party-board', error, 'Loading the party board failed.');
    } finally {
      this.busy.set(false);
    }
  }

  // Returns true on signup, false on error.
  async signUpEvent(eventId: number, slotId: number, input: ActivityPartySetupSignUpInput): Promise<boolean> {
    this.busy.set(true);
    try {
      const outcome = await mutateThenRefresh(this.auth, {
        mutate: () => this.http.postActivityJson<{ success?: boolean }>(
          `/api/activity/events/${eventId}/party-slots/${slotId}/signup`, input),
        refresh: () => this.loadEventBoard(eventId),
        success: 'Signed up.',
        failure: 'Signing up failed.'
      });
      return outcome.ok;
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
    return this.save(
      () => this.http.postActivityAction(path, body),
      () => this.loadEventBoard(eventId),
      message,
      'Updating the party board failed.');
  }

  // Send one change, then reload what it touched. True when the save worked, even if the reload
  // after it did not (see mutate-then-refresh.ts).
  private async save(
    mutate: () => Promise<unknown>,
    refresh: () => Promise<unknown>,
    success: string,
    failure: string
  ): Promise<boolean> {
    this.busy.set(true);
    try {
      const outcome = await mutateThenRefresh(this.auth, { mutate, refresh, success, failure });
      return outcome.ok;
    } finally {
      this.busy.set(false);
    }
  }

  // ----- Officer editor (create / edit / delete / assign) -----

  async create(input: ActivityPartySetupEditorInput): Promise<number | null> {
    this.busy.set(true);
    try {
      const outcome = await mutateThenRefresh(this.auth, {
        mutate: () => this.http.postActivityJson<{ id: number }>('/api/activity/party-setups', input),
        refresh: () => this.loadList(input.linkshellId),
        success: 'Party setup created.',
        failure: 'Creating the party setup failed.'
      });
      return outcome.ok ? outcome.result?.id ?? null : null;
    } finally {
      this.busy.set(false);
    }
  }

  async update(id: number, input: ActivityPartySetupEditorInput): Promise<boolean> {
    return this.save(
      () => this.http.postActivityJson(`/api/activity/party-setups/${id}`, input),
      async () => {
        await this.loadList(input.linkshellId);
        await this.loadDetail(id);
      },
      'Party setup updated.',
      'Updating the party setup failed.');
  }

  async remove(id: number, linkshellId: number): Promise<boolean> {
    return this.save(
      () => this.http.postActivityJson(`/api/activity/party-setups/${id}/delete`),
      () => this.loadList(linkshellId),
      'Party setup deleted.',
      'Deleting the party setup failed.');
  }

  async assign(id: number, monsterName: string | null, linkshellId: number): Promise<boolean> {
    return this.save(
      () => this.http.postActivityJson(`/api/activity/party-setups/${id}/assign`, { monsterName }),
      async () => {
        await this.loadList(linkshellId);
        await this.loadDetail(id);
      },
      monsterName ? `Assigned to ${monsterName}.` : 'Cleared monster assignment.',
      'Updating the assignment failed.');
  }
}
