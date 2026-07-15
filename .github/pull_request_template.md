<!-- Ticket link, e.g. Closes #123. If there isn't one, write "No linked issue." -->

## Release notes

<!--
The user-facing prose for this release.

Everything between this H2 and the next H2 lands verbatim in manifest.json's
changelog field and as the GitHub Release body. Write one paragraph, present-
tense, no internal jargon (no symbol names, no `MissingMethodException`, no
"PR #50"). Past entries on https://jellyscribe.dev/releases set the tone.

Don't leave this empty. release.yml falls back to the PR title if the section
is missing, which is almost never what end-users browsing their plugin catalog
should see.
-->

(Replace with one paragraph describing what's in this release for users.)

## What's broken

<!--
The symptom in plain terms, where it shows up, who reported it.
For features, frame this as what's missing or the user problem being solved.
-->

## Why it happens

<!--
The root cause in simple terms. Avoid function names, type signatures, or SQL in prose.
A short numbered "two things lined up" list often works well.
-->

1. 
2. 

## What this PR does

<!--
The change in plain terms. Naming files and helpers is fine, but keep it readable.
Note anything deliberately NOT touched and why.
-->

## How to test

<!--
Automated test file plus the command to run it, then a quick manual check.
-->

## Follow-ups (not in this PR)

<!--
Short list of related things left for later.
-->

- 
