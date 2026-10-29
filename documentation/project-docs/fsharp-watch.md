# Standalone F# watch maintenance

The standalone `fsharp-watch` package uses this fork's watch engine.
The selected SDK supplies MSBuild, the F# compiler, and FSharp.Compiler.Service.
The package repository pins a commit from `feature/fsharp-watch-global-tool`.

`ManagedCodeWorkspace` owns the Roslyn and F# sessions.
`ProjectUpdatesBuilder` combines their updates and restart requests.
`RunningProjectsManager` retains updates for future processes and delivers updates to current processes.
A compilation error from either compiler discards both pending updates.

The standalone engine targets .NET 10.
Its MSBuild compile references use `FSharpWatchMicrosoftBuildVersion` from `eng/ManualVersions.props`.
`Directory.Packages.props` restricts that version to the three host projects when `SdkTargetFramework` is `net10.0`.
Runtime MSBuild still comes from the selected SDK.
The default SDK build retains upstream dependencies and target framework.

The [upstream workflow](../../.github/workflows/fsharp-watch-sync.yml) opens a draft PR with current `dotnet/sdk` changes.
Conflicts fail the workflow and leave the maintained branch unchanged.
The workflow never merges its PR.
GitHub runs the schedule only after the workflow exists on the fork's default branch.

## Verify the engine

Run the focused build and test gate:

```bash
python3 scripts/fsharp-watch-verify.py
```

## Verification scope

The [verification script](../../scripts/fsharp-watch-verify.py) builds both the standalone host and the default watch target.
It installs the runtime versions that the test project requires.
It creates an isolated SDK fixture with fresh watch assemblies and runs the repository test runner.
The selected tests cover the F# bridge, compiler identity, reflection, and file events.
They do not establish installed application behavior or compiler support in released SDKs.
The package repository owns those acceptance tests.
