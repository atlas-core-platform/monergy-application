import { expect, it } from 'vitest';
import {
  friendlyActionFailure,
  personaEntryCopy,
  readableDate,
  readableDateTime,
  readableMoney,
} from '../src/ux/personaCopy';

const technicalLanguage = /\b(?:tenant|actor|subject|capability|resource grant|policy version|authorization decision|owner service|runtime|adapter|persistence|postgresql|database|schema|migration|endpoint|http|payload|json|topology|dependency|container|docker|clamav|revision conflict|idempotent|operation receipt|platform control plane|technical acceptance)\b/iu;

it('keeps every persona entry message in business language', () => {
  expect(Object.keys(personaEntryCopy)).toHaveLength(10);
  for (const copy of Object.values(personaEntryCopy)) {
    const visibleCopy = [
      copy.eyebrow,
      ...copy.headline,
      copy.introduction,
      copy.assurance,
      copy.workspaceName,
    ].join(' ');
    expect(visibleCopy).not.toMatch(technicalLanguage);
  }
  expect(friendlyActionFailure).not.toMatch(technicalLanguage);
});

it('formats dates, activity times and money for the India-facing workspace', () => {
  expect(readableDate('2026-09-30T00:00:00Z')).toMatch(/^30 Sept? 2026$/u);
  expect(readableDateTime('2026-09-30T10:30:00Z')).toMatch(/30 Sept? 2026/u);
  expect(readableMoney(52_480, 'INR')).toBe('₹52,480.00');
});
