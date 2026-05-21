import { Injectable, inject, signal } from '@angular/core';

import { ActivityHttpClient } from './activity-http.client';
import { AuthService } from './auth.service';
import { formatActionError } from './discord-activity.helpers';
import type {
  ActivityPartySetupDetail,
  ActivityPartySetupEditorInput,
  ActivityPartySetupListResponse,
  ActivityPartySetupSignUpInput
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
