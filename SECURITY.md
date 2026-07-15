# Security Policy

## Supported versions

Every merge to `main` ships a release, so only the **latest release** receives security fixes. If you are on an older version, update through the Jellyfin plugin catalog before reporting.

| Version | Supported |
| ------- | --------- |
| Latest release | ✅ |
| Older releases | ❌ |

## Reporting a vulnerability

Please **do not open a public issue** for security problems.

Report privately via [GitHub's private vulnerability reporting](https://github.com/builtbyproxy/jellyfin-plugin-letterboxd/security/advisories/new) (Security tab → "Report a vulnerability").

You can expect an acknowledgement within a few days. Because the release pipeline ships on every merge, confirmed fixes typically go out quickly.

## Scope

In scope:

- The plugin itself (`Jellyscribe.dll`), including anything that could expose Letterboxd credentials, raw cookies, or other users' data on a shared Jellyfin server
- The telemetry/manifest worker (`worker/`), including the anonymous telemetry pipeline, log-bundle uploads, and the manifest/download mirror
- The plugin's REST endpoints (e.g. privilege escalation between Jellyfin users, non-admins reaching admin-only data)

Out of scope:

- Vulnerabilities in Jellyfin itself (report to the [Jellyfin project](https://github.com/jellyfin/jellyfin/security))
- Vulnerabilities in Letterboxd's website or API (report to Letterboxd)
- The File Transformation plugin (report to [its repository](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation))
