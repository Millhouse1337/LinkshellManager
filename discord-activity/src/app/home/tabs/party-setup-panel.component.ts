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
  // When true, signing up also claims the party-leader spot (event board only).
  asLeader: boolean;
  // Event board only: which character to sign up as ('' = main). Picker shows
  // only when the member has alts.
  characterName: string;
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
  // When > 0, this panel is rendering an EVENT's board: the roster is scoped to
  // the event (per-event signups), so detail + sign-up/withdraw use the event
  // endpoints instead of the shared template. Keeps the Activity in sync with the
  // Discord board and the web event page.
  readonly eventId = input<number>(0);
  // When true, this is a LIVE event's board: open slots stay claimable (late join
  // into a slot), but a member can't drop their OWN slot mid-run — only an officer
  // can Clear a slot. Combine with readOnly=false + eventId>0.
  readonly live = input<boolean>(false);

  private readonly drafts = signal<Record<number, SlotSignupDraft>>({});

  constructor() {
    effect(() => {
      const eventId = this.eventId();
      const setupId = this.setupId();
      if (eventId > 0) {
        queueMicrotask(() => void this.partySetup.loadEventBoard(eventId));
      } else if (setupId) {
        queueMicrotask(() => void this.partySetup.loadDetail(setupId));
      }
    });
  }

  protected detail() {
    return this.eventId() > 0
      ? this.partySetup.eventBoardFor(this.eventId())
      : this.partySetup.detailFor(this.setupId());
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
    return this.drafts()[slotId] ?? { role: '', mainJob: '', subJob: '', asLeader: false, characterName: '' };
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
          ? `${slot.mainJob}/${slot.subJob ? slot.subJob : "Player's Choice"}`
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

  // Event boards only: whether claiming a party-leader spot is offered.
  protected isEventBoard(): boolean {
    return this.eventId() > 0;
  }

  // Characters the member can sign up as (main + alts). Only used on the event
  // board; the picker renders when there's more than one.
  protected signupCharacterOptions(): string[] {
    return this.activity.signupCharacterOptions();
  }

  // True once any slot in the party has a per-event leader, so the "Sign up as
  // leader" action is hidden (first-claim-wins).
  protected partyHasLeader(party: { slots: ActivityPartySetupSlot[] }): boolean {
    return party.slots.some(s => s.signedUpIsPartyLeader === true);
  }

  protected async signUp(slot: ActivityPartySetupSlot): Promise<void> {
    const draft = this.draftFor(slot.slotId);
    const picks = {
      role: this.needsRole(slot) ? (draft.role || null) : null,
      mainJob: this.needsMainJob(slot) ? (draft.mainJob || null) : null,
      subJob: this.needsSubJob(slot) ? (draft.subJob || null) : null
    };
    const ok = this.eventId() > 0
      ? await this.partySetup.signUpEvent(this.eventId(), slot.slotId, { ...picks, asLeader: draft.asLeader, characterName: draft.characterName || null })
      : await this.partySetup.signUp(this.setupId(), slot.slotId, picks);
    if (ok) {
      this.setDraft(slot.slotId, { role: '', mainJob: '', subJob: '', asLeader: false, characterName: '' });
    }
  }

  protected async withdraw(slot: ActivityPartySetupSlot): Promise<void> {
    if (this.eventId() > 0) {
      await this.partySetup.withdrawEvent(this.eventId(), slot.slotId);
    } else {
      await this.partySetup.withdraw(this.setupId(), slot.slotId);
    }
  }

  // Drop a no-slot ("Also attending") signup for the current member, then reload
  // the board so the row disappears. Event boards only.
  protected async withdrawNoSlot(): Promise<void> {
    const eventId = this.eventId();
    if (eventId <= 0) { return; }
    await this.activity.unsignFromEvent(eventId);
    await this.partySetup.loadEventBoard(eventId);
  }
}
