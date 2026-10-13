// Pairs release-notes.ts entries with the versions that actually shipped. A version
// that never shipped (one push brought several version bumps, so only the newest was
// released, as with 2.12.4 on 2026-10-12) has its notes shown with the lowest shipped
// version above it, so its highlights are not silently dropped. Entries newer than
// every shipped version wait until they ship. Kept free of site imports so it can be
// tested with Node's test runner (site/test/fold-notes.test.mjs).
import type { ReleaseCategory, ReleaseNotes } from '../data/release-notes';

const CATEGORIES: ReleaseCategory[] = ['new', 'improvements', 'fixes', 'breaking'];

export function versionKey(v: string): number[] {
  return v.split('.').map((n) => parseInt(n, 10) || 0);
}

/** Orders versions such as "2.13.0" and "2.12.4" numerically, part by part. */
export function compareVersions(a: string, b: string): number {
  const x = versionKey(a);
  const y = versionKey(b);
  for (let i = 0; i < Math.max(x.length, y.length); i++) {
    const d = (x[i] ?? 0) - (y[i] ?? 0);
    if (d !== 0) return d;
  }
  return 0;
}

function merge(own: ReleaseNotes | undefined, folded: ReleaseNotes[], version: string): ReleaseNotes | undefined {
  if (!own && folded.length === 0) return undefined;
  // The version's own entry comes first, then the folded ones oldest first. Without
  // an entry of its own, the newest folded entry supplies the headline.
  const parts = own ? [own, ...folded] : folded;
  const head = own ?? folded[folded.length - 1];
  const summaries = parts.map((n) => n.summary).filter((s): s is string => !!s);
  const highlights: ReleaseNotes['highlights'] = {};
  for (const key of CATEGORIES) {
    const items = parts.flatMap((n) => n.highlights[key] ?? []);
    if (items.length) highlights[key] = items;
  }
  return {
    ...head,
    version,
    summary: summaries.length ? summaries.join('\n\n') : undefined,
    highlights,
  };
}

/** Notes to show for each shipped version (short form, e.g. "2.13.0"). */
export function foldNotes(notes: ReleaseNotes[], shipped: string[]): Record<string, ReleaseNotes> {
  const shippedSorted = [...shipped].sort(compareVersions);
  const shippedSet = new Set(shipped);
  const own = new Map(notes.filter((n) => shippedSet.has(n.version)).map((n) => [n.version, n]));
  const unshipped = notes.filter((n) => !shippedSet.has(n.version)).sort((a, b) => compareVersions(a.version, b.version));

  const result: Record<string, ReleaseNotes> = {};
  let previous: string | undefined;
  for (const version of shippedSorted) {
    // Oldest first, so summaries and highlights read in the order they were written.
    const folded = unshipped.filter(
      (n) => compareVersions(n.version, version) < 0 && (previous === undefined || compareVersions(n.version, previous) > 0),
    );
    const merged = merge(own.get(version), folded, version);
    if (merged) result[version] = merged;
    previous = version;
  }
  return result;
}
