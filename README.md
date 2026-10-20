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
  bot; accounts are bound to a single device.
- **Self-update from GitHub Releases** — checks for new versions, shows
  release notes, downloads the installer, verifies its SHA-256, then runs it.
  Stable and Beta channels.
- **Zero secrets in the client** — bot tokens and admin credentials live on
  the server only; the client stores just a DPAPI-protected session token.

> This repository contains the **client application**. The catalog server
> (ASP.NET Core API with Telegram integration) runs separately and is not
> part of this repo; the client works with any server implementing the API
> contract below.

## Requirements

**Client:** Windows 10 or 11. The installer is self-contained — no .NET
runtime needs to be preinstalled.

**Building from source:** .NET SDK 8.0+, and
[Inno Setup 6](https://jrsoftware.org/isinfo.php) for the installer.

## Getting started

```powershell
git clone https://github.com/editorhelper/Editor-Helper.git
cd Editor-Helper/"Editor Helper Client"
dotnet run
```

The app starts with the bundled demo catalog. To connect it to a server,
run your server instance, then in the app: Settings → Remote Server →
server API address.

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
```

Catalog loading, search, filters, error handling, downloads (retries,
cancellation), checksums, installation rules, formatting, and the update
flow against a fake releases API — no network access required.

## How updates work

Update metadata is served by **your server**, not queried from GitHub
directly by the client:

1. **Check** — the app calls `GET /api/v1/updates/check?channel=stable|beta`
   on the catalog server with its session. The server fetches release lists
   from GitHub (public repo for Stable, a private repo for Beta) and decides
   what the user may see.
2. **Channel gate** — Beta metadata is only present in the response when the
   account has the `is_beta_tester` flag, granted by the server Owner. A user
   without the grant never receives beta versions, notes, or links — no
   local setting can change that, the decision is server-side.
3. **Compare** — numeric semver comparison including prerelease suffixes;
   equal or older versions are never offered. A newer beta does not count as
   an update for a stable-channel user.
4. **Download** — the installer streams from the server
   (`GET /api/v1/updates/download?channel=…&tag=…`), which proxies the
   artifact from the release repository with server-side authorization, so
   the GitHub token never reaches the client. Progress is byte-accurate.
5. **Verify** — the release must include a `<installer>.exe.sha256` asset
   produced by the same CI run; the downloaded file is hashed and compared.
   A release without a checksum artifact is refused, and a mismatched hash
   deletes the file and aborts — the app keeps running the current version.
6. **Install** — the verified installer is launched; Inno Setup closes the
   running app, updates it, and offers to restart it. If anything fails
   before that, the app stays on the current version untouched.

Beta channel: opt-in in Settings → Updates for users with server-granted
access. Revoking access immediately stops new beta updates for that user;
the app then offers a return to the latest stable release.

## Server API expected by the client

```
GET /api/v1/catalog                       -> { resources: [...] }
GET /api/v1/resources/{id}                -> one resource
GET /api/v1/resources/{id}/download       -> file stream
GET /api/v1/health                        -> { ok, telegram }
POST /api/v1/auth/telegram/start          -> login challenge
GET  /api/v1/auth/telegram/status/{id}    -> challenge status
POST /api/v1/auth/telegram/complete       -> session token
GET /api/v1/updates/check?channel=…       -> stable/beta release metadata
GET /api/v1/updates/download?channel=…&tag=… -> installer stream
```

## Security

- All privileged operations check the role on the **server**, on every
  request — the hidden UI is convenience, not protection.
- Session tokens are DPAPI-protected on the client and hashed server-side.
  Device identity is a local random key; only its hash leaves the machine.
- The client never executes commands or scripts from release notes or
  remote metadata.
- Update integrity: SHA-256 checksum from the release, verified before
  install; a failed check aborts and keeps the current version.
- Beta artifacts live in a **private** repository; the server proxies them
  per-request with per-user authorization, so the read token never enters
  the client.

See [SECURITY.md](SECURITY.md) for reporting vulnerabilities.

## License

[MIT](LICENSE) — for the project code. Third-party dependencies
(Microsoft .NET packages) are under their own MIT-compatible licenses.
