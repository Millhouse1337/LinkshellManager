import { CommonModule } from '@angular/common';
import { Component, ElementRef, inject, input, output, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';

import { DiscordActivityService } from '../../discord/discord-activity.service';
import { PartySetupService } from '../../discord/party-setup.service';
import type { ActivityPartySetupSlotInput } from '../../discord/discord-activity.types';

interface EditorSlot {
  role: string;
  mainJob: string;
  subJob: string;
  isPartyLeader: boolean;
}

interface EditorParty {
  name: string;
  slots: EditorSlot[];
}

interface EditorAlliance {
  name: string;
  parties: EditorParty[];
}

// Officer create/edit modal for a party setup's alliance -> party -> slot tree.
// Rendered in a native <dialog> (showModal) — Discord's iframe blocks
// window.confirm/native prompts. Uses default change detection: the model is a
// plain mutable nested object two-way bound with ngModel, and structural edits
// (add/remove) happen in click handlers. On save the tree is flattened to the
// server's BuildTreeFromFlat shape (allianceIndex/partyIndex/slotIndex), with
// RequirementType derived per slot (Job > Role > Any) to match MapSlot.
@Component({
  selector: 'app-party-setup-editor',
  imports: [CommonModule, FormsModule],
  templateUrl: './party-setup-editor.component.html',
  styleUrl: './party-setup-editor.component.scss'
})
export class PartySetupEditorComponent {
  private static readonly BASE_EVENT_TYPE_OPTIONS = ['Sky', 'Sea', 'HENM', 'Limbus', 'Dynamis', 'BCNM', 'KSNM'] as const;

  protected readonly activity = inject(DiscordActivityService);
  protected readonly partySetup = inject(PartySetupService);

  readonly linkshellId = input.required<number>();
  readonly saved = output<number>();

  private readonly dialog = viewChild<ElementRef<HTMLDialogElement>>('editorDialog');

  protected readonly MAX_PARTIES = 3;
  protected readonly MAX_SLOTS = 6;
  protected readonly ANY_ROLE = 'Any Role';
  protected readonly HNM_EVENT_TYPE = 'HNM';
  protected readonly OTHER_EVENT_TYPE = 'Other';
  // A party setup typed "Any" is event-type-agnostic: it shows in the Create
  // Event party-setup picker for EVERY event type. (Not an event type itself —
  // events are always a specific type.)
  protected readonly ANY_EVENT_TYPE = 'Any';

  protected editingId: number | null = null;
  protected name = '';
  protected eventTypeSelection = '';
  protected customEventType = '';
  protected assignedMonster = '';
  protected notes = '';
  protected alliances: EditorAlliance[] = [];

  protected shouldShowMonsterField(): boolean {
    return this.eventTypeSelection === this.HNM_EVENT_TYPE;
  }

  protected eventTypeOptions(): string[] {
    return [
      this.ANY_EVENT_TYPE,
      ...PartySetupEditorComponent.BASE_EVENT_TYPE_OPTIONS,
      this.HNM_EVENT_TYPE,
      this.OTHER_EVENT_TYPE
    ];
  }

  protected isOtherEventTypeSelected(): boolean {
    return this.eventTypeSelection === this.OTHER_EVENT_TYPE;
  }

  protected setEventTypeSelection(value: string): void {
    this.eventTypeSelection = value;
    if (value !== this.OTHER_EVENT_TYPE) {
      this.customEventType = '';
    }
    if (value !== this.HNM_EVENT_TYPE) {
      this.assignedMonster = '';
    }
  }

  private resolvedEventType(): string {
    if (this.eventTypeSelection === this.OTHER_EVENT_TYPE) {
      return this.customEventType.trim();
    }

    return this.eventTypeSelection.trim();
  }

  private applyEventType(eventType: string | null | undefined, assignedMonsterName?: string | null): void {
    const trimmed = (eventType ?? '').trim();
    if (!trimmed && assignedMonsterName) {
      this.eventTypeSelection = this.HNM_EVENT_TYPE;
      this.customEventType = '';
      this.assignedMonster = assignedMonsterName;
      return;
    }

    if (!trimmed) {
      this.eventTypeSelection = '';
      this.customEventType = '';
      this.assignedMonster = '';
      return;
    }

    if (this.eventTypeOptions().includes(trimmed)) {
      this.eventTypeSelection = trimmed;
      this.customEventType = '';
    } else {
      this.eventTypeSelection = this.OTHER_EVENT_TYPE;
      this.customEventType = trimmed;
    }

    this.assignedMonster = trimmed === this.HNM_EVENT_TYPE ? (assignedMonsterName ?? '') : '';
  }

  protected monsterOptions(): string[] {
    return this.partySetup.list()?.monsterOptions ?? [];
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

  // The Main select's blank option reflects the chosen role ("Any Tank", "Any
  // DPS", …) so an open role slot reads naturally; a slot with no specific role
  // falls back to "Any job".
  protected anyMainLabel(slot: EditorSlot): string {
    const role = (slot.role ?? '').trim();
    return role && role !== this.ANY_ROLE ? `Any ${role}` : 'Any job';
  }

  // A job picked in Main can't also be picked in Sub, and vice versa — each list
  // drops the OTHER field's current pick (but keeps its own, so a legacy main==sub
  // row never silently loses its value).
  protected mainJobOptionsFor(slot: EditorSlot): string[] {
    return this.mainJobOptions().filter(job => job !== slot.subJob || job === slot.mainJob);
  }

  protected subJobOptionsFor(slot: EditorSlot): string[] {
    return this.subJobOptions().filter(job => job !== slot.mainJob || job === slot.subJob);
  }

  openForCreate(): void {
    this.editingId = null;
    this.name = '';
    this.applyEventType(null, null);
    this.notes = '';
    this.alliances = [this.newAlliance()];
    this.openDialog();
  }

  async openForEdit(setupId: number): Promise<void> {
    await this.partySetup.loadDetail(setupId);
    const detail = this.partySetup.detailFor(setupId);
    if (!detail) return;

    this.editingId = setupId;
    this.name = detail.name;
    this.applyEventType(detail.eventType, detail.assignedMonsterName ?? null);
    this.notes = detail.notes ?? '';
    this.alliances = detail.alliances.map(alliance => ({
      name: alliance.name,
      parties: alliance.parties.map(party => ({
        name: party.name,
        slots: party.slots.map(slot => ({
          role: slot.role || this.ANY_ROLE,
          mainJob: slot.mainJob ?? '',
          subJob: slot.subJob ?? '',
          isPartyLeader: slot.isPartyLeader
        }))
      }))
    }));
    this.openDialog();
  }

  // ----- Tree edits (mutate in place; default CD picks them up) -----

  protected addAlliance(): void {
    this.alliances.push(this.newAlliance());
  }

  protected removeAlliance(index: number): void {
    this.alliances.splice(index, 1);
  }

  protected addParty(alliance: EditorAlliance): void {
    if (alliance.parties.length < this.MAX_PARTIES) {
      alliance.parties.push(this.newParty());
    }
  }

  protected removeParty(alliance: EditorAlliance, index: number): void {
    alliance.parties.splice(index, 1);
  }

  protected addSlot(party: EditorParty): void {
    if (party.slots.length < this.MAX_SLOTS) {
      party.slots.push(this.newSlot());
    }
  }

  protected removeSlot(party: EditorParty, index: number): void {
    party.slots.splice(index, 1);
  }

  // Clone a slot row directly below itself (web's ps-duplicate-slot button).
  // Skipped silently when the party is at the 6-slot cap.
  protected duplicateSlot(party: EditorParty, index: number): void {
    if (party.slots.length >= this.MAX_SLOTS) return;
    const source = party.slots[index];
    party.slots.splice(index + 1, 0, {
      role: source.role,
      mainJob: source.mainJob,
      subJob: source.subJob,
      isPartyLeader: false
    });
  }

  // A slot is "Any" — no specific role/job required — when the role is the
  // sentinel "Any Role" pick (or blank) AND no main job is chosen. The view
  // collapses the Main/Sub selects into a dashed "— Anything —" pill.
  protected isAnySlot(slot: EditorSlot): boolean {
    const role = (slot.role ?? '').trim();
    return (role === '' || role === this.ANY_ROLE) && !slot.mainJob;
  }

  // One leader per party: clear the rest when one is toggled on.
  protected setLeader(party: EditorParty, slot: EditorSlot, isLeader: boolean): void {
    if (isLeader) {
      for (const other of party.slots) {
        other.isPartyLeader = false;
      }
    }
    slot.isPartyLeader = isLeader;
  }

  private newSlot(): EditorSlot {
    return { role: this.ANY_ROLE, mainJob: '', subJob: '', isPartyLeader: false };
  }

  private newParty(): EditorParty {
    return { name: '', slots: [this.newSlot()] };
  }

  private newAlliance(): EditorAlliance {
    return { name: '', parties: [this.newParty()] };
  }

  // ----- Save -----

  protected async save(): Promise<void> {
    const name = this.name.trim();
    if (!name) {
      this.activity.actionError.set('A name is required.');
      return;
    }

    const eventType = this.resolvedEventType();
    if (!eventType) {
      this.activity.actionError.set(this.isOtherEventTypeSelected()
        ? 'Enter a custom event type.'
        : 'Select an event type.');
      return;
    }

    const slots: ActivityPartySetupSlotInput[] = [];
    this.alliances.forEach((alliance, allianceIndex) => {
      alliance.parties.forEach((party, partyIndex) => {
        party.slots.forEach((slot, slotIndex) => {
          const role = slot.role === this.ANY_ROLE ? '' : slot.role.trim();
          const mainJob = slot.mainJob.trim();
          const subJob = slot.subJob.trim();
          // Derive RequirementType the same way the server's MapSlot does.
          const requirementType = mainJob ? 'Job' : (role ? 'Role' : 'Any');
          slots.push({
            allianceIndex,
            partyIndex,
            slotIndex,
            allianceName: alliance.name.trim() || null,
            partyName: party.name.trim() || null,
            requirementType,
            role: role || null,
            mainJob: mainJob || null,
            subJob: subJob || null,
            isPartyLeader: slot.isPartyLeader
          });
        });
      });
    });

    if (slots.length === 0) {
      this.activity.actionError.set('Add at least one alliance with a party and a slot.');
      return;
    }

    const input = {
      linkshellId: this.linkshellId(),
      name,
      eventType,
      assignedMonsterName: this.shouldShowMonsterField() ? (this.assignedMonster || null) : null,
      notes: this.notes.trim() || null,
      slots
    };

    let resultId: number | null;
    if (this.editingId !== null) {
      resultId = (await this.partySetup.update(this.editingId, input)) ? this.editingId : null;
    } else {
      resultId = await this.partySetup.create(input);
    }

    if (resultId !== null) {
      this.closeDialog();
      this.saved.emit(resultId);
    }
  }

  // ----- Dialog plumbing (mirrors tods-tab.component.ts) -----

  private openDialog(): void {
    setTimeout(() => {
      const dialog = this.dialog()?.nativeElement;
      if (dialog && !dialog.open) {
        dialog.showModal();
      }
    });
  }

  protected closeDialog(): void {
    const dialog = this.dialog()?.nativeElement;
    if (dialog && dialog.open) {
      dialog.close();
    }
  }

  protected onDialogClick(event: MouseEvent): void {
    if (event.target === this.dialog()?.nativeElement) {
      this.closeDialog();
    }
  }
}
