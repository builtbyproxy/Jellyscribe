// Run: node --experimental-strip-types --test site/test/fold-notes.test.mjs (Node 22.6+)
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { compareVersions, foldNotes } from '../src/lib/fold-notes.ts';

const fix = {
  version: '2.12.4',
  headline: 'Watched titles are not requested again',
  summary: 'Fix summary.',
  highlights: { fixes: ['Skips watched titles.'] },
};
const feat = {
  version: '2.13.0',
  headline: 'Choose whether watched titles are requested again',
  summary: 'Feature summary.',
  highlights: { new: ['A new option.'] },
};

test('notes for a version that never shipped join the next shipped version', () => {
  const out = foldNotes([feat, fix], ['2.12.3', '2.13.0']);
  assert.equal(out['2.13.0'].headline, feat.headline);
  assert.equal(out['2.13.0'].summary, 'Feature summary.\n\nFix summary.');
  assert.deepEqual(out['2.13.0'].highlights, { new: ['A new option.'], fixes: ['Skips watched titles.'] });
  assert.equal(out['2.12.4'], undefined);
});

test('a shipped version without its own entry takes the folded one', () => {
  const out = foldNotes([fix], ['2.12.3', '2.13.0']);
  assert.equal(out['2.13.0'].headline, fix.headline);
  assert.equal(out['2.13.0'].version, '2.13.0');
  assert.deepEqual(out['2.13.0'].highlights, fix.highlights);
});

test('notes newer than every shipped version wait until they ship', () => {
  const out = foldNotes([feat, { ...fix, version: '2.14.0' }], ['2.13.0']);
  assert.deepEqual(Object.keys(out), ['2.13.0']);
  assert.deepEqual(out['2.13.0'].highlights, feat.highlights);
});

test('several unshipped versions fold oldest first, and only into the next release', () => {
  const a = { version: '2.12.4', headline: 'A', summary: 'A.', highlights: { fixes: ['a'] } };
  const b = { version: '2.12.5', headline: 'B', summary: 'B.', highlights: { fixes: ['b'] } };
  const older = { version: '2.11.9', headline: 'Old', highlights: { fixes: ['old'] } };
  const out = foldNotes([feat, b, a, older], ['2.11.0', '2.12.0', '2.13.0']);
  assert.equal(out['2.13.0'].summary, 'Feature summary.\n\nA.\n\nB.');
  assert.deepEqual(out['2.13.0'].highlights.fixes, ['a', 'b']);
  assert.equal(out['2.12.0'].headline, 'Old');
  assert.equal(out['2.11.0'], undefined);
});

test('shipped versions with their own notes and nothing to fold are unchanged', () => {
  const out = foldNotes([fix, feat], ['2.12.4', '2.13.0']);
  assert.deepEqual(out['2.12.4'], fix);
  assert.deepEqual(out['2.13.0'], feat);
});

test('without an entry of its own, folded notes read oldest first under the newest headline', () => {
  const a = { version: '2.12.4', headline: 'A', summary: 'A.', highlights: { fixes: ['a'] } };
  const b = { version: '2.12.5', headline: 'B', summary: 'B.', highlights: { fixes: ['b'] } };
  const out = foldNotes([b, a], ['2.12.3', '2.13.0']);
  assert.equal(out['2.13.0'].headline, 'B');
  assert.equal(out['2.13.0'].summary, 'A.\n\nB.');
  assert.deepEqual(out['2.13.0'].highlights.fixes, ['a', 'b']);
});

test('compareVersions orders numerically, part by part', () => {
  assert.deepEqual(['2.9.0', '2.13.0', '2.12.4', '10.0.0'].sort(compareVersions), ['2.9.0', '2.12.4', '2.13.0', '10.0.0']);
  assert.equal(compareVersions('2.13.0', '2.13.0.0'), 0);
});
