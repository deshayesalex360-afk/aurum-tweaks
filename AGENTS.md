# AGENTS.md

Entry point for AI coding agents (Codex, etc.) working in this repository.
**`CLAUDE.md` is the authoritative project guide — read it first.** This file mirrors only the
load-bearing rules and the current state so a fresh agent can start without re-discovering them.

## What this is

Aurum Tweaks — a Windows-only **WPF / .NET 8** PC-optimization app (registry / service / BIOS / GPU
tweaks, hardware monitoring). UI text is **French**; code, comments, and tests are **English**.
**x64-only**, runs **elevated** (`app.manifest` → `requireAdministrator`).

## Build / test / run (PowerShell)

```powershell
dotnet build                                                            # whole solution (Debug)
dotnet run --project src/AurumTweaks                                    # run the app (UAC prompt)
dotnet build src/AurumTweaks/AurumTweaks.csproj -c Release              # "shippable" gate: 0 warn / 0 err
dotnet test tests/AurumTweaks.Tests/AurumTweaks.Tests.csproj           # full xUnit suite (French output)
dotnet test tests/AurumTweaks.Tests/AurumTweaks.Tests.csproj --filter "FullyQualifiedName~TweakServiceTests"
```

There is no separate linter — a clean Release build (0 warnings) is the lint gate.

## Non-negotiable rules (see CLAUDE.md for the full text)

- **Honesty mandate (load-bearing):** never claim something the app doesn't do — no dead buttons, no
  fabricated metrics, no fake "verified/safe" indicators, no kernel drivers / NVRAM / vBIOS writes.
  Disclose limits in code and UI. **Pinning a known-wrong value in a test is itself a violation.**
- **Apply and revert must be genuine inverses** — reversibility is the product's core promise; tests pin it.
- **Pure-core pattern:** pull honesty/correctness logic into `public static` classes (in the same file as
  their I/O service) and unit-test the *decision*, not the registry/process/WMI. No mocking framework —
  hand-written fakes in `tests/AurumTweaks.Tests/Fakes.cs` share an `EventLog` for ordering assertions.
- **Verification rhythm before declaring done:** filtered test run → full `dotnet test` → Release build,
  expecting **0 warnings / 0 errors**.
- **Language split:** user-facing strings French (`.resx` fr/en under `Localization/`); identifiers,
  comments, tests English. The same measurement must never be worded two ways across surfaces.

## Current state (handoff)

- Build green: **0 warn / 0 err**; **2831** xUnit tests passing; app version **0.1.0**, launched on
  GitHub Releases (public repo, Discussions on).
- Latest landed feature — **build-version provenance** across shareable surfaces: the system report &
  evidence proof stamp the running version; a snapshot freezes the capturing build
  (`SystemSnapshot.AppVersion`, preserved through JSON export/import) and renders « Version à la capture »;
  the comparison report warns when its two sides come from different builds (`SnapshotVersionProvenance`,
  only when both versions are known and differ); the snapshot list shows a compact « · v0.1.0 » badge.
  One frozen source per snapshot, reused verbatim in-app and in exports so they can't drift.
- `main` is committed and clean. `origin` (the public repo) may be behind — run `git push origin main`
  to sync it when ready (the published v0.1.0 Release is a separate tag and is unaffected).
