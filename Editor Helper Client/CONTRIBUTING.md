# Contributing to Editor Helper

Thanks for your interest! A few ground rules keep this project healthy.

## Getting set up

```powershell
git clone https://github.com/editorhelper/Editor-Helper.git
cd Editor-Helper/"Editor Helper Client"
dotnet build EditorHelper.sln
dotnet run --project tests/EditorHelper.Tests
```

This repository contains the client application. The catalog server runs
separately; the client tests are fully self-contained and need no server.

## Before you open a pull request

1. **Build must be clean** — zero warnings, zero errors:
   `dotnet build EditorHelper.sln`.
2. **Tests must pass**: `dotnet run --project tests/EditorHelper.Tests`.
3. **Match the existing style.** The codebase uses Russian comments for
   non-obvious constraints and no comments for the obvious. WPF UI is built
   in code-behind with the shared design system (`Ui/Palette.cs`,
   `Resources/Styles.xaml`, `Ui/Motion.cs`) — don't introduce a second theme
   or a UI framework.
4. **No secrets in code, tests, or docs.** Bot tokens, admin tokens and
   personal IDs never belong in the repository. Server secrets go in
   environment variables or `server-data/secrets.json` (gitignored).
5. **Keep the architecture.** One Telegram bot, one polling loop, one
   catalog provider interface. Authorization is enforced server-side —
   client-side hiding is convenience only.
6. **Add tests for behavior you add or fix.** The suites are dependency-free
   custom runners; extend them rather than introducing a test framework.

## Commit style

- Short imperative summary line.
- Body explains *why*, not *what* — the diff shows what.
- No secrets, no personal data, no machine-specific paths.

## Reporting bugs

Use the issue templates. Include: what you did, what you expected, what
happened, app version (Settings → About), and whether you were on a local
catalog or a server connection.

## Licensing

By contributing you agree that your contributions are licensed under the
project's [MIT license](LICENSE).
