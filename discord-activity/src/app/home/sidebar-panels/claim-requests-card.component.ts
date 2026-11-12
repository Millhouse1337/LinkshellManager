import { ChangeDetectionStrategy, Component, effect, inject, input, signal } from '@angular/core';

import { ActivityHttpClient } from '../../discord/activity-http.client';
import { AuthService } from '../../discord/auth.service';
import { formatActionError } from '../../discord/discord-activity.helpers';
import type { ActivityPlaceholderClaimRequest } from '../../discord/discord-activity.types';

// Roster card for officers: people asking to take over an imported (placeholder) member by name.
// A character name is public, so the server only files the request; Approve here is what hands
// that member's DKP over (or, for a Discord sign-up, links them to that Discord user). Hidden when
// nothing is waiting. The parent shows it only to members with "Manage members"; the server checks
// that permission again on every call.
@Component({
  selector: 'app-claim-requests-card',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (requests().length > 0) {
      <section class="claim-requests" aria-label="Claim requests">
        <div class="panel__header">
          <h3>Claim requests</h3>
          <span class="pill">{{ requests().length }}</span>
        </div>
        <p class="muted claim-requests__hint">
          These people say an imported member is them. Approve only if you know it's true:
          approving gives them that member's DKP.
        </p>
        <ul class="claim-requests__list">
          @for (request of requests(); track request.id) {
            <li class="claim-requests__row">
              <span class="claim-requests__text">
                <strong>{{ request.requesterName }}</strong>
                {{ request.requesterKind === 'discord' ? '(Discord sign-up)' : '' }}
                wants <strong>{{ request.placeholderName }}</strong> · {{ request.placeholderDkp }} DKP
              </span>
              <span class="button-cluster">
                <button type="button" class="action-button"
                  [disabled]="busyId() !== null" (click)="resolve(request, true)">
                  {{ busyId() === request.id ? 'Saving…' : 'Approve' }}
                </button>
                <button type="button" class="action-button action-button--secondary"
                  [disabled]="busyId() !== null" (click)="resolve(request, false)">
                  Deny
                </button>
              </span>
            </li>
          }
        </ul>
      </section>
    }
  `,
  styles: [`
    .claim-requests { margin: 12px 0; }
    .claim-requests__hint { margin: 4px 0 8px; font-size: 12px; }
    .claim-requests__list { list-style: none; margin: 0; padding: 0; display: grid; gap: 6px; }
    .claim-requests__row {
      display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: 8px;
    }
    .claim-requests__text { min-width: 0; overflow-wrap: anywhere; }
  `]
})
export class ClaimRequestsCardComponent {
  readonly linkshellId = input.required<number>();

  private readonly http = inject(ActivityHttpClient);
  private readonly auth = inject(AuthService);

  protected readonly requests = signal<ActivityPlaceholderClaimRequest[]>([]);
  protected readonly busyId = signal<number | null>(null);

  public constructor() {
    effect(() => {
      void this.load(this.linkshellId());
    });
  }

  protected async resolve(request: ActivityPlaceholderClaimRequest, approve: boolean): Promise<void> {
    const linkshellId = this.linkshellId();
    this.busyId.set(request.id);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);
    try {
      await this.http.postActivityJson(
        `/api/activity/linkshells/${linkshellId}/claim-requests/${request.id}/${approve ? 'approve' : 'deny'}`
      );
      this.auth.setActionMessage(
        approve
          ? `Approved: ${request.placeholderName} now belongs to ${request.requesterName}.`
          : `Denied ${request.requesterName}'s request for ${request.placeholderName}.`
      );
      if (approve) {
        // The roster and DKP changed (a member moved or got linked).
        await this.auth.refreshOverview();
      }
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Updating the claim request failed.'));
    } finally {
      this.busyId.set(null);
      await this.load(linkshellId);
    }
  }

  private async load(linkshellId: number): Promise<void> {
    try {
      const result = await this.http.fetchActivityJson<{ requests: ActivityPlaceholderClaimRequest[] }>(
        `/api/activity/linkshells/${linkshellId}/claim-requests`
      );
      // A slow answer for a linkshell the officer has since switched away from is dropped.
      if (this.linkshellId() === linkshellId) {
        this.requests.set(result?.requests ?? []);
      }
    } catch {
      // Non-fatal: no card is shown when the list can't be read.
      if (this.linkshellId() === linkshellId) {
        this.requests.set([]);
      }
    }
  }
}
