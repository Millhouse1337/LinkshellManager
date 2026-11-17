import type { ActivityPlaceholderClaimRequest } from '../../discord/discord-activity.types';

// The banner after an officer approves a claim request. When the player had no free alt slot for
// some of the imported member's names, say which: loot or attendance recorded under those names
// no longer finds the player.
export function claimApprovedMessage(
  request: Pick<ActivityPlaceholderClaimRequest, 'placeholderName' | 'requesterName'>,
  namesNotKept: readonly string[]
): string {
  const approved = `Approved: ${request.placeholderName} now belongs to ${request.requesterName}.`;
  if (namesNotKept.length === 0) {
    return approved;
  }
  const names = namesNotKept.join(', ');
  return `${approved} ${names} could not be kept as an alt (both alt slots are full), `
    + `so loot or attendance under ${namesNotKept.length === 1 ? 'that name' : 'those names'} won't find them.`;
}
