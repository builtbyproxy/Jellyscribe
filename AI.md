# AI transparency

This plugin is **heavily written with AI**. If that matters to you — either way — this page is the honest accounting.

## The short version

Nearly all of the code in this repository (the plugin, the tests, the website, the telemetry worker, the CI pipelines, and most of this documentation) was written by AI coding agents, directed and reviewed by the maintainer. This is a one-person project; the AI is the workforce, the human sets direction, reviews the output, and owns every decision that ships.

## The stack

- **[Claude Code](https://claude.com/claude-code)** (Anthropic's coding agent) is the primary tool — used interactively from the terminal against this repo. Recorded sessions ran on Claude Opus-class models (`claude-opus-4-8` in the currently retained transcripts); earlier development used contemporary Claude 4-family models.
- **[OpenSpec](openspec/)** for spec-driven development: non-trivial changes get an AI-drafted proposal, design, and task breakdown under `openspec/changes/` before any code is written. Past proposals are archived in the repo, so you can read exactly how features were designed.
- **Repo-local agent config**: custom skills and commands the agent uses when working in this repo.
- Additional agent tooling (skill suites for shipping, review, and QA workflows) runs on the maintainer's machine and isn't checked in.

## What keeps the quality honest

AI writes the code; these gates decide whether it ships:

- **Every change goes through a pull request** with a human-reviewed diff — nothing lands on `main` directly except documentation.
- **CI on every PR**: full build, ~590 unit and live-integration tests, coverage tracking via Codecov, manifest integrity validation, and a version gate.
- **Live integration tests** run against the real Letterboxd site, so "the AI's fixture was wrong" gets caught before release (a lesson learned — see the v1.18.4 release notes).
- **Every merge ships a release**, so mistakes are small, attributable, and quickly reverted. Incidents and their prevention are documented in the repo's `CLAUDE.md`.

Bugs still happen — the [issue tracker](https://github.com/builtbyproxy/jellyfin-plugin-letterboxd/issues) and [release history](https://letterboxdsync.dev/releases/) don't hide them.

## The scale of it

Rather than abstract token counts (tried it — the numbers were noise), here is the concrete output of this human-directs-AI-builds arrangement, as of July 2026:

- **173 commits** since the first one on 2026-03-25
- **53 merged pull requests**, every one human-reviewed
- **40 shipped releases** through a fully automated pipeline
- **~18,000 lines of C#** including **~590 tests**
- Plus the [letterboxdsync.dev](https://letterboxdsync.dev/) website, the telemetry worker, and the CI/release automation

All of it AI-written in roughly three months of evenings, by one person who reviews everything and writes almost none of it by hand. The [commit history](https://github.com/builtbyproxy/jellyfin-plugin-letterboxd/commits/main) and [merged PRs](https://github.com/builtbyproxy/jellyfin-plugin-letterboxd/pulls?q=is%3Apr+is%3Amerged) are the receipts.

## Questions

If anything here seems incomplete or misleading, [open an issue](https://github.com/builtbyproxy/jellyfin-plugin-letterboxd/issues) — the point of this page is that you shouldn't have to guess.
