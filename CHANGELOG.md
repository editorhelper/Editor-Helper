# Changelog

All notable changes to Editor Helper are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow
semantic versioning.

## [0.4.0] — 2026-10-20

First public release with the self-update system.

### Added
- Two update channels with server-side gating: Stable for everyone, Beta only
  for users granted the `is_beta_tester` flag by the server Owner. Beta
  metadata and files are never sent to unauthorized users; the grant/revoke
  is audited and takes effect immediately.
- Update check and installer download flow through the server
  (`/api/v1/updates/check`, `/api/v1/updates/download`): the server reads
  GitHub releases (public repo for Stable, private repo for Beta with a
  read-only token) and authorizes every request per user.
- Updates page in Settings: current and available version, release notes,
  byte-accurate download progress, checksum verification, install-and-restart.
- Self-update from GitHub Releases: version check over HTTPS, release notes,
  installer download with byte-accurate progress, SHA-256 verification
  against the `.sha256` release asset, installer launch. Stable and Beta
  channels (opt-in prereleases) in Settings → Updates.
- Release pipeline: GitHub Actions CI (client + server suites on push/PR) and
  a tag-triggered release workflow that builds the installer, computes its
  checksum and publishes both to GitHub Releases.
- Rate limiting on the server: sliding window per route tier (catalog, auth
  completion, auth polling, admin), per-session keys for admin routes,
  configurable via environment variables.
- Request body size limits on the server: 256 KB for JSON endpoints, 5 MB
  banners, 50 MB Telegram uploads, 100 MB file uploads, with the Kestrel
  ceiling aligned to the largest tier.
- Windows installer (Inno Setup): shortcuts, Add/Remove Programs entry,
  upgrade-over-install, uninstall preserving user data.
- User data relocated to `%LOCALAPPDATA%\EditorHelper` — the app now works
  from `Program Files` without admin rights; legacy portable state migrates
  automatically on first run.

### Changed
- Release builds default to the remote server catalog instead of the bundled
  demo data; the API URL is a build parameter (`Release\publish.cmd <url>`).
- Demo `Data/` files and debug artifacts (PDB) are excluded from release
  payloads and verified absent by the pipeline.
- Deterministic release builds (project paths stripped from IL).

## [0.3.0] — 2026-10-18

### Added
- In-app Admin Panel (Owner role, server-enforced): card CRUD, publishing
  workflow (draft/published/hidden), duplication, deletion with
  confirmation, banner and archive uploads, three file sources (server
  file, external HTTP URL, Telegram document), live card preview matching
  the catalog rendering.
- Resource detail pages open inside the main window instead of a separate
  dialog, with a back-to-catalog button.
- Manual installation instructions render as an in-page overlay.
- Server: catalog draft/publish lifecycle, publish-time file availability
  checks, additional links normalization, banner endpoints.

## [0.2.0] — 2026-10-12

### Added
- Telegram login for accounts: one-time codes confirmed by the single bot,
  channel membership check, account binding to one device (DPAPI key,
  hash-only on the server), unbind flow with rate limiting.
- Roles: User / VIP / Owner; Owner determined by server configuration only.
- Server-backed favorites; audit log for admin actions; session revocation.
- SSRF protection for external file sources.

## [0.1.0] — 2026-10-04

### Added
- Initial release: WPF client with the Premium Cosmic UI, local catalog,
  search and filters, downloads with checksums and retries, safe
  installation into Adobe folders, favorites, settings.
- ASP.NET Core catalog server with the web admin panel and Telegram
  channel file storage.
