# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

Aurum Tweaks is a Windows-only WPF / .NET 8 PC-optimization app (registry/service/BIOS/GPU tweaks, hardware monitoring). UI text is French; code, comments, and tests are English.

## Commands

```powershell
# Build the whole solution (Debug)
dotnet build

# Run the app — WPF WinExe; app.manifest forces a UAC elevation prompt on launch
dotnet run --project src/AurumTweaks

# Release build of the app — the "is it shippable" gate; must be 0 warnings / 0 errors
dotnet build src/AurumTweaks/AurumTweaks.csproj -c Release

# Full test suite (xUnit). Output is French: "Réussi!  - échec : 0, réussite : N"
dotnet test tests/AurumTweaks.Tests/AurumTweaks.Tests.csproj

# A single test class
dotnet test tests/AurumTweaks.Tests/AurumTweaks.Tests.csproj --filter "FullyQualifiedName~TweakServiceTests"

# A single test method
dotnet test tests/AurumTweaks.Tests/AurumTweaks.Tests.csproj --filter "FullyQualifiedName~RegistryValueTests.Matches_Qword_ComparesNumerically"
```

There is no separate linter — a clean Release build (0 warnings) is the lint gate. The project is **x64-only** and runs **elevated** (`app.manifest` → `requireAdministrator`), which `dotnet run` honors via a UAC prompt. If this working copy isn't under git (it wasn't when this file was written), diff-based review tooling has no commit baseline — review the changed files directly.

## Architecture (the parts that span multiple files)

**Manual DI + static service locator.** There is no generic host. `App.OnStartup` (`src/AurumTweaks/App.xaml.cs`) builds a `ServiceCollection` in `ConfigureServices()` and assigns it to the static `App.Services`. Every service and every ViewModel is registered there as a **singleton**. Views resolve their ViewModel through `App.Services`; cross-page navigation goes through `MainViewModel.Navigate(pageKey)` (`NavigationService`). **Adding a service or ViewModel means: add its contract to `Services/Interfaces.cs` and register it in `ConfigureServices()`** — nothing is auto-discovered.

**Startup sequence.** Serilog (logs → `%LOCALAPPDATA%\AurumTweaks\Logs`) → `ConfigureServices` → `SplashWindow` while it detects hardware, loads the tweak catalog, and starts monitoring → first-launch `WelcomeWindow` (gated by `settings.HasSeenWelcome`) → `MainWindow`. Settings and profiles persist under `%LOCALAPPDATA%\AurumTweaks\`.

**The tweak engine is the core domain.** JSON files under `src/AurumTweaks/Tweaks/{tranquille,advanced,extreme}/` (the three tiers) are copied to the output dir and loaded by `TweakRepository` from `AppContext.BaseDirectory\Tweaks`. Each `Tweak` holds `Operations` of type Registry / Service / PowerShell / Cmd / AppX / ScheduledTask / Bcdedit / File. `TweakService.ExecuteAsync` dispatches by `OperationType` to apply **or** revert. `ApplyManyAsync` creates a Windows System Restore point *before* touching anything — gated by `Settings.CreateRestorePointBeforeTweaks` (honor the toggle exactly: off means none). **Apply and revert must be genuine inverses** — that reversibility is the product's core promise, and the tests pin it.

**Pure-core extraction pattern (follow this).** Honesty- and correctness-bearing logic is pulled out of the I/O services into `public static` classes so it is unit-testable without touching the registry, spawning processes, or querying WMI. Examples, each living in the same file as its service: `TweakShellCommand` (builds the exact `(fileName, args)` for shell ops) and `RegistryValue` (DWord/QWord parse + numeric compare) ; plus `ShellLauncher` (URL/console allow-list), `HardwareClassification`, `BiosApplyAdvisor`, `ProfilePresets`, `NetworkRouteMath`. When you add decision logic to an I/O service, extract the decision into a pure static and test that, rather than mocking the world.

**Hardware/monitoring** runs through `HardwareService` (WMI via `System.Management`) and `MonitoringService` (`LibreHardwareMonitorLib`); both need elevation for full sensor access. GPU overclocking is behind `IGpuOcService` (NVAPI/ADL abstraction, not yet a native backend).

(See `README.md` for the full directory tree, section-by-section feature map, and the design/credits context — not repeated here.)

## Tests

xUnit, in `tests/AurumTweaks.Tests`. **No mocking framework** — behavior is verified against hand-written fakes in `Fakes.cs` (`FakeRegistryService`, `FakeServiceManager`, `RecordingRestorePointService`, `FakeAppSettingsStore`) that share an `EventLog` so tests can assert *ordering* (e.g. "restore point is created before the first registry write"). Mirror that style: pure helpers get `[Theory]`/`[InlineData]` value tables; service behavior gets a fake-backed `TweakService`.

## Project conventions

- **Honesty mandate (load-bearing).** The app must never claim to do something it doesn't. No dead buttons (a control that appears to act but does nothing), no fabricated metrics, no fake "verified/safe" indicators, no kernel drivers or NVRAM/vBIOS writes. Be explicit about a feature's limits in code and UI. **Pinning a known-wrong value in a test is itself a violation** — verify the invariant actually holds before asserting it.
- **Verification rhythm before declaring done:** filtered test run → full `dotnet test` → Release build of the app, expecting **0 warnings / 0 errors**.
- **Language split:** user-facing strings are French (`NeutralLanguage` fr-FR; `.resx` for fr/en under `Localization/`). Identifiers, comments, and tests are English.
- **Comments encode WHY** (a non-obvious constraint or invariant), not WHAT the code plainly does.
