import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { DiscordActivityService } from '../../discord/discord-activity.service';
import { JOIN_REQUEST_STATUS } from '../../discord/discord-activity.types';
import type { ActivityInvite, ActivityLinkshellSearchResult } from '../../discord/discord-activity.types';

/** The longest note the server keeps (JoinRequestService.MaxMessageLength). */
const MAX_NOTE_LENGTH = 200;

/**
 * "Join a linkshell", on the Dashboard's Onboarding page: a player finds a linkshell by name and
 * asks to join it, then sees the answer here. Officers answer in the Linkshell tab's Invites view.
 */
@Component({
  selector: 'app-join-linkshell-panel',
  imports: [CommonModule, FormsModule],
  templateUrl: './join-linkshell-panel.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class JoinLinkshellPanelComponent {
  protected readonly activity = inject(DiscordActivityService);
  protected readonly maxNoteLength = MAX_NOTE_LENGTH;

  protected query = '';
  protected note = '';
  /** Set once a search ran, so "nothing found" only shows after one. */
  protected readonly searched = signal(false);

  protected async search(): Promise<void> {
    if (this.query.trim().length < 2) {
      this.activity.actionError.set('Type at least 2 letters of the linkshell name.');
      this.activity.actionMessage.set(null);
      return;
    }
    await this.activity.searchLinkshells(this.query);
    this.searched.set(true);
  }

  protected async ask(result: ActivityLinkshellSearchResult): Promise<void> {
    if (await this.activity.requestToJoin(result.id, this.note)) {
      this.note = '';
    }
  }

  protected requests(): ActivityInvite[] {
    return this.activity.overview()?.outgoingJoinRequests ?? [];
  }

  protected isPending(request: ActivityInvite): boolean {
    return request.status === JOIN_REQUEST_STATUS.pending;
  }

  protected statusLabel(request: ActivityInvite): string {
    switch (request.status) {
      case JOIN_REQUEST_STATUS.pending:
        return 'Waiting for an officer';
      case JOIN_REQUEST_STATUS.approved:
        return 'Approved';
      case JOIN_REQUEST_STATUS.denied:
        return 'Denied';
      default:
        return request.status;
    }
  }

  protected statusTagClass(request: ActivityInvite): string {
    switch (request.status) {
      case JOIN_REQUEST_STATUS.approved:
        return 'tag success';
      case JOIN_REQUEST_STATUS.denied:
        return 'tag danger';
      default:
        return 'tag warning';
    }
  }

  protected memberCountLabel(count: number): string {
    return count === 1 ? '1 member' : `${count} members`;
  }
}
