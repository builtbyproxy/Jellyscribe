## Context

`main` has one repository ruleset (id 24695636) that blocks deletion and non-fast-forward pushes, with the Admin role as an "always" bypass actor. Merge settings already allow squash only. Nothing requires a pull request or a passing check, so Version Gate, PR Title and build-and-test only advise.

`release.yml` runs on every push to `main` in three jobs: `build` (read-only: reads the version, writes the release notes, builds, tests, packages), `live-check` (the live Letterboxd suite), and `publish` (`contents: write`: creates the GitHub release and pushes a `Release vX.Y.Z (manifest auto-update)` commit to `main` with `GITHUB_TOKEN`). A rule that requires pull requests would refuse that push, which is why CI-2 was deferred. `GITHUB_TOKEN` pushes also start no workflows, so `deploy-docs.yml` listens for completed Release runs through `workflow_run`.

The build job finds the version bump commit with `git log -1 -G'<AssemblyVersion>'` and reads only that one PR's `## Release notes`. When a push carries several bumps, the older ones are silently skipped; the existing warning tells the maintainer to re-run a run that, in the single-push case, never existed. On 2026-10-12, #166 (2.12.4) and #167 (2.13.0) arrived in one push and 2.12.4 vanished.

The site pairs `site/src/data/release-notes.ts` entries with `manifest.json` versions in `site/src/lib/releases.ts`; an entry with no matching manifest version is never shown. The Releases page already splits a summary into paragraphs on blank lines.

The repository has no deploy keys. Its only environment is `github-pages`.

## Goals / Non-Goals

**Goals:**
- Nothing reaches `main` except a squash-merged pull request whose required checks passed on an up-to-date branch, plus the release workflow's own manifest commit.
- A push carrying several version bumps loses no release notes: in the GitHub release, the plugin catalog, or on the site.
- Every new behaviour in the scripts is covered by tests that run on every PR.

**Non-Goals:**
- Publishing each skipped version on its own (see the proposal).
- Requiring the paths-filtered integration check.
- Fixing the stale tag-era requirements in the `release-process` spec.

## Decisions

### 1. The manifest commit is pushed with a deploy key that the ruleset lets bypass

The publish job checks out `main` with `actions/checkout`'s `ssh-key` input set from `RELEASE_DEPLOY_KEY`, so its existing `git push origin HEAD:main` goes over SSH as the deploy key. The ruleset lists `DeployKey` as an "always" bypass actor.

The secret lives in a new `release` environment, not at repository level. The publish job declares `environment: release`, and the environment only allows deployments from `main`, so a workflow on a PR branch cannot read it.

Alternatives considered:
- Add GitHub Actions (the `github-actions` app) as a bypass actor. Any workflow with `contents: write`, including one added in a PR branch that is later merged, could then push past the rules. It is also not reliably offered as a ruleset bypass actor for `GITHUB_TOKEN`.
- A GitHub App with its own token. This is stronger isolation, but it means creating, installing and rotating an app for one commit a release; the deploy key covers the same need.
- A fine-grained personal access token. That ties releases to one person's account and expiry.

A `DeployKey` bypass covers every deploy key on the repository, so the repository must hold only this one write key (it currently holds none). The tasks record that, and the runbook says so.

### 2. Deploy-key pushes start workflows, so the site trigger changes

Unlike `GITHUB_TOKEN`, a deploy-key push starts workflows:
- **`release.yml`** runs again on the manifest commit. Its first step already sees the version listed in `manifest.json` and does nothing, and the `release` concurrency group queues it behind the run that pushed.
- **`ci.yml`** runs on the push, which is harmless.
- **`deploy-docs.yml`** now starts twice: once on the push (its `manifest.json` path filter) and once on `workflow_run`.

`deploy-docs.yml` keeps both triggers, so a release now deploys the site twice. Its `pages` concurrency group already runs deploys one at a time without cancelling, so the second deploy only republishes the same build.

Removing `workflow_run` was considered and rejected. The site would then redeploy only because the push token starts workflows, and falling back to `GITHUB_TOKEN` (the rollback path) would leave the site stale without any failure to notice.

### 3. The ruleset

The existing ruleset is updated, not replaced, so its id and history stay. It gets these rules:
- **`deletion` and `non_fast_forward`:** kept.
- **`pull_request`:** zero required approvals (a solo maintainer cannot approve their own PR), and `allowed_merge_methods: ["squash"]`, matching the repository setting.
- **`required_status_checks`:** `strict_required_status_checks_policy: true`, with contexts `build-and-test`, `Version Gate`, `PR Title` and `Release scripts`, each pinned to the GitHub Actions integration (id 15368) so another app cannot report a check with the same name.

It gets these bypass actors:
- **`DeployKey`:** `always`.
- **`RepositoryRole` 5 (Admin):** changed from `always` to `pull_request`. The maintainer can still merge a PR whose checks are red in an emergency, but cannot push to `main` directly.

Requiring an up-to-date branch matters for Version Gate: it compares the PR against `main` as it was when the check ran, so a stale pass is how two PRs could claim the same version.

### 4. Distinct check names

`version-gate.yml` and `pr-title.yml` both run a job with id `check` and no `name:`, so both report the context `check` and a ruleset cannot require one without the other. They get `name: Version Gate` and `name: PR Title`. The job ids stay unchanged.

### 5. Version bump discovery and combined notes move into a tested script

`.github/scripts/release_notes.py` replaces the inline shell and Python in the build job's "Extract release body" step. For a new release it:

