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
  protected readonly activity = inject(DiscordActivityService);
  protected readonly partySetup = inject(PartySetupService);

  readonly linkshellId = input.required<number>();
  readonly saved = output<number>();

  private readonly dialog = viewChild<ElementRef<HTMLDialogElement>>('editorDialog');

  protected readonly MAX_PARTIES = 3;
  protected readonly MAX_SLOTS = 6;
  protected readonly ANY_ROLE = 'Any Role';

  protected editingId: number | null = null;
  protected name = '';
  protected assignedMonster = '';
  protected notes = '';
  protected alliances: EditorAlliance[] = [];

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

  openForCreate(): void {
    this.editingId = null;
    this.name = '';
    this.assignedMonster = '';
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
    this.assignedMonster = detail.assignedMonsterName ?? '';
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
      assignedMonsterName: this.assignedMonster || null,
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
