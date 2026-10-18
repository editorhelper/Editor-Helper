## What this changes

One or two sentences: the user-visible or architectural outcome.

## Why

The reason this change is needed — a bug it fixes, a limitation it removes.

## How it was verified

- [ ] `dotnet build EditorHelper.sln` — clean, no warnings
- [ ] `dotnet run --project tests/EditorHelper.Tests` — all pass
- [ ] `dotnet build "EditorHelper Server/EditorHelper.Server.sln"` — clean (if server touched)
- [ ] `dotnet run --project "Editor Helper Server/tests/EditorHelper.Server.Tests"` — all pass (if server touched)

Describe what you actually tested beyond the suites, if anything.

## Checklist

- [ ] No secrets, tokens or personal data in the diff
- [ ] Existing behavior preserved (no removed features)
- [ ] New behavior covered by tests