1. Reads the shipped versions from `manifest.json` on `origin/main` and takes the highest.
2. Walks `git log -G'<AssemblyVersion>' -- Directory.Build.props` from `HEAD`. For each commit it reads the `AssemblyVersion` at that commit and at its parent, and counts the commit as a bump only when the version strictly rose. It keeps the bumps whose version is higher than the highest shipped one. Stopping at the shipped version, rather than using `github.event.before`, also covers queued runs that collapsed. The release workflow may run on a later commit than the last bump.
3. Fetches each of those PRs' `## Release notes` section (PR number from the squash subject, through `gh pr view`), oldest first. A bump commit without a PR number or without the section contributes its subject line, as today.
4. Joins the sections with a blank line, then applies the existing cleaning (comments and tags stripped, stray `<` dropped, whitespace squeezed) and the 4000-character cap.
5. Prints a `::notice::` naming every version folded into this release when there is more than one, replacing the misleading "re-run that run" warning.

Requiring an up-to-date branch (decision 3) does not make several bumps in one push impossible. GitHub merges a `gh stack` stack as one push, as it did on 2026-10-12, and stacked PRs are this repository's preferred workflow. Failing such a release was considered and rejected: it would break every stack with two shipping layers.

Repair mode keeps reading the existing release body. The script exposes its PR-body fetcher so tests can feed fixtures. The cleaning, extraction and combining functions are pure and are tested without git. `.github/scripts/test_release_notes.py` (unittest, standard library only) builds throwaway git repositories in a `tempfile.TemporaryDirectory`, passing `-c user.name`, `-c user.email` and `-c init.defaultBranch=main` on every git call so the runner's git configuration cannot change the result. It covers:
- one bump;
- two bumps in one push;
- a later non-bump commit at `HEAD`;
- a missing notes section;
- the cap;
- that a shipped version stops the walk;
- a commit touching the version line without raising it, which is not a bump.

The PR body stays untrusted input. The script only reads it, and it reaches the release through the same cleaning as today.

### 6. Unshipped notes fold into the next shipped release on the site

A pure function in `site/src/lib/fold-notes.ts` takes the notes list and the shipped versions. It returns, for each shipped version, its own notes with the highlights of every unshipped entry between it and the previous shipped version appended per category. Each unshipped summary is added as a further paragraph after the shipped version's summary. If the shipped version has no entry of its own, the newest unshipped entry supplies its headline.

`releases.ts` uses this instead of `notesByVersion[v]`. The page template is unchanged, because it already renders summary paragraphs and highlight lists.

The tests are `site/test/fold-notes.test.mjs`, run with Node's built-in runner and `--experimental-strip-types`, as the worker tests are. One CI job, `Release scripts`, runs both the Python and the site tests on every PR. The worker tests stay in their own job.

### 7. One-off repair for v2.13.0

The v2.13.0 GitHub release body is rewritten to carry the 2.12.4 fix notes followed by the 2.13.0 notes, with `gh release edit --notes-file`.

The 2.13.0 entry in `release-notes.ts` already exists. After decision 6, the orphan 2.12.4 entry folds into it automatically, so no data edit is needed.

The manifest changelog for 2.13.0 stays as published, because only `release.yml` writes `manifest.json`.

## Risks / Trade-offs

- **[Order of rollout]** If the ruleset starts requiring PRs before the deploy-key push is live, the next release cannot push its manifest entry.
  - Mitigation: apply the ruleset only after this PR is merged and a manifest push over the deploy key has succeeded (the tasks order it).
  - Recovery: a failed publish is repaired by re-running it once the key works.
- **[Deploy key leaks]** The key could push to `main` past every rule.
  - Mitigation: it lives only in the `release` environment, limited to `main`, and is read only by the publish job, which runs no repository code.
  - Rotation: replace the key and secret (documented in `CLAUDE.md`).
- **[Any deploy key bypasses]** A second write deploy key added later would also bypass.
  - Mitigation: the runbook says the release key must be the repository's only write deploy key.
- **[Up-to-date requirement slows stacks]** Each layer must be current with `main` before it merges.
  - Mitigation: `gh stack` rebases layers as lower ones merge. The required checks are cheap.
- **[Dependabot PRs]** Open Dependabot PRs report the old check name `check` until they are rebased.
  - Mitigation: Dependabot rebases on request, and they are not merged routinely anyway.
- **[Required check never reported]** A required check that a PR never triggers blocks it forever.
  - Mitigation: all four required checks run on every PR to `main`, with no paths filter. This is the reason the integration check is excluded.

## Migration Plan

1. Create the deploy key, the `release` environment and its secret before merging, because the new publish job needs the key.
2. Prove the key can push. Push a throwaway branch over SSH with the key, then delete the branch. Its first real use on `main` is the next release's publish job.
3. Merge the workflow, script and site changes. The merge itself ships no release (non-shipping diff).
4. Apply the ruleset update with `gh api`. Check that a direct push from the maintainer's account is refused, and that this change's follow-up docs PR shows the four required checks.
5. Repair the v2.13.0 release body.
6. At the next release, confirm the publish job pushed the manifest commit over SSH and that `deploy-docs.yml` ran once, from that push. If the push fails, re-running the publish job repairs the release once the cause is fixed. The manifest entry is the only thing missing in that state.

**Rollback:** set the ruleset back to its previous rules (deletion and non_fast_forward, Admin "always"). Removing the `ssh-key` input returns the publish job to `GITHUB_TOKEN`, and `deploy-docs.yml` keeps working through `workflow_run`.

## Open Questions

- Does a ruleset accept a `DeployKey` bypass actor on this public repository on the current plan? If not, fall back to a GitHub App token (decision 1, alternative 2). This is verified at task 3.1, before anything depends on it.
