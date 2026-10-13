import manifest from '../../../manifest.json';
import { releaseNotes, type ReleaseNotes } from '../data/release-notes';
import { compareVersions, foldNotes } from './fold-notes';

export type Release = {
  version: string;
  manifestVersion: string;
  date: Date;
  dateLabel: string;
  changelog: string;
  sourceUrl: string;
  githubReleaseUrl: string;
  tag: string;
  anchor: string;
  notes?: ReleaseNotes;
};

type ManifestEntry = {
  version: string;
  changelog: string;
  sourceUrl: string;
  timestamp: string;
  checksum: string;
  targetAbi: string;
};

const manifestEntries = manifest[0].versions as ManifestEntry[];

// Notes for a version that never shipped appear with the next one that did.
const notesByShippedVersion = foldNotes(
  releaseNotes,
  manifestEntries.map((e) => shortVersion(e.version)),
);

const repoUrl = 'https://github.com/builtbyproxy/Jellyscribe';

function shortVersion(manifestVersion: string): string {
  return manifestVersion.replace(/\.0$/, '');
}

function formatDate(d: Date): string {
  return d.toLocaleDateString('en-US', { year: 'numeric', month: 'long', day: 'numeric' });
}

export const releases: Release[] = manifestEntries
  .map((entry): Release => {
    const v = shortVersion(entry.version);
    const date = new Date(entry.timestamp);
    return {
      version: v,
      manifestVersion: entry.version,
      date,
      dateLabel: formatDate(date),
      changelog: entry.changelog,
      sourceUrl: entry.sourceUrl,
      githubReleaseUrl: `${repoUrl}/releases/tag/v${v}`,
      tag: `v${v}`,
      anchor: `v${v.replace(/\./g, '-')}`,
      notes: notesByShippedVersion[v],
    };
  })
  .sort((a, b) => compareVersions(b.version, a.version));

export const latestRelease = releases[0];
