"""Tests for release_notes.py. Run: python3 -m unittest discover -s .github/scripts -p 'test_*.py'"""
import os
import subprocess
import tempfile
import unittest

import release_notes as rn

GIT = ["git", "-c", "user.name=test", "-c", "user.email=test@example.com",
       "-c", "init.defaultBranch=main", "-c", "commit.gpgsign=false"]
PROPS = "<Project>\n  <PropertyGroup>\n    <AssemblyVersion>{}</AssemblyVersion>\n  </PropertyGroup>\n</Project>\n"


class Repo:
    """A throwaway git repository whose commits set Directory.Build.props."""

    def __init__(self, path):
        self.path = path
        self.run("init", "-q")

    def run(self, *args):
        return subprocess.run(GIT + ["-C", self.path, *args], check=True, capture_output=True, text=True).stdout

    def commit(self, subject, version=None, other=None):
        if version:
            with open(os.path.join(self.path, rn.PROPS), "w") as f:
                f.write(PROPS.format(version))
        if other:
            with open(os.path.join(self.path, "notes.txt"), "a") as f:
                f.write(other + "\n")
        self.run("add", "-A")
        self.run("commit", "-q", "-m", subject)
        return self.run("rev-parse", "HEAD").strip()


def bodies(mapping):
    return lambda number: mapping.get(str(number), "")


def silent(_msg):
    pass


class PureFunctionTests(unittest.TestCase):
    def test_extract_section_stops_at_next_h2(self):
        body = "Closes #1\n\n## Release notes\n\nIt works now.\n\n## What's broken\n\nIt didn't."
        self.assertEqual(rn.extract_section(body).strip(), "It works now.")

    def test_extract_section_missing(self):
        self.assertEqual(rn.extract_section("## Summary\n\nnothing"), "")

    def test_clean_strips_comments_tags_and_stray_brackets(self):
        dirty = "<!-- hidden\nacross lines -->Fixed <b>it</b> <script>x</script> a < b  now\n\n\n\nok"
        self.assertEqual(rn.clean(dirty), "Fixed it x a b now\n\nok")

    def test_cap_truncates_on_a_word_and_warns(self):
        warnings = []
        out = rn.cap("word " * 1000, warnings.append)
        self.assertLessEqual(len(out), rn.MAX)
        self.assertTrue(out.endswith("..."))
        self.assertEqual(len(warnings), 1)

    def test_subject_title_drops_pr_number(self):
        self.assertEqual(rn.subject_title("fix: thing (#166)"), "fix: thing")


class BumpDiscoveryTests(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.repo = Repo(self._tmp.name)
        self.repo.commit("Release v2.12.3", version="2.12.3.0")

    def tearDown(self):
        self._tmp.cleanup()

    def found(self, shipped=("2.12.3.0",)):
        return [(v, s) for _sha, v, s in rn.unshipped_bumps(self.repo.path, list(shipped))]

    def test_one_bump(self):
        self.repo.commit("fix: one (#166)", version="2.12.4.0")
        self.assertEqual(self.found(), [("2.12.4.0", "fix: one (#166)")])

    def test_two_bumps_in_one_push_oldest_first(self):
        self.repo.commit("fix: one (#166)", version="2.12.4.0")
        self.repo.commit("feat: two (#167)", version="2.13.0.0")
        self.assertEqual(self.found(), [("2.12.4.0", "fix: one (#166)"), ("2.13.0.0", "feat: two (#167)")])

    def test_later_non_bump_commit_at_head(self):
        self.repo.commit("feat: two (#167)", version="2.13.0.0")
        self.repo.commit("docs: readme (#168)", other="docs")
        self.assertEqual(self.found(), [("2.13.0.0", "feat: two (#167)")])

    def test_shipped_version_stops_the_walk(self):
        self.repo.commit("fix: one (#166)", version="2.12.4.0")
        self.repo.commit("feat: two (#167)", version="2.13.0.0")
        self.assertEqual(self.found(shipped=("2.12.3.0", "2.12.4.0")), [("2.13.0.0", "feat: two (#167)")])

    def test_version_line_change_that_is_not_a_rise_is_not_a_bump(self):
        # A commit that rewrites the version line to a lower (still unshipped) version
        # matches -G but raised nothing, so it contributes no notes.
        self.repo.commit("feat: two (#167)", version="2.13.0.0")
        self.repo.commit("revert: back to 2.12.9 (#170)", version="2.12.9.0")
        self.assertEqual(self.found(), [("2.13.0.0", "feat: two (#167)")])

    def test_nothing_unshipped(self):
        self.assertEqual(self.found(), [])


class BuildNotesTests(unittest.TestCase):
    BODY_A = "Closes #165\n\n## Release notes\n\nWatched titles are skipped.\n\n## What's broken\n\nx"
    BODY_B = "## Release notes\n\nA new option.\n<!-- template comment -->\n"

    def test_single_bump_uses_its_section(self):
        notes = rn.build_notes([("a", "2.13.0.0", "feat: two (#167)")], bodies({"167": self.BODY_B}), silent, silent)
        self.assertEqual(notes, "A new option.")

    def test_two_bumps_combine_oldest_first_and_notice(self):
        notices = []
        notes = rn.build_notes(
            [("a", "2.12.4.0", "fix: one (#166)"), ("b", "2.13.0.0", "feat: two (#167)")],
            bodies({"166": self.BODY_A, "167": self.BODY_B}), silent, notices.append)
        self.assertEqual(notes, "Watched titles are skipped.\n\nA new option.")
        self.assertEqual(len(notices), 1)
        self.assertIn("2.12.4.0 folded into 2.13.0.0", notices[0])

    def test_missing_section_falls_back_to_title_and_warns(self):
        warnings = []
        notes = rn.build_notes([("a", "2.13.0.0", "feat: two (#167)")], bodies({"167": "no section"}), warnings.append, silent)
        self.assertEqual(notes, "feat: two")
        self.assertEqual(len(warnings), 1)

    def test_no_pr_number_falls_back_to_subject(self):
        notes = rn.build_notes([("a", "2.13.0.0", "Hotfix pushed by hand")], bodies({}), silent, silent)
        self.assertEqual(notes, "Hotfix pushed by hand")

    def test_combined_notes_are_capped(self):
        long_body = "## Release notes\n\n" + "word " * 600
        notes = rn.build_notes(
            [("a", "2.12.4.0", "fix: one (#1)"), ("b", "2.13.0.0", "feat: two (#2)")],
            bodies({"1": long_body, "2": long_body}), silent, silent)
        self.assertLessEqual(len(notes), rn.MAX)
        self.assertTrue(notes.endswith("..."))


if __name__ == "__main__":
    unittest.main()
