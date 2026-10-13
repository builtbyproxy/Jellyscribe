## ADDED Requirements

### Requirement: Notes for an unshipped version appear with the next shipped version

A `site/src/data/release-notes.ts` entry whose version is not in `manifest.json` SHALL be shown on the Releases view as part of the lowest shipped version above it. Its summary MUST follow that version's own summary as further paragraphs, and its highlights MUST be appended to that version's highlights in the same categories. An entry newer than every shipped version MUST NOT be shown until a version at or above it ships.

#### Scenario: A version folded into the next release

- **WHEN** `release-notes.ts` has entries for 2.12.4 and 2.13.0 and `manifest.json` lists 2.13.0 but not 2.12.4
- **THEN** the 2.13.0 entry on the Releases view shows its own headline, its own summary followed by the 2.12.4 summary, and its highlights followed by the 2.12.4 fixes

#### Scenario: The shipped version has no entry of its own

- **WHEN** `manifest.json` lists 2.13.0, `release-notes.ts` has no 2.13.0 entry, and has a 2.12.4 entry
- **THEN** the 2.13.0 entry shows the 2.12.4 headline, summary and highlights

#### Scenario: Notes for a version not released yet

- **WHEN** `release-notes.ts` has an entry for 2.14.0 and the newest manifest version is 2.13.0
- **THEN** the Releases view does not show the 2.14.0 notes
