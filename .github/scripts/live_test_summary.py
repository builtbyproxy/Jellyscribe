"""Summarise a live Letterboxd test run (TRX) for the workflow summary.

Usage: live_test_summary.py <results.trx> <cookie secret set: true|false>

Writes markdown to $GITHUB_STEP_SUMMARY (stdout when unset) and prints a
::warning:: when the website-login (scraping) path was not exercised.
Skip reasons are shown only for the known missing-cookie skip; any other
reason (a Cloudflare or sign-in error) stays in the test log, so nothing
from an error message is copied into the public summary.
"""
import os
import sys
import xml.etree.ElementTree as ET

NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
# Must stay a substring of ScrapingLiveTest.NoCookiesSkipMessage
# (LetterboxdSync.Tests/Integration/ScrapingLiveTest.cs). xUnit's skip reason
# lands in the TRX as Output/ErrorInfo/Message on a NotExecuted result.
NO_COOKIES = "LETTERBOXD_TEST_RAW_COOKIES is not set"


def main() -> int:
    trx, have_cookies = sys.argv[1], sys.argv[2] == "true"
    results = []
    if os.path.exists(trx):
        for r in ET.parse(trx).getroot().iterfind(".//t:UnitTestResult", NS):
            msg = r.find("t:Output/t:ErrorInfo/t:Message", NS)
            results.append((r.get("testName", ""), r.get("outcome", ""),
                            msg.text if msg is not None and msg.text else ""))

    def count(outcome):
        return sum(1 for _, o, _ in results if o == outcome)

    scraping = [r for r in results if ".Scraping_" in r[0]]
    covered = bool(scraping) and all(o == "Passed" for _, o, _ in scraping)

    lines = ["## Live Letterboxd tests", ""]
    if not results:
        lines.append("No test results were produced (the run failed before the tests finished).")
    else:
        lines.append(f"Passed {count('Passed')}, failed {count('Failed')}, "
                     f"skipped {count('NotExecuted')}, total {len(results)}.")
    lines.append("")
    if covered:
        lines.append("Website-login fallback (scraping path): **covered**.")
    else:
        lines.append("Website-login fallback (scraping path): **not covered** by this run.")
        for name, outcome, msg in scraping:
            short = name.rsplit(".", 1)[-1]
            if outcome == "NotExecuted" and NO_COOKIES in msg:
                why = "skipped: the LETTERBOXD_TEST_RAW_COOKIES secret is not set"
            elif outcome == "NotExecuted":
                why = "skipped: the website sign-in failed (see the test log)"
            else:
                why = outcome.lower() or "did not run"
            lines.append(f"- `{short}`: {why}")
        if not results:
            hint = "See the run log for why the tests did not finish."
        elif any(o == "Failed" for _, o, _ in scraping):
            hint = ("A scraping test failed: Letterboxd may have changed its website markup. "
                    "See the test log.")
        elif have_cookies:
            hint = ("The cookie secret is set but the website sign-in did not get through. The "
                    "Cloudflare cookie has probably expired: refresh LETTERBOXD_TEST_RAW_COOKIES "
                    "(CLAUDE.md, Build & Test).")
        else:
            hint = "Set the LETTERBOXD_TEST_RAW_COOKIES secret to cover it (CLAUDE.md, Build & Test)."
        lines += ["", hint]
        print(f"::warning::Website-login fallback not covered by the live tests. {hint}")

    text = "\n".join(lines) + "\n"
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as f:
            f.write(text)
    else:
        sys.stdout.write(text)
    return 0


if __name__ == "__main__":
    sys.exit(main())
