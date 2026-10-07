// Pure helpers for the install-count GET routes, kept free of Worker bindings so
// worker/test/dl.test.mjs can run them under plain `node`.

// A release tag as release.yml writes it: v2.10.3 (or a four-part v1.2.3.4).
const TAG_RE = /^v\d+(?:\.\d+){2,3}$/;
// The one asset every release ships, named after its own tag.
const ASSET_RE = /^jellyfin-plugin-letterboxd-v\d+(?:\.\d+){2,3}\.zip$/;

export interface DownloadTarget {
  tag: string;
  asset: string;
}

// Parse the RAW (still percent-encoded) path after "/dl/". It must be exactly
// "<tag>/<asset>" with no encoding at all: the allowed characters are digits,
// letters, '.', '-' and one '/', so an encoded slash (%2F) or dot segment (%2E%2E)
// is rejected before anything is decoded. Decoding first is what let
// "/dl/..%2F..%2F<owner>%2F<repo>/..." turn into a redirect to any GitHub path.
export function parseDownloadPath(rawRel: string): DownloadTarget | null {
  if (rawRel.includes("%") || rawRel.includes("..")) return null;
  const parts = rawRel.split("/");
  if (parts.length !== 2) return null;
  const [tag, asset] = parts;
  if (!TAG_RE.test(tag) || !ASSET_RE.test(asset)) return null;
  // The asset is always named after the tag it lives under.
  if (asset !== `jellyfin-plugin-letterboxd-${tag}.zip`) return null;
  return { tag, asset };
}

// Daily ceiling on diagnostic-bundle uploads. The ingest key is public by design,
// so without this a flood of 256 KB bundles could fill the shared D1 database.
// A genuine user sends a handful a year; these limits only bite under abuse.
export const BUNDLES_PER_DAY = 200;
export const BUNDLE_BYTES_PER_DAY = 20 * 1024 * 1024;

export function bundleCapExceeded(todayRows: number, todayBytes: number, incomingBytes: number): boolean {
  return todayRows >= BUNDLES_PER_DAY || todayBytes + incomingBytes > BUNDLE_BYTES_PER_DAY;
}

// "YYYY-MM-DDT00:00:00Z" for the UTC day of d, in log_bundles.received_at's format.
export function utcDayStart(d: Date): string {
  return d.toISOString().slice(0, 10) + "T00:00:00Z";
}
