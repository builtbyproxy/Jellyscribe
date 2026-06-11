// Telemetry ingest: the SOLE write path into public.pings.
// Contract (see openspec/changes/add-opt-in-telemetry/specs/telemetry-backend):
//  - validate schema_version + shape, reject > 2 KB
//  - never persist IPs into the dataset (transient in-memory rate limiting only)
//  - weekly: upsert on (instance_id, week) MERGING counters, never overwriting
//  - error_transition: per-instance daily cap, 204 on cap hit (plugin never retries)
//  - global + per-IP requests-per-minute caps bound abuse from minted UUIDs

import { createClient } from "jsr:@supabase/supabase-js@2";

const supabase = createClient(
  Deno.env.get("SUPABASE_URL")!,
  Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!,
);

const CATEGORIES = ["cloudflare_403", "auth_failure", "tmdb_lookup", "jellyseerr_error", "other"];
const MAX_BODY_BYTES = 2048;
const PER_IP_PER_MINUTE = 30;
const GLOBAL_PER_MINUTE = 600;

// Transient, in-memory, per-isolate. IPs never touch the database.
const ipHits = new Map<string, { count: number; windowStart: number }>();
let globalHits = { count: 0, windowStart: Date.now() };

function rateLimited(ip: string): boolean {
  const now = Date.now();
  if (now - globalHits.windowStart > 60_000) globalHits = { count: 0, windowStart: now };
  if (++globalHits.count > GLOBAL_PER_MINUTE) return true;

  const entry = ipHits.get(ip);
  if (!entry || now - entry.windowStart > 60_000) {
    ipHits.set(ip, { count: 1, windowStart: now });
    if (ipHits.size > 10_000) ipHits.clear(); // bound memory under flood
    return false;
  }
  return ++entry.count > PER_IP_PER_MINUTE;
}

function weekOfUtc(d: Date): string {
  // Monday-based UTC week start, matching TelemetryService.WeekStartUtc.
  const day = (d.getUTCDay() + 6) % 7;
  const monday = new Date(Date.UTC(d.getUTCFullYear(), d.getUTCMonth(), d.getUTCDate() - day));
  return monday.toISOString().slice(0, 10);
}

function bad(status: number, msg: string): Response {
  return new Response(JSON.stringify({ error: msg }), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

const UUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

Deno.serve(async (req) => {
  if (req.method !== "POST") return bad(405, "POST only");

  const ip = req.headers.get("x-forwarded-for")?.split(",")[0]?.trim() ?? "unknown";
  if (rateLimited(ip)) return bad(429, "rate limited");

  const raw = await req.text();
  if (raw.length > MAX_BODY_BYTES) return bad(413, "payload too large");

  let p: Record<string, unknown>;
  try {
    p = JSON.parse(raw);
  } catch {
    return bad(400, "invalid JSON");
  }

  // Shape validation: exact known fields, nothing extra trusted.
  if (p.schema_version !== 1) return bad(400, "unknown schema_version");
  if (typeof p.instance_id !== "string" || !UUID_RE.test(p.instance_id)) return bad(400, "invalid instance_id");
  if (p.ping_type !== "weekly" && p.ping_type !== "error_transition") return bad(400, "invalid ping_type");
  if (typeof p.plugin_version !== "string" || p.plugin_version.length > 32) return bad(400, "invalid plugin_version");
  if (typeof p.jellyfin_version !== "string" || p.jellyfin_version.length > 32) return bad(400, "invalid jellyfin_version");
  if (typeof p.features !== "object" || p.features === null) return bad(400, "invalid features");
  if (typeof p.buckets !== "object" || p.buckets === null) return bad(400, "invalid buckets");
  if (typeof p.errors !== "object" || p.errors === null) return bad(400, "invalid errors");

  const features = Object.fromEntries(
    Object.entries(p.features as Record<string, unknown>)
      .filter(([, v]) => typeof v === "boolean").slice(0, 24),
  );
  const buckets = Object.fromEntries(
    Object.entries(p.buckets as Record<string, unknown>)
      .filter(([, v]) => typeof v === "string" && (v as string).length <= 16).slice(0, 8),
  );
  const errsIn = p.errors as Record<string, unknown>;
  const errors: Record<string, unknown> = {};
  for (const c of CATEGORIES) {
    errors[c] = typeof errsIn[c] === "number" ? Math.min(Math.max(errsIn[c] as number, 0), 1_000_000) : 0;
  }
  const stateIn = (errsIn.state ?? {}) as Record<string, unknown>;
  errors.state = Object.fromEntries(CATEGORIES.map((c) => [c, stateIn[c] === true]));

  const now = new Date();
  const row = {
    week: weekOfUtc(now),
    instance_id: p.instance_id,
    schema_version: 1,
    plugin_version: p.plugin_version,
    jellyfin_version: p.jellyfin_version,
    ping_type: p.ping_type,
    features,
    buckets,
    errors,
  };

  if (p.ping_type === "error_transition") {
    // Server-side daily cap: 204 and drop silently on hit; the plugin queues
    // and consolidates client-side, so nothing is lost.
    const { count } = await supabase
      .from("pings")
      .select("id", { count: "exact", head: true })
      .eq("instance_id", row.instance_id)
      .eq("ping_type", "error_transition")
      .gte("received_at", new Date(now.getTime() - 86_400_000).toISOString());
    if ((count ?? 0) >= 1) return new Response(null, { status: 204 });

    const { error } = await supabase.from("pings").insert(row);
    if (error) return bad(500, "insert failed");
    return new Response(null, { status: 201 });
  }

  // Weekly: merge counters on conflict rather than overwriting, so a duplicate
  // same-week send can never destroy an earlier window's counts.
  const { data: existing } = await supabase
    .from("pings")
    .select("id, errors")
    .eq("instance_id", row.instance_id)
    .eq("week", row.week)
    .eq("ping_type", "weekly")
    .maybeSingle();

  if (existing) {
    const merged: Record<string, unknown> = { ...errors };
    const prev = (existing.errors ?? {}) as Record<string, unknown>;
    for (const c of CATEGORIES) {
      merged[c] = ((typeof prev[c] === "number" ? prev[c] as number : 0)) + (merged[c] as number);
    }
    const { error } = await supabase
      .from("pings")
      .update({ ...row, errors: merged, received_at: now.toISOString() })
      .eq("id", existing.id);
    if (error) return bad(500, "update failed");
    return new Response(null, { status: 200 });
  }

  const { error } = await supabase.from("pings").insert(row);
  if (error) return bad(500, "insert failed");
  return new Response(null, { status: 201 });
});
