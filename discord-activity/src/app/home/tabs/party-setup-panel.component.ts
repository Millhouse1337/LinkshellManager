import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, effect, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';

import { DiscordActivityService } from '../../discord/discord-activity.service';
import { PartySetupService } from '../../discord/party-setup.service';
import type { ActivityPartySetupSlot } from '../../discord/discord-activity.types';

interface SlotSignupDraft {
  role: string;
  mainJob: string;
  subJob: string;
}

// Shared interactive view of a single party setup's alliance -> party -> slot
// tree with member sign-up / withdraw. Embedded by BOTH the Party Setup tab
// (primary surface — works for every setup) and the ToDs tab inline panel (for
// an assigned monster's row). The detail is cached by setup id in
// PartySetupService, so several instances on the ToDs tab don't clobber each
// other. Option lists for the sign-up dropdowns come from the list response,
// which the embedding tabs always load.
@Component({
  selector: 'app-party-setup-panel',
  imports: [CommonModule, FormsModule],
  templateUrl: './party-setup-panel.component.html',
  styleUrl: './party-setup-panel.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class PartySetupPanelComponent {
  protected readonly activity = inject(DiscordActivityService);
  protected readonly partySetup = inject(PartySetupService);

  readonly setupId = input.required<number>();
  readonly linkshellId = input.required<number>();
  // When true, render the tree as a view-only roster: open slots show an
  // "Open" placeholder instead of the signup dropdowns + button, and filled
  // slots hide the Withdraw/Clear actions. Used by the live-event view to
  // recap "who signed up before this event started" without letting anyone
  // change the roster mid-run.
  readonly readOnly = input<boolean>(false);
  // When true, render the setup as a pure template: every slot shows only its
  // requirement (Any Tank, RDM/WHM, …) with NO sign-up controls and NO occupant
  // names — even for slots that have a stored signup. Used by the Party Setup tab
  // (a setup is a reusable template; signing up happens on an event), so it never
  // surfaces or invites roster changes.
  readonly templateOnly = input<boolean>(false);

  private readonly drafts = signal<Record<number, SlotSignupDraft>>({});

  constructor() {
    effect(() => {
      const id = this.setupId();
      if (id) queueMicrotask(() => void this.partySetup.loadDetail(id));
    });
  }

  protected detail() {
    return this.partySetup.detailFor(this.setupId());
  }

  protected roleOptions(): string[] {
    return this.partySetup.list()?.roleOptions ?? [];
  }

  protected mainJobOptions(): string[] {
    return this.partySetup.list()?.mainJobOptions ?? [];
  }

  protected subJobOptions(): string[] {
    return this.partySetup.list()?.subJobOptions ?? [];
  }

  protected myAppUserId(): string | null {
    return this.activity.overview()?.appUser?.id ?? null;
  }

  protected canManage(): boolean {
    return this.detail()?.canManage ?? false;
  }

  protected isOpen(slot: ActivityPartySetupSlot): boolean {
    return !slot.signedUpAppUserId;
  }

  protected isMine(slot: ActivityPartySetupSlot): boolean {
    const me = this.myAppUserId();
    return !!me && slot.signedUpAppUserId === me;
  }

  // Which sign-up dropdowns to show: only the fields the slot does NOT pin.
  // Mirrors the slotRequiresRole/Main/Sub logic in PartySetupController.SignUp.
  protected needsRole(slot: ActivityPartySetupSlot): boolean {
    return !slot.role;
  }

  protected needsMainJob(slot: ActivityPartySetupSlot): boolean {
    return !slot.mainJob;
  }

  protected needsSubJob(slot: ActivityPartySetupSlot): boolean {
    return !slot.subJob;
  }

  protected draftFor(slotId: number): SlotSignupDraft {
    return this.drafts()[slotId] ?? { role: '', mainJob: '', subJob: '' };
  }

  protected setDraft(slotId: number, patch: Partial<SlotSignupDraft>): void {
    this.drafts.update(map => ({
      ...map,
      [slotId]: { ...this.draftFor(slotId), ...patch }
    }));
  }

  // Server requires Role + Main on an open slot for any field it doesn't pin;
  // Sub stays optional. Disable the button until those are filled.
  protected signUpDisabled(slot: ActivityPartySetupSlot): boolean {
    if (this.partySetup.busy()) return true;
    const draft = this.draftFor(slot.slotId);
    if (this.needsRole(slot) && !draft.role) return true;
    if (this.needsMainJob(slot) && !draft.mainJob) return true;
    return false;
  }

  // Mirrors PartySetupSlotView.Display (ViewModels/PartySetupViewModel.cs).
  protected slotRequirement(slot: ActivityPartySetupSlot): string {
    const label = slot.label ? ` (${slot.label})` : '';
    let core: string;
    switch (slot.requirementType) {
      case 'Role':
        core = slot.role ? `Any ${slot.role}` : 'Any Role';
        break;
      case 'Job':
        core = slot.mainJob
          ? (slot.subJob ? `${slot.mainJob}/${slot.subJob}` : slot.mainJob)
          : 'Any job';
        break;
      default:
        core = 'Any Role';
    }
    return core + label;
  }

  // Mirrors PartySetupSlotView.SignedUpJobsDisplay.
  protected signedUpJobs(slot: ActivityPartySetupSlot): string {
    const parts: string[] = [];
    if (slot.signedUpRole) parts.push(slot.signedUpRole);
    if (slot.signedUpMainJob) {
      parts.push(slot.signedUpSubJob ? `${slot.signedUpMainJob}/${slot.signedUpSubJob}` : slot.signedUpMainJob);
    }
    return parts.join(' - ');
  }

  protected async signUp(slot: ActivityPartySetupSlot): Promise<void> {
    const draft = this.draftFor(slot.slotId);
    const ok = await this.partySetup.signUp(this.setupId(), slot.slotId, {
      role: this.needsRole(slot) ? (draft.role || null) : null,
      mainJob: this.needsMainJob(slot) ? (draft.mainJob || null) : null,
      subJob: this.needsSubJob(slot) ? (draft.subJob || null) : null
    });
    if (ok) {
      this.setDraft(slot.slotId, { role: '', mainJob: '', subJob: '' });
    }
  }

  protected async withdraw(slot: ActivityPartySetupSlot): Promise<void> {
    await this.partySetup.withdraw(this.setupId(), slot.slotId);
  }
}
