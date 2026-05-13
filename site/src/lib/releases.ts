import manifest from '../../../manifest.json';
import { notesByVersion, type ReleaseNotes } from '../data/release-notes';

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

const repoUrl = 'https://github.com/builtbyproxy/jellyfin-plugin-letterboxd';

function semverKey(v: string): number {
  const [maj, min, patch] = v.split('.').map((n) => parseInt(n, 10) || 0);
  return maj * 1_000_000 + min * 1_000 + patch;
}

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
      notes: notesByVersion[v],
    };
  })
  .sort((a, b) => semverKey(b.version) - semverKey(a.version));

export const latestRelease = releases[0];
