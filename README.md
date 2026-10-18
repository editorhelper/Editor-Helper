# Editor Helper

A catalog of tools for video editors and motion designers: scripts, plugins,
presets and assets for After Effects, Premiere Pro and Photoshop — in one
Windows desktop app with a living cosmic UI.

Editor Helper ships with a self-contained Windows installer, downloads
resources with checksum verification, installs them into Adobe folders, and
updates itself from GitHub Releases.

![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-blue)
![.NET](https://img.shields.io/badge/.NET-8.0-512BD4)
![License](https://img.shields.io/badge/license-MIT-green)

## Features

- **Resource catalog** — browse scripts, plugins and presets; search, filter
  by application and type, favorites (synced to your server account).
- **Detail pages** — every resource opens as a page inside the main window
  with descriptions, tags, and version metadata.
- **Downloads with integrity checks** — SHA-256 checksums verified before a
  file is accepted; failed or canceled downloads are cleaned up; network
  retries with backoff.
- **Safe installation** — copies files into Adobe folders (ScriptUI Panels,
  presets, plug-ins) with confirmation; the app never runs downloaded EXE/BAT
  or PowerShell files — installer-type resources get step-by-step manual
  instructions instead.
- **Accounts via Telegram** — one-time-code login confirmed by a Telegram
  bot; accounts are bound to a single device; three roles (User, VIP, Owner).
- **Admin panel** — full CRUD for catalog cards, publishing workflow, banner
  and archive uploads, Telegram channel storage — every action re-checks the
  Owner role server-side.
- **Self-update from GitHub Releases** — checks for new versions, shows
  release notes, downloads the installer, verifies its SHA-256, then runs it.
  Stable and Beta channels.
- **Zero secrets in the client** — bot tokens and admin credentials live on
  the server only; the client stores just a DPAPI-protected session token.

## Project layout

This repository contains three sibling projects:

| Folder | What it is |
|---|---|
| `Editor Helper Client/` | WPF (.NET 8) desktop application — the app users install |
| `Editor Helper Server/` | ASP.NET Core API: catalog, auth, admin, Telegram integration |
| `Editor Helper Telegram Bot/` | Bot handlers (linked source compiled into the server) |

The server is optional for trying the client: a local development catalog is
bundled. In production the client talks to one server instance.

## Requirements

**Client:** Windows 10 or 11. The installer is self-contained — no .NET
runtime needs to be preinstalled.

**Building from source:** .NET SDK 8.0+, and
[Inno Setup 6](https://jrsoftware.org/isinfo.php) for the installer.

## Getting started

```powershell
git clone https://github.com/<owner>/editorhelper.git
cd editorhelper/"Editor Helper Client"
dotnet run
```

The app starts with the bundled demo catalog. To connect it to your own
server, run the server (next section), then in the app: Settings → Remote
Server → `http://127.0.0.1:5080`.

## Running the server

```powershell
cd "Editor Helper Server"
dotnet run --project EditorHelper.Server
```

The API listens on `http://127.0.0.1:5080`, the web admin panel is at
`http://127.0.0.1:5080/admin`. On first start the server generates
`server-data/admin-token.txt` — use that token to sign in to the admin panel.

Optional integrations (see `Editor Helper Server/.env.example` for every
setting):

- **Telegram login & admin bot** — set `TELEGRAM_BOT_TOKEN` and
  `EDITORHELPER_OWNER_TELEGRAM_ID`. One bot handles login confirmation, user
  management and channel document discovery; the token stays on the server.
- **Telegram channel as file storage** — set `TELEGRAM_CHANNEL_ID`; archives
  posted in the channel are discovered by the bot and attachable to cards.

> Keep the server on loopback for development. Production deployment needs
> HTTPS, a public URL, and the development-login switches turned off.

## Building a release

```cmd
cd "Editor Helper Client"
Release\publish.cmd                                  :: self-contained payload
Release\installer.cmd                                 :: EditorHelper-Setup-x.y.z.exe
```

`publish.cmd` accepts the default server API URL as an argument. The pipeline
verifies that no demo data, dev settings or debug files leak into the payload.

## Testing

```powershell
cd "Editor Helper Client"
dotnet run --project tests/EditorHelper.Tests

cd "Editor Helper Server"
dotnet run --project tests/EditorHelper.Server.Tests
```

Client suite: catalog loading, search, filters, error handling, downloads
(retries, cancellation), checksums, installation rules, formatting, and the
GitHub update flow against a fake releases API. Server suite: SSRF guard,
auth challenges, roles, device binding, rate limiting, admin lifecycle,
Telegram integration against a fake Bot API — no network access required.

## How updates work

1. **Check** — the app queries the public GitHub Releases API
   (`releases/latest`, or `releases` for the Beta channel) over HTTPS. No
   token, no authentication; publishing happens through GitHub Actions.
2. **Compare** — semver comparison including prerelease suffixes; equal or
   older versions are never offered.
3. **Download** — the `EditorHelper-Setup-*.exe` asset streams to
   `%LOCALAPPDATA%\EditorHelper\updates` with a real byte-accurate progress
   bar.
4. **Verify** — the release must include a `<installer>.exe.sha256` asset
   produced by the same CI run; the downloaded file is hashed and compared.
   A release without a checksum artifact is refused, and a mismatched hash
   deletes the file and aborts — the app keeps running the current version.
5. **Install** — the verified installer is launched; Inno Setup closes the
   running app, updates it, and offers to restart it. If anything fails
   before that, the app stays on the current version untouched.

Beta channel: opt-in in Settings → Updates. Stable users only ever see final
releases; prereleases reach only those who turned the switch on.

## Security

- All privileged operations check the role on the **server**, on every
  request — the hidden UI is convenience, not protection.
- SSRF protection on external file URLs (private ranges, link-local, CGNAT,
  metadata IPs are rejected, including after redirects).
- Rate limiting per route tier: catalog, auth completion, auth polling,
  admin; request body size limits per endpoint class.
- Session tokens are SHA-256-hashed server-side and DPAPI-protected on the
  client. Device identity is a local random key; only its hash leaves the
  machine.
- The client never executes commands or scripts from release notes or
  remote metadata.

See [SECURITY.md](SECURITY.md) for reporting vulnerabilities.

## License

[MIT](LICENSE) — for the project code. Third-party dependencies
(Microsoft .NET packages, Microsoft.Data.Sqlite) are under their own
MIT-compatible licenses.
