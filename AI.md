# AI transparency

This plugin is **heavily written with AI**. If that matters to you — either way — this page is the honest accounting.

## The short version

Nearly all of the code in this repository (the plugin, the tests, the website, the telemetry worker, the CI pipelines, and most of this documentation) was written by AI coding agents, directed and reviewed by the maintainer. This is a one-person project; the AI is the workforce, the human sets direction, reviews the output, and owns every decision that ships.

## The stack

- **[Claude Code](https://claude.com/claude-code)** (Anthropic's coding agent) is the primary tool — used interactively from the terminal against this repo. Recorded sessions ran on Claude Opus-class models (`claude-opus-4-8` in the currently retained transcripts); earlier development used contemporary Claude 4-family models.
- **[OpenSpec](openspec/)** for spec-driven development: non-trivial changes get an AI-drafted proposal, design, and task breakdown under `openspec/changes/` before any code is written. Past proposals are archived in the repo, so you can read exactly how features were designed.
- **Repo-local agent config** in [`.claude/`](.claude/): custom skills and commands the agent uses when working here, plus the hook that keeps the usage numbers below current.
- Additional agent tooling (skill suites for shipping, review, and QA workflows) runs on the maintainer's machine and isn't checked in.

## What keeps the quality honest

AI writes the code; these gates decide whether it ships:

- **Every change goes through a pull request** with a human-reviewed diff — nothing lands on `main` directly except documentation.
- **CI on every PR**: full build, ~590 unit and live-integration tests, coverage tracking via Codecov, manifest integrity validation, and a version gate.
- **Live integration tests** run against the real Letterboxd site, so "the AI's fixture was wrong" gets caught before release (a lesson learned — see the v1.18.4 release notes).
- **Every merge ships a release**, so mistakes are small, attributable, and quickly reverted. Incidents and their prevention are documented in the repo's `CLAUDE.md`.

Bugs still happen — the [issue tracker](https://github.com/builtbyproxy/jellyfin-plugin-letterboxd/issues) and [release history](https://letterboxdsync.dev/releases/) don't hide them.

## Token usage

These numbers are generated from Claude Code's local session transcripts by [`scripts/update-ai-usage.py`](scripts/update-ai-usage.py), which runs automatically at the end of every agent session in this repo (see [`.claude/settings.json`](.claude/settings.json)) and folds each session into a committed ledger at [`docs/ai-usage.json`](docs/ai-usage.json).

<!-- AI-USAGE:START -->
_Tracked since **2026-06-29** across **1** recorded session(s); last updated **2026-07-02** (UTC). Counts are a floor — see the caveats below the table._

| Model | Input | Output | Cache write | Cache read |
|---|---:|---:|---:|---:|
| `claude-opus-4-8` | 43,434 | 21,393 | 130,386 | 2,139,019 |
| **Total** | **43,434** | **21,393** | **130,386** | **2,139,019** |
<!-- AI-USAGE:END -->

Caveats, so the numbers aren't oversold:

- **Tracking began 2026-06-29.** The project started 2026-03-25 and has 170+ commits; transcripts from the first three months were pruned before tracking existed, so the true lifetime totals are much higher than shown.
- Sessions run from outside this repo's directory (e.g. one-off fixes made from another project) aren't captured. The numbers are a floor, not a total.
- **Cache reads dominate** and are the cheapest kind of token — they're the agent re-reading its own context, not fresh work. "Output" is the closest proxy for text/code actually produced.
- Tokens measure conversation volume, not shipped code. A long debugging session can burn millions of tokens and produce a two-line fix.

## Questions

If anything here seems incomplete or misleading, [open an issue](https://github.com/builtbyproxy/jellyfin-plugin-letterboxd/issues) — the point of this page is that you shouldn't have to guess.
