-- Telemetry storage: one table, RLS deny-all, ingest happens only through the
-- /ingest edge function (service role). See openspec/changes/add-opt-in-telemetry.

create table if not exists public.pings (
    id bigint generated always as identity primary key,
    received_at timestamptz not null default now(),
    -- Computed by the ingest function from UTC arrival time (a generated column
    -- over timestamptz is non-immutable and fails; client clocks drift).
    week date not null,
    instance_id uuid not null,
    schema_version int not null,
    plugin_version text not null,
    jellyfin_version text not null,
    ping_type text not null check (ping_type in ('weekly', 'error_transition')),
    features jsonb not null default '{}'::jsonb,
    buckets jsonb not null default '{}'::jsonb,
    errors jsonb not null default '{}'::jsonb
);

-- One weekly row per instance per week; the ingest function merges counters on
-- conflict so a duplicate send can never destroy a window's counts.
create unique index if not exists pings_weekly_instance_week
    on public.pings (instance_id, week)
    where ping_type = 'weekly';

create index if not exists pings_instance_received on public.pings (instance_id, received_at desc);
create index if not exists pings_type_received on public.pings (ping_type, received_at desc);

-- Deny-all: the anon key shipped in the plugin cannot read or write this table.
-- The edge function uses the service role internally after validating.
alter table public.pings enable row level security;

-- Active installs by version: latest weekly ping per instance.
create or replace view public.latest_per_instance as
select distinct on (instance_id)
    instance_id, received_at, week, plugin_version, jellyfin_version, features, buckets, errors
from public.pings
where ping_type = 'weekly'
order by instance_id, received_at desc;
