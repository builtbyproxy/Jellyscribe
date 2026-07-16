# Visual baselines

Manual reference captures, not wired into any automated test or CI check.
`site/` has no visual regression harness (see rebrand-jellyscribe tasks.md
2.3 and 1.3); these exist so a future favicon change has something to diff
against by eye.

- `favicon-16.png`, `favicon-32.png`, `favicon-96.png`: `site/public/favicon.svg`
  rasterized via `rsvg-convert -w <n> -h <n> public/favicon.svg -o ...`
  at the sizes browsers actually request (tab icon, taskbar, high-DPI).
