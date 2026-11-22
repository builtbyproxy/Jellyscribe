# AI transparency

This plugin is **heavily written with AI**. If that matters to you, either way, this page is the honest accounting.

## The short version

Nearly all of the code in this repository (the plugin, the tests, the website, the telemetry worker, the CI pipelines, and most of this documentation) was written by AI coding agents, directed and reviewed by the maintainer. This is a one-person project; the AI is the workforce, the human sets direction, reviews the output, and owns every decision that ships.

## The stack

- **[Claude Code](https://claude.com/claude-code)** (Anthropic's coding agent) is the primary tool, used interactively from the terminal against this repo. The currently retained session transcripts ran on `claude-opus-5-5` and `claude-fable-5-2`; earlier development used the Claude models current at the time.
- **[OpenSpec](openspec/)** for spec-driven development: every change to plugin code, bug fixes included, gets an AI-drafted proposal, design, and task breakdown under `openspec/changes/` before any code is written (the rule since November 2026; before that it covered non-trivial changes only). Past proposals are archived in the repo, so you can read exactly how features were designed.
- Additional agent tooling (custom skills, commands, and workflow suites for shipping, review, and QA) runs on the maintainer's machine and isn't checked in; the checked-in guidance the agent works from is [`CLAUDE.md`](CLAUDE.md).

## What keeps the quality honest

AI writes the code; these gates decide whether it ships:

- **Everything reaches `main` through a pull request** with a human-reviewed diff. A repository ruleset blocks direct pushes and requires the build and tests, the version gate, the PR title check and the release-script tests to pass first; the only direct push is the release job recording a new version in `manifest.json`. Early development pushed straight to `main`, as the commit history shows.
- **CI on every PR**: full build, about 1,500 unit tests, coverage tracking via Codecov, manifest integrity validation, and a version gate. PRs that touch plugin code additionally run live integration tests against the real Letterboxd site.
- **Live integration tests** hit the real Letterboxd site, so "the AI's fixture was wrong" gets caught before release (a lesson learned, see the v1.18.4 release notes). They run again every Monday against `main`, and once more on the exact commit being released: a release is not published unless they pass.
- **Every merge that changes the plugin ships a release**, so mistakes are small, attributable, and quickly reverted. Incidents and their prevention are documented in the repo's `CLAUDE.md`.

Bugs still happen, the [issue tracker](https://github.com/builtbyproxy/Jellyscribe/issues) and [release history](https://jellyscribe.dev/releases/) don't hide them.

## The scale of it

Rather than abstract token counts (tried it, the numbers were noise), here is the concrete output of this human-directs-AI-builds arrangement, as of November 2026:

- **320+ commits** since the first one on 2026-03-25
- **140+ merged pull requests**, every one human-reviewed
- **92 shipped releases**, every plugin change auto-ships through the release pipeline since May 2026 (earlier releases were tagged by hand)
- **~46,000 lines of C#**, about 16,000 in the plugin and 30,000 in its **~1,500 tests**
- Plus the [jellyscribe.dev](https://jellyscribe.dev/) website, the telemetry worker, and the CI/release automation

All of it AI-written in roughly eight months of evenings, by one person who reviews everything and writes almost none of it by hand. The [commit history](https://github.com/builtbyproxy/Jellyscribe/commits/main) and [merged PRs](https://github.com/builtbyproxy/Jellyscribe/pulls?q=is%3Apr+is%3Amerged) are the receipts.

## Questions

If anything here seems incomplete or misleading, [open an issue](https://github.com/builtbyproxy/Jellyscribe/issues), the point of this page is that you shouldn't have to guess.
