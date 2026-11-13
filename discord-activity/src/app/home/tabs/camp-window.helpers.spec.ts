import { describe, expect, it } from 'vitest';
import type { ActivityAttendanceWindow } from '../../discord/discord-activity.types';
import {
  campClosePosted,
  campPostCount,
  campPostsOpenAndClose,
  campWindowLabel,
  type CampWindowFields
} from './camp-window.helpers';

function posted(...sequenceNumbers: number[]): ActivityAttendanceWindow[] {
  return sequenceNumbers.map(sequenceNumber => ({
    id: sequenceNumber, sequenceNumber, postedAt: '2026-03-01T12:00:00Z', attendees: []
  }));
}

// A Standard king/dragon camp: 7 spawn windows, but only an Open and a Close roster read.
const kingCamp: CampWindowFields = { windowCount: 7, attendancePostCount: 2, postsOpenAndClose: true };
// A custom NM posted Open/Close with no window grid.
const openCloseNm: CampWindowFields = { windowCount: 1, attendancePostCount: 2, postsOpenAndClose: true };
// A Window N camp that posts every window.
const everyWindow: CampWindowFields = { windowCount: 7, attendancePostCount: 7, postsOpenAndClose: false };

describe('campPostCount / campPostsOpenAndClose', () => {
  it('counts roster reads, falling back to windowCount for an older server', () => {
    expect(campPostCount(kingCamp)).toBe(2);
    expect(campPostCount({ windowCount: 5 })).toBe(5);
  });

  it('takes the server flag, else treats exactly two posts as Open/Close', () => {
    expect(campPostsOpenAndClose(kingCamp)).toBe(true);
    expect(campPostsOpenAndClose(everyWindow)).toBe(false);
    expect(campPostsOpenAndClose({ windowCount: 2 })).toBe(true);
    // A Window N camp with exactly two windows is numbered, and the server flag says so.
    expect(campPostsOpenAndClose({ windowCount: 2, postsOpenAndClose: false })).toBe(false);
  });
});

describe('campClosePosted', () => {
  it('is true once window 2 (the Close) is in on an Open/Close camp', () => {
    expect(campClosePosted({ ...openCloseNm, attendanceWindows: posted(1) })).toBe(false);
    expect(campClosePosted({ ...openCloseNm, attendanceWindows: posted(1, 2) })).toBe(true);
    expect(campClosePosted({ ...openCloseNm, attendanceWindows: null })).toBe(false);
  });

  it('never applies to a Window N camp', () => {
    expect(campClosePosted({ ...everyWindow, attendanceWindows: posted(1, 2) })).toBe(false);
  });
});

describe('campWindowLabel', () => {
  it('numbers a Window N camp by the board focus window', () => {
    expect(campWindowLabel({ ...everyWindow, hnmFocusWindow: 3, hnmWindowNumber: 2 })).toBe('Window 3 of 7');
    expect(campWindowLabel({ ...everyWindow, hnmWindowNumber: 2 })).toBe('Window 2 of 7');
    expect(campWindowLabel(everyWindow)).toBe('Window 1 of 7');
  });

  it('keeps "Window N of 7" on a king/dragon even though it posts Open/Close', () => {
    expect(campWindowLabel({ ...kingCamp, hnmFocusWindow: 4 })).toBe('Window 4 of 7');
  });

  it('says Open, then Close, on an Open/Close camp with no window grid', () => {
    expect(campWindowLabel({ ...openCloseNm, hnmFocusWindow: 1 })).toBe('Open');
    expect(campWindowLabel({ ...openCloseNm, hnmFocusWindow: 2 })).toBe('Close');
  });

  it('says Close once the Close is posted, whatever the clock says', () => {
    expect(campWindowLabel({ ...openCloseNm, hnmFocusWindow: 1, attendanceWindows: posted(2) })).toBe('Close');
  });
});
