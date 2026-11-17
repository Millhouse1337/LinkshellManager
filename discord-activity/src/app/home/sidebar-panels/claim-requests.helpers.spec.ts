import { describe, expect, it } from 'vitest';

import { claimApprovedMessage } from './claim-requests.helpers';

describe('claimApprovedMessage', () => {
  const request = { placeholderName: 'Otherbob', requesterName: 'Bob' };

  it('says only that it was approved when every name was kept', () => {
    expect(claimApprovedMessage(request, [])).toBe('Approved: Otherbob now belongs to Bob.');
  });

  it('names the one name that could not be kept', () => {
    expect(claimApprovedMessage(request, ['Otherbob'])).toBe(
      'Approved: Otherbob now belongs to Bob. Otherbob could not be kept as an alt (both alt slots are full), '
        + "so loot or attendance under that name won't find them."
    );
  });

  it('lists several names that could not be kept', () => {
    expect(claimApprovedMessage(request, ['Otherbob', 'Thirdbob'])).toContain(
      "Otherbob, Thirdbob could not be kept as an alt (both alt slots are full), so loot or attendance under those names won't find them."
    );
  });
});
