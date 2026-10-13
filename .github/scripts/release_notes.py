"""Write the release notes for the version release.yml is about to publish.

Usage: release_notes.py <manifest.json from origin/main> <output file>
       release_notes.py --clean <input file> <output file>

The --clean form only cleans and caps existing text: repair mode uses it on the
body of a release that is already published.

The notes are every unshipped version bump's "## Release notes" PR section,
oldest first. A bump is a commit that raised Directory.Build.props's
AssemblyVersion; it is unshipped when its version is higher than every version
in the manifest. Normally that is one bump. When one push brings several (GitHub
merges a gh stack stack as one push, which lost 2.12.4 on 2026-10-12), only the
newest version is released, so the others' notes are folded into it and a
::notice:: names them.

The PR body is untrusted (it can be edited after review), so the combined text
is reduced to plain text and capped before it reaches manifest.json, the
GitHub Release and every user's plugin catalog.
"""
import json
import re
import subprocess
import sys

MAX = 4000
PROPS = "Directory.Build.props"
VERSION_RE = re.compile(r"<AssemblyVersion>([^<]+)")
PR_RE = re.compile(r" \(#(\d+)\)$")
SECTION_RE = re.compile(r"^##\s+Release notes\s*$(.*?)(?=^##\s|\Z)", re.MULTILINE | re.DOTALL)


def version_key(v):
    return tuple(int(p) if p.isdigit() else 0 for p in v.split("."))


def extract_section(body):
    """The text under "## Release notes", up to the next H2, or ''."""
    m = SECTION_RE.search(body or "")
    return m.group(1) if m else ""


def clean(notes):
    """Plain text only: comments and tags stripped (even across lines), then any
    stray '<' dropped so nothing can open a tag, and whitespace squeezed."""
    notes = re.sub(r"<!--.*?(-->|\Z)", "", notes, flags=re.DOTALL)
    notes = re.sub(r"</?[A-Za-z!][^>]*>", "", notes)
    notes = notes.replace("<", "")
    notes = re.sub(r"[ \t]{2,}", " ", notes)
    return re.sub(r"\n{3,}", "\n\n", notes).strip()


def cap(notes, warn):
    if len(notes) <= MAX:
        return notes
    cut = notes.rfind(" ", 0, MAX - 3)
    warn(f"Release notes were longer than {MAX} characters and have been truncated.")
    return notes[:cut if cut > 0 else MAX - 3].rstrip() + "..."


def subject_title(subject):
    """A squash subject without its "(#NN)" suffix."""
    return PR_RE.sub("", subject.split("\n", 1)[0]).strip()


def git(repo, *args):
    return subprocess.run(["git", "-C", repo, *args], check=True, capture_output=True, text=True).stdout


def version_at(repo, rev):
    try:
        text = git(repo, "show", f"{rev}:{PROPS}")
    except subprocess.CalledProcessError:
        return None
    m = VERSION_RE.search(text)
    return m.group(1).strip() if m else None


def unshipped_bumps(repo, shipped, head="HEAD"):
    """(sha, version, subject) for each commit reachable from head that raised
    AssemblyVersion above every shipped version, oldest first."""
    highest = max((version_key(v) for v in shipped), default=())
    bumps = []
    # --topo-order: never let commit dates put an older bump before a newer one.
    for sha in git(repo, "log", "--topo-order", "--format=%H", "-G<AssemblyVersion>", head, "--", PROPS).split():
        version = version_at(repo, sha)
        if not version:
            continue
        if version_key(version) <= highest:
            break  # this version and everything older has shipped
        before = version_at(repo, f"{sha}^")
        if before and version_key(version) <= version_key(before):
            continue  # touched the version line without raising it
        bumps.append((sha, version, git(repo, "log", "-1", "--format=%s", sha).strip()))
    return list(reversed(bumps))


def gh_pr_body(number):
    return subprocess.run(["gh", "pr", "view", str(number), "--json", "body", "--jq", ".body"],
                          check=True, capture_output=True, text=True).stdout


def fetch_section(fetch_body, number, warn):
    """The cleaned notes section of a PR, or '' when the PR can't be read."""
    try:
        return clean(extract_section(fetch_body(number)))
    except (subprocess.CalledProcessError, OSError) as e:
        warn(f"Could not read PR #{number} ({e.__class__.__name__}).")
        return ""


def build_notes(bumps, fetch_body=gh_pr_body, warn=print, notice=print):
    """The cleaned, capped notes for a release made of these bumps (oldest first)."""
    parts = []
    for _sha, version, subject in bumps:
        m = PR_RE.search(subject)
        section = fetch_section(fetch_body, m.group(1), warn) if m else ""
        if not section:
            warn(f"No '## Release notes' text for {version} ({subject_title(subject)}); "
                 "using its PR title. Add the section to keep the changelog consistent.")
            section = subject_title(subject)
        parts.append(section)
    if len(bumps) > 1:
        folded = ", ".join(v for _s, v, _t in bumps[:-1])
        notice(f"One push brought several version bumps: {folded} folded into {bumps[-1][1]}, "
               "whose notes include theirs.")
    return cap(clean("\n\n".join(parts)), warn)


def main(argv):
    warn = lambda m: print(f"::warning::{m}")
    if len(argv) == 4 and argv[1] == "--clean":
        notes = cap(clean(open(argv[2]).read()), warn)
        with open(argv[3], "w") as f:
            f.write(notes + "\n")
        return 0
    if len(argv) != 3:
        sys.stderr.write(__doc__)
        return 2
    manifest = json.load(open(argv[1]))
    shipped = [v["version"] for v in manifest[0]["versions"]]
    bumps = unshipped_bumps(".", shipped)
    if not bumps:
        # No commit raised the version past the manifest: nothing to describe but HEAD.
        head = git(".", "log", "-1", "--format=%s").strip()
        bumps = [("HEAD", version_at(".", "HEAD") or "?", head)]
    print(f"Version bump commits: {', '.join(f'{v} {s[:10]}' for s, v, _ in bumps)}")
    notes = build_notes(bumps, warn=warn, notice=lambda m: print(f"::notice::{m}"))
    with open(argv[2], "w") as f:
        f.write(notes + "\n")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
