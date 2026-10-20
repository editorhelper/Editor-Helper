# Security Policy

## Supported versions

Only the latest release receives security fixes. The app updates itself from
GitHub Releases — keeping it current is the supported state.

## Reporting a vulnerability

**Do not open a public issue for security problems.**

Use [GitHub private vulnerability reporting](https://docs.github.com/en/code-security/security-advisories/guidance-on-reporting-and-handling-security-vulnerabilities)
(Report a vulnerability button on the Security tab), or email the maintainer
directly. Include:

- description of the issue and its impact,
- steps or proof-of-concept to reproduce it,
- affected component (client, server, or update pipeline),
- the version you tested against.

You will get an acknowledgement within a few days. Please avoid public
disclosure until a fix is released.

## Security model (what is and isn't protected)

**Client:** holds no secrets by design. The session token is DPAPI-protected
per-user; the device identity key never leaves the machine in clear. Anyone
can decompile the client (the source is open) — nothing in it grants
privileges.

**Server:** every privileged operation re-checks the role at execution time
from a server-verified session. Client-supplied Telegram IDs, usernames and
roles are never trusted. SSRF guard rejects private/link-local/CGNAT
addresses on external file URLs including after redirects. Rate limits apply
per route tier; request body sizes are capped per endpoint class. Admin and
Telegram secrets live only in server environment variables or the
gitignored `server-data/`.

**Updates:** the client talks to GitHub over HTTPS only, and accepts a
release artifact only if the same CI run published a `.sha256` asset for it;
a missing or mismatched checksum aborts the update. The client never
executes commands from release notes or metadata, and never passes a GitHub
token (reading public releases is anonymous; publishing is a server-side
GitHub Actions job).

**Known limitation:** installer binaries are not Authenticode-signed (no
code-signing certificate). Checksum verification pins the file to the GitHub
release that CI built, but Windows SmartScreen will still show its standard
"unknown publisher" prompt on first run. Signing will be added when a
certificate is available; the pipeline has a dedicated checksum step that
will complement a signature, not be replaced by it.
