import { CommonModule } from '@angular/common';
import { CdkDragDrop, DragDropModule } from '@angular/cdk/drag-drop';
import { ChangeDetectionStrategy, Component, effect, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';

import { AutoRefreshService } from '../../discord/auto-refresh.service';
import { DiscordActivityService } from '../../discord/discord-activity.service';
import { PartySetupService } from '../../discord/party-setup.service';
import type {
  ActivityAlsoAttending,
  ActivityPartySetupAlliance,
  ActivityPartySetupParty,
  ActivityPartySetupSlot,
  PartySignupNudge
} from '../../discord/discord-activity.types';

interface SlotSignupDraft {
  role: string;
  mainJob: string;
  subJob: string;
  // When true, signing up also claims the party-leader spot (event board only).
  asLeader: boolean;
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
  imports: [CommonModule, FormsModule, DragDropModule],
  templateUrl: './party-setup-panel.component.html',
  styleUrl: './party-setup-panel.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class PartySetupPanelComponent {
  protected readonly activity = inject(DiscordActivityService);
  protected readonly partySetup = inject(PartySetupService);
  private readonly autoRefresh = inject(AutoRefreshService);

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
      // Re-read on every app refresh (timer / Refresh button / live change-feed) so
      // OTHER members' signups appear here without having to switch tabs.
      this.autoRefresh.refreshSignal();
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
    return this.drafts()[slotId] ?? { role: '', mainJob: '', subJob: '', asLeader: false };
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
        // "PC" (Player's Choice) keeps the slot label on one line — e.g. "MNK/PC".
        core = slot.mainJob
          ? `${slot.mainJob}/${slot.subJob ? slot.subJob : 'PC'}`
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

  // Whether to show the single "Sign up as" character picker in the board toolbar:
  // event board, the member actually has alts, and we're not editing / read-only.
  protected showCharacterPicker(): boolean {
    return !this.templateOnly() && !this.readOnly() && this.isEventBoard()
      && !this.editEnabled() && this.signupCharacterOptions().length > 1;
  }

  // True once any slot in the party has a per-event leader, so the "Sign up as
  // leader" action is hidden (first-claim-wins).
  protected partyHasLeader(party: { slots: ActivityPartySetupSlot[] }): boolean {
    return party.slots.some(s => s.signedUpIsPartyLeader === true);
  }

  // Alliance lead is only meaningful on a multi-alliance event board (a single
  // alliance has no header row of its own to mark).
  protected multiAlliance(): boolean {
    return (this.detail()?.alliances.length ?? 0) > 1;
  }

  // Show "Make me alliance lead" for a member who holds a slot in this alliance but
  // isn't already its lead. Mirrors the Discord button's slot gate + the web board.
  protected canTakeAllianceLead(alliance: ActivityPartySetupAlliance): boolean {
    if (!this.isEventBoard() || this.editEnabled() || this.readOnly() || !this.multiAlliance()) {
      return false;
    }
    const me = this.myAppUserId();
    if (!me || alliance.leadAppUserId === me) {
      return false;
    }
    return alliance.parties.some(p => p.slots.some(s => s.signedUpAppUserId === me));
  }

  protected async makeAllianceLead(): Promise<void> {
    await this.partySetup.makeAllianceLead(this.eventId());
  }

  // "Fill earlier alliances first" prompt: the slot the member chose + the server's
  // suggested earlier slot. Null when no prompt is showing.
  protected readonly signupNudge = signal<{
    nudge: PartySignupNudge;
    slot: ActivityPartySetupSlot;
    asLeader: boolean;
    characterName: string | null;
  } | null>(null);

  protected async signUp(slot: ActivityPartySetupSlot): Promise<void> {
    const draft = this.draftFor(slot.slotId);
    const picks = {
      role: this.needsRole(slot) ? (draft.role || null) : null,
      mainJob: this.needsMainJob(slot) ? (draft.mainJob || null) : null,
      subJob: this.needsSubJob(slot) ? (draft.subJob || null) : null
    };
    if (this.eventId() > 0) {
      const result = await this.partySetup.signUpEvent(
        this.eventId(), slot.slotId, { ...picks, asLeader: draft.asLeader, characterName: this.signupCharacter() || null });
      if (result && typeof result === 'object') {
        // Server suggests an open earlier-alliance slot — show the prompt; nothing committed.
        this.signupNudge.set({ nudge: result, slot, asLeader: draft.asLeader, characterName: this.signupCharacter() || null });
        return;
      }
      if (result === true) {
        this.setDraft(slot.slotId, { role: '', mainJob: '', subJob: '', asLeader: false });
      }
      return;
    }
    const ok = await this.partySetup.signUp(this.setupId(), slot.slotId, picks);
    if (ok) {
      this.setDraft(slot.slotId, { role: '', mainJob: '', subJob: '', asLeader: false });
    }
  }

  // Take the suggested earlier slot (uses the member's resolved role/job from the nudge).
  protected async takeSuggestedSlot(): Promise<void> {
    const n = this.signupNudge();
    if (!n) { return; }
    const ok = await this.partySetup.signUpEvent(this.eventId(), n.nudge.suggestedSlotId, {
      role: n.nudge.role ?? null, mainJob: n.nudge.mainJob ?? null, subJob: n.nudge.subJob ?? null,
      asLeader: n.asLeader, characterName: n.characterName, force: true
    });
    if (ok === true) {
      this.setDraft(n.slot.slotId, { role: '', mainJob: '', subJob: '', asLeader: false });
    }
    this.signupNudge.set(null);
  }

  // Sign up for the slot they originally chose, bypassing the nudge.
  protected async signUpAnyway(): Promise<void> {
    const n = this.signupNudge();
    if (!n) { return; }
    const ok = await this.partySetup.signUpEvent(this.eventId(), n.slot.slotId, {
      role: n.nudge.role ?? null, mainJob: n.nudge.mainJob ?? null, subJob: n.nudge.subJob ?? null,
      asLeader: n.asLeader, characterName: n.characterName, force: true
    });
    if (ok === true) {
      this.setDraft(n.slot.slotId, { role: '', mainJob: '', subJob: '', asLeader: false });
    }
    this.signupNudge.set(null);
  }

  protected dismissNudge(): void {
    this.signupNudge.set(null);
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

  // ===== Officer board editing (live event boards only) =============================
  // Drag slots to rearrange the comp (occupants ride along), edit a slot's job inline
  // (an occupied slot's job change moves the occupant to "Also Attending"), move members
  // between slots / to-from Also Attending, and add/remove/rename slots + parties. All
  // gated behind an explicit "Edit layout" toggle so the normal board stays clean.

  protected readonly editing = signal(false);
  protected readonly editingSlotId = signal<number | null>(null);

  // Single character (main/alt) the member signs up as, chosen once in the board
  // toolbar instead of per-slot. '' = main; other values are alt names. Mirrors
  // signupCharacterOptions (first entry is the main). Persists across sign-ups so
  // claiming several slots as the same alt needs only one selection.
  protected readonly signupCharacter = signal('');
  private readonly jobEdits = signal<Record<number, { role: string; mainJob: string; subJob: string }>>({});

  // Officer + a live event board (never the reusable template or a read-only recap).
  protected canEdit(): boolean {
    return this.isEventBoard() && !this.templateOnly() && !this.readOnly() && this.canManage();
  }

  protected toggleEditing(): void {
    this.editing.update(v => !v);
    this.editingSlotId.set(null);
  }

  protected editEnabled(): boolean {
    return this.canEdit() && this.editing();
  }

  // ----- slot drag (reorder within / move between parties; occupant rides along) -----
  protected onSlotDropped(event: CdkDragDrop<ActivityPartySetupSlot[]>, targetParty: ActivityPartySetupParty): void {
    if (event.previousContainer === event.container && event.previousIndex === event.currentIndex) {
      return;
    }
    const slot = event.item.data as ActivityPartySetupSlot;
    void this.partySetup.moveSlot(this.eventId(), slot.slotId, targetParty.partyId, event.currentIndex);
  }

  // ----- inline slot job edit -----
  protected jobEditFor(slotId: number): { role: string; mainJob: string; subJob: string } {
    return this.jobEdits()[slotId] ?? { role: '', mainJob: '', subJob: '' };
  }

  protected openJobEdit(slot: ActivityPartySetupSlot): void {
    this.jobEdits.update(m => ({
      ...m,
      [slot.slotId]: { role: slot.role ?? '', mainJob: slot.mainJob ?? '', subJob: slot.subJob ?? '' }
    }));
    this.editingSlotId.set(slot.slotId);
  }

  protected setJobEdit(slotId: number, patch: Partial<{ role: string; mainJob: string; subJob: string }>): void {
    this.jobEdits.update(m => ({ ...m, [slotId]: { ...this.jobEditFor(slotId), ...patch } }));
  }

  protected async saveJobEdit(slot: ActivityPartySetupSlot): Promise<void> {
    const e = this.jobEditFor(slot.slotId);
    const ok = await this.partySetup.editSlotRequirement(this.eventId(), slot.slotId, {
      role: e.role || null,
      mainJob: e.mainJob || null,
      subJob: e.subJob || null
    });
    if (ok) { this.editingSlotId.set(null); }
  }

  // Back out of the inline job edit without saving. The draft is re-seeded from the
  // slot's current values whenever the editor is reopened (openJobEdit), so just
  // closing it is enough — the abandoned draft is discarded on next open.
  protected cancelJobEdit(): void {
    this.editingSlotId.set(null);
  }

  // ----- member moves (precise dropdown, touch-friendly) -----
  // Every OPEN slot across the board, labeled, as a move target.
  protected openSlotTargets(): { slotId: number; label: string }[] {
    const detail = this.detail();
    if (!detail) { return []; }
    const out: { slotId: number; label: string }[] = [];
    detail.alliances.forEach((a, ai) =>
      a.parties.forEach((p, pi) =>
        p.slots.forEach(s => {
          if (!s.signedUpAppUserId) {
            out.push({ slotId: s.slotId, label: `A${ai + 1} ${p.name || 'Party ' + (pi + 1)}: ${this.slotRequirement(s)}` });
          }
        })));
    return out;
  }

  protected async moveMemberToSlot(fromSlotId: number, value: string): Promise<void> {
    if (!value) { return; }
    await this.partySetup.moveMember(this.eventId(), {
      fromSlotId,
      toSlotId: value === 'also' ? null : Number(value)
    });
  }

  protected async seatAttendee(attendee: ActivityAlsoAttending, value: string): Promise<void> {
    if (!value) { return; }
    await this.partySetup.moveMember(this.eventId(), {
      fromSlotId: null,
      toSlotId: Number(value),
      appUserId: attendee.appUserId ?? null
    });
  }

  // ----- structure (add / delete / rename) -----
  protected partyAtCap(party: ActivityPartySetupParty): boolean {
    return party.slots.length >= 6;
  }

  protected allianceAtCap(alliance: ActivityPartySetupAlliance): boolean {
    return alliance.parties.length >= 3;
  }

  protected async addSlot(party: ActivityPartySetupParty): Promise<void> {
    await this.partySetup.addSlot(this.eventId(), { partyId: party.partyId });
  }

  protected async deleteSlot(slot: ActivityPartySetupSlot): Promise<void> {
    await this.partySetup.deleteSlot(this.eventId(), slot.slotId);
  }

  protected async addParty(alliance: ActivityPartySetupAlliance): Promise<void> {
    await this.partySetup.addParty(this.eventId(), alliance.allianceId);
  }

  protected async removeParty(party: ActivityPartySetupParty): Promise<void> {
    await this.partySetup.removeParty(this.eventId(), party.partyId);
  }

  protected async renameParty(party: ActivityPartySetupParty, name: string): Promise<void> {
    await this.partySetup.rename(this.eventId(), { partyId: party.partyId, name });
  }

  protected async renameAlliance(alliance: ActivityPartySetupAlliance, name: string): Promise<void> {
    await this.partySetup.rename(this.eventId(), { allianceId: alliance.allianceId, name });
  }
}
