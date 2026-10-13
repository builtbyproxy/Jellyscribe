## 1. Release notes across several version bumps

- [x] 1.1 Add `.github/scripts/release_notes.py`. It finds the version bump commits above the highest shipped version, fetches each PR's `## Release notes` (oldest first, with an injectable fetcher), falls back to the subject, cleans and caps the text, and prints a notice naming folded versions.
- [x] 1.2 Add `.github/scripts/test_release_notes.py` (unittest, throwaway git repos). Cover: one bump; two bumps in one push; a later non-bump commit at HEAD; a missing notes section; no PR number; the 4000-character cap; a shipped version stopping the walk; a version-line change that is not a rise; comment and tag stripping. Git calls pass their own identity and default branch.
- [x] 1.3 Replace the build job's inline notes logic in `release.yml` with the script, keeping repair mode's reuse of the existing release body.

## 2. Unshipped notes on the site

- [x] 2.1 Add `site/src/lib/fold-notes.ts` (pure) and use it in `site/src/lib/releases.ts` in place of `notesByVersion[v]`.
- [x] 2.2 Add `site/test/fold-notes.test.mjs` (Node test runner). Cover: folding into a shipped version, a shipped version without its own entry, an entry newer than every shipped version, and several unshipped entries in order.
- [x] 2.3 Build the site and check the v2.13.0 entry on the Releases page shows the folded 2.12.4 notes. Take before and after screenshots.

## 3. Push the manifest commit with a deploy key

- [x] 3.1 Confirm the ruleset API accepts a `DeployKey` bypass actor (dry check against the API docs and the live ruleset schema) before anything depends on it.
- [x] 3.2 Generate an ed25519 key pair. Add the public half as the repository's only write deploy key ("Release manifest push"), create the `release` environment limited to `main`, and store the private half as its `RELEASE_DEPLOY_KEY` secret.
- [x] 3.3 Prove the key can push: push a throwaway branch over SSH with the key, delete the branch, then delete the local private key.
- [x] 3.4 In `release.yml`'s publish job, add `environment: release` and check out `main` with `ssh-key: ${{ secrets.RELEASE_DEPLOY_KEY }}`.
- [x] 3.5 Update `deploy-docs.yml`'s trigger comment: the manifest push now starts it too, and `workflow_run` stays so a fallback to `GITHUB_TOKEN` cannot leave the site stale.

## 4. Distinct required check names and the CI job

- [x] 4.1 Name the jobs `Version Gate` (`version-gate.yml`) and `PR Title` (`pr-title.yml`).
- [x] 4.2 Add a `Release scripts` job to `ci.yml` that runs the Python and site tests on every PR and push.

## 5. Docs

- [x] 5.1 Update `CLAUDE.md`'s Releasing section:
   - step 3: the notes now come from `release_notes.py` and combine every unshipped bump;
   - step 4: deploy-docs also fires on the manifest push, and why `workflow_run` stays;
   - a new "Merge gates on main" subsection: the ruleset rules, required checks, bypass actors, the `release` environment and deploy key (only write deploy key, rotation, rollback);
   - the `release-notes.ts` rule: an unshipped version's entry folds into the next release;
   - a Past incidents entry for 2.12.4.
- [x] 5.2 Update the PR template's comment if it mentions the old single-version behaviour. (2026-10-12: checked, it does not; no change.)

## 6. Roll out

- [x] 6.1 Open the PR (non-shipping: no version bump or release notes) and get CI green. (2026-10-12: #169, all checks green.)
- [x] 6.2 After it merges, update ruleset 24695636. Add `pull_request` (squash only, no approvals) and `required_status_checks` (strict; `build-and-test`, `Version Gate`, `PR Title`, `Release scripts`, all from integration 15368). Set the bypass actors to `DeployKey` always and Admin `pull_request`. (2026-10-13: applied with `gh api` right after #169 merged as 7772a90; the ruleset now lists deletion, non_fast_forward, pull_request and required_status_checks, with bypass DeployKey always and Admin pull_request.)
- [x] 6.3 Verify. A direct push to `main` from the maintainer's account is refused, and a PR shows the four required checks. (2026-10-13: a direct push of an empty commit to `main` was refused with "Changes must be made through a pull request" and "4 of 4 required status checks are expected"; #170 showed BLOCKED until its four required checks passed.)
- [x] 6.4 Rewrite the v2.13.0 GitHub release body to carry the 2.12.4 notes followed by the 2.13.0 notes. (2026-10-13: `gh release edit v2.13.0` with #166's section followed by the published 2.13.0 text, cleaned and capped by `release_notes.py`.)
- [x] 6.5 At the next release, confirm the manifest commit was pushed by the deploy key and `deploy-docs.yml` ran once. Then archive this change. (2026-10-13: v2.13.1's manifest commit de93a5f landed on `main` under the new ruleset, which only a deploy key may push past, and its push started Deploy site, which a `GITHUB_TOKEN` push cannot. Deploy site ran three times, not once: the push trigger, plus `workflow_run` for both Release runs, because the deploy-key push also starts Release, which then skips. The `pages` group cancelled the superseded queued run and the site served 2.13.1. v2.13.2 (e78227c) behaved the same. The deploy-docs comment and CLAUDE.md now say so.)
- [x] 6.6 Mark audit finding CI-2 as fixed in the audit tracker. (2026-10-13: CI-2 marked merged in the audit tracker, and its status note updated.)
