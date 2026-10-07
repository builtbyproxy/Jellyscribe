// Run from the repo root (Node 22.6+):
//   node --experimental-strip-types --test worker/test/dl.test.mjs
// No dependencies: node:test + node:assert against the pure helpers in src/dl.ts.
import { test } from "node:test";
import { readFileSync } from "node:fs";
import assert from "node:assert/strict";
import {
  parseDownloadPath, bundleCapExceeded, utcDayStart, BUNDLES_PER_DAY, BUNDLE_BYTES_PER_DAY,
} from "../src/dl.ts";

const GH = "https://github.com/builtbyproxy/Jellyscribe/releases/download/";

// The worker hands parseDownloadPath the raw pathname after "/dl/", exactly as
// new URL(req.url).pathname gives it (still percent-encoded).
function rawRel(href) {
  return new URL(href, "https://dl.example").pathname.slice("/dl/".length);
}

test("accepts every manifest-style download path", () => {
  for (const tag of ["v2.10.3", "v1.0.0", "v1.13.0.1"]) {
    const t = parseDownloadPath(`${tag}/jellyfin-plugin-letterboxd-${tag}.zip`);
    assert.deepEqual(t, { tag, asset: `jellyfin-plugin-letterboxd-${tag}.zip` });
  }
});

test("accepts every download URL the real manifest.json advertises", () => {
  const manifest = JSON.parse(readFileSync(new URL("../../manifest.json", import.meta.url), "utf8"));
  const dl = manifest[0].versions.map((v) => new URL(v.sourceUrl)).filter((u) => u.pathname.startsWith("/dl/"));
  assert.ok(dl.length > 0);
  for (const u of dl) assert.notEqual(parseDownloadPath(u.pathname.slice("/dl/".length)), null, u.href);
});

test("rejects the encoded-slash dot-segment open redirect", () => {
  const payload = rawRel("/dl/..%2F..%2F..%2F..%2Fattacker%2Frepo%2Freleases%2Fdownload%2Fv1%2Fevil.zip");
  assert.equal(parseDownloadPath(payload), null);

  // What the old code did with the same payload: decode, pass the loose regex,
  // then prefix the base. The browser resolves the dot segments off-repo.
  const decoded = decodeURIComponent(payload);
  assert.match(decoded, /^[A-Za-z0-9._-]+(?:\/[A-Za-z0-9._-]+)*$/);
  assert.equal(new URL(GH + decoded).pathname.startsWith("/attacker/repo/"), true);
});

test("rejects encoded dots, extra segments and mismatched names", () => {
  const bad = [
    "%2E%2E/%2E%2E/x",
    "v2.10.3%2Fjellyfin-plugin-letterboxd-v2.10.3.zip",
    "v2.10.3/jellyfin-plugin-letterboxd-v2.10.3.zip/extra",
    "v2.10.3/../jellyfin-plugin-letterboxd-v2.10.3.zip",
    "v2.10.3/jellyfin-plugin-letterboxd-v2.10.2.zip",
    "v2.10/jellyfin-plugin-letterboxd-v2.10.zip",
    "latest/jellyfin-plugin-letterboxd-v2.10.3.zip",
    "v2.10.3/other.zip",
    "v2.10.3",
    "",
  ];
  for (const p of bad) assert.equal(parseDownloadPath(p), null, p);
});

test("bundle cap trips on row count or on bytes", () => {
  assert.equal(bundleCapExceeded(0, 0, 262144), false);
  assert.equal(bundleCapExceeded(BUNDLES_PER_DAY - 1, 0, 1), false);
  assert.equal(bundleCapExceeded(BUNDLES_PER_DAY, 0, 1), true);
  assert.equal(bundleCapExceeded(1, BUNDLE_BYTES_PER_DAY - 10, 10), false);
  assert.equal(bundleCapExceeded(1, BUNDLE_BYTES_PER_DAY - 10, 11), true);
});

test("utcDayStart matches the received_at format", () => {
  assert.equal(utcDayStart(new Date("2026-10-07T23:59:59Z")), "2026-10-07T00:00:00Z");
});
