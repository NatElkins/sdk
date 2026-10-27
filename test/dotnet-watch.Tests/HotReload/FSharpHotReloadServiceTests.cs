// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.DotNet.HotReload;

namespace Microsoft.DotNet.Watch.UnitTests;

[TestClass]
public class FSharpHotReloadServiceTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BrowserDiagnosticsIncludeFSharpIssuesAndClearAfterRecovery(bool compilationBlocked)
    {
        await using var browser = new DiagnosticBrowserRefreshServer();
        var clients = new HotReloadClients([], browser, useRefreshServerToApplyStaticAssets: false);
        // Diagnostic delivery only needs the clients. No application process is launched.
        var runningProject = new RunningProject(null!, null!, clients, NullLogger.Instance, null!, null!, []);
        var builder = new ProjectUpdatesBuilder
        {
            Logger = NullLogger.Instance,
            HotReloadService = null!,
            FSharpHotReloadService = null!,
            ManagedCodeSnapshot = default,
            RunningProjects = ImmutableDictionary<string, ImmutableArray<RunningProject>>.Empty.Add("App.fsproj", [runningProject]),
        };
        var message = compilationBlocked ? "FS0001: F# compile error" : "FSHRDL001: Restart required";
        var result = new FSharpManagedUpdateResult(
            compilationBlocked ? FSharpManagedUpdateStatus.Blocked : FSharpManagedUpdateStatus.RestartRequired,
            [],
            [new("App.fsproj", message), new("App.fsproj", null), new("App.fsproj", "")]);
        var display = typeof(ProjectUpdatesBuilder).GetMethod("DisplayResultsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        // Roslyn grants access to the watch assembly, but not this test assembly.
        var updatesType = display.GetParameters()[0].ParameterType;
        var runningInfos = EmptyCollection(display.GetParameters()[2].ParameterType);

        await (ValueTask)display.Invoke(builder, [CreateRoslynUpdates(hasError: true), result, runningInfos, CancellationToken.None])!;

        using (var payload = JsonDocument.Parse(browser.Messages.Single()))
        {
            Assert.AreEqual("ReportDiagnostics", payload.RootElement.GetProperty("type").GetString());
            var expected = compilationBlocked ? message : $"{message} Learn more: {FSharpHotReloadService.RudeEditHelpLink}";
            Assert.AreSequenceEqual(
                ["error CS1002: C# compile error", $"[App] {expected}"],
                payload.RootElement.GetProperty("diagnostics").EnumerateArray().Select(value => value.GetString()));
        }

        await (ValueTask)display.Invoke(builder, [CreateRoslynUpdates(hasError: false), new FSharpManagedUpdateResult(FSharpManagedUpdateStatus.NoChanges, [], []), runningInfos, CancellationToken.None])!;

        using var cleared = JsonDocument.Parse(browser.Messages.Last());
        Assert.HasCount(2, browser.Messages);
        Assert.AreEqual(0, cleared.RootElement.GetProperty("diagnostics").GetArrayLength());

        object CreateRoslynUpdates(bool hasError)
        {
            var updates = Activator.CreateInstance(updatesType)!;
            foreach (var property in updatesType.GetProperties())
            {
                property.SetValue(updates, property.Name == "Status"
                    ? Enum.Parse(property.PropertyType, hasError ? "Blocked" : "NoChangesToApply")
                    : EmptyCollection(property.PropertyType));
            }

            if (hasError)
            {
                updatesType.GetProperty("PersistentDiagnostics")!.SetValue(updates, ImmutableArray.Create(
                    Diagnostic.Create(new DiagnosticDescriptor("CS1002", "Syntax error", "C# compile error", "Compiler", DiagnosticSeverity.Error, true), Location.None)));
            }

            return updates;
        }

        static object EmptyCollection(Type type)
            => (type.GetField("Empty")?.GetValue(null) ?? type.GetProperty("Empty")?.GetValue(null))!;
    }

    /// <summary>Records the existing browser transport payload without a network listener.</summary>
    private sealed class DiagnosticBrowserRefreshServer() : BrowserRefreshServer(
        NullLogger.Instance, _ => NullLogger.Instance, _ => NullLogger.Instance, () => null,
        middlewareAssemblyPath: "", useGatewayProxy: false, suppressTimeouts: true)
    {
        public List<string> Messages { get; } = [];

        internal override ValueTask<TResult?> SendAndReceiveAsync<TRequest, TResult>(
            IReadOnlyCollection<BrowserConnection> openConnections,
            Func<string?, TRequest>? request,
            ResponseFunc<TResult>? response,
            CancellationToken cancellationToken)
            where TResult : struct
        {
            var value = request!(null);
            var bytes = value is ReadOnlyMemory<byte> message ? message : SerializeJson(value);
            Messages.Add(System.Text.Encoding.UTF8.GetString(bytes.Span));
            return ValueTask.FromResult<TResult?>(null);
        }
    }

    [TestMethod]
    public void RudeEditHelpLink_PointsToFSharpDiagnosticReference()
    {
        var field = typeof(FSharpHotReloadService).GetField(
            "RudeEditHelpLink",
            BindingFlags.Static | BindingFlags.NonPublic)!;

        Assert.AreEqual(
            "https://github.com/NatElkins/fsharp/blob/413be4377c3dd88abe08c2bbb6e9f4b285a06a1e/docs/hot-reload-rude-edits.md",
            field.GetRawConstantValue());
    }

    [TestMethod]
    [DataRow("/tmp/Program.fs", false)]
    [DataRow("/tmp/Program.fsi", false)]
    [DataRow("/tmp/Project.fsproj", false)]
    [DataRow("/tmp/.Program.fs.swp", false)]
    [DataRow("/tmp/payload.txt", true)]
    [DataRow("/tmp/view.xaml", true)]
    public void IsManagedDependencyCandidatePath_ClassifiesProjectAndTempFiles(string path, bool expected)
    {
        var method = typeof(FSharpHotReloadService).GetMethod(
            "IsManagedDependencyCandidatePath",
            BindingFlags.Static | BindingFlags.NonPublic)!;

        var raw = method.Invoke(null, [path]);
        Assert.IsInstanceOfType(raw, typeof(bool));
        Assert.AreEqual(expected, (bool)raw!);
    }

    [TestMethod]
    public void GetChangedFSharpProjects_MatchesDependencyByProjectDirectoryFallback()
    {
        var projectDirectory = Path.Combine(Path.GetTempPath(), "dotnet-watch-fsharp-tests");
        var projectPath = Path.Combine(projectDirectory, "TestApp.fsproj");
        var targetPath = Path.Combine(projectDirectory, "bin", "Debug", "net10.0", "TestApp.dll");
        var compilerPath = Path.Combine(projectDirectory, "fsc.dll");

        var projectId = new ProjectInstanceId(projectPath, "net10.0");
        var projectInfo = new FSharpProjectInfo(projectId, projectPath, "net10.0", targetPath, compilerPath, []);

        var service = new FSharpHotReloadService(NullLogger.Instance);
        var projectsField = typeof(FSharpHotReloadService).GetField("_projects", BindingFlags.Instance | BindingFlags.NonPublic)!;
        projectsField.SetValue(service, ImmutableDictionary<ProjectInstanceId, FSharpProjectInfo>.Empty.Add(projectId, projectInfo));

        var changedFiles = new List<ChangedFile>
        {
            new(
                new FileItem
                {
                    FilePath = Path.Combine(projectDirectory, "payload.txt"),
                    ContainingProjectPaths = []
                },
                ChangeKind.Update)
        };

        var runningProjects = ImmutableDictionary<string, ImmutableArray<RunningProject>>.Empty.Add(projectPath, []);

        var method = typeof(FSharpHotReloadService).GetMethod(
            "GetChangedFSharpProjects",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        var result = (ImmutableArray<ProjectInstanceId>)method.Invoke(service, [changedFiles, runningProjects])!;

        Assert.HasCount(1, result);
        Assert.AreEqual(projectId, result[0]);
    }

    [TestMethod]
    public void GetChangedFSharpProjects_MatchesCommandLineDependencyOutsideProjectDirectory()
    {
        var projectDirectory = Path.Combine(Path.GetTempPath(), "dotnet-watch-fsharp-tests-cmdline");
        var projectPath = Path.Combine(projectDirectory, "TestApp.fsproj");
        var targetPath = Path.Combine(projectDirectory, "bin", "Debug", "net10.0", "TestApp.dll");
        var compilerPath = Path.Combine(projectDirectory, "fsc.dll");

        var externalDependency = Path.Combine(Path.GetTempPath(), "dotnet-watch-fsharp-external", "MainPage.xaml");

        var projectId = new ProjectInstanceId(projectPath, "net10.0");
        var projectInfo =
            new FSharpProjectInfo(
                projectId,
                projectPath,
                "net10.0",
                targetPath,
                compilerPath,
                [$"--resource:{externalDependency},MainPage.xaml"]);

        var service = new FSharpHotReloadService(NullLogger.Instance);
        var projectsField = typeof(FSharpHotReloadService).GetField("_projects", BindingFlags.Instance | BindingFlags.NonPublic)!;
        projectsField.SetValue(service, ImmutableDictionary<ProjectInstanceId, FSharpProjectInfo>.Empty.Add(projectId, projectInfo));

        var changedFiles = new List<ChangedFile>
        {
            new(
                new FileItem
                {
                    FilePath = externalDependency,
                    ContainingProjectPaths = []
                },
                ChangeKind.Update)
        };

        var runningProjects = ImmutableDictionary<string, ImmutableArray<RunningProject>>.Empty.Add(projectPath, []);

        var method = typeof(FSharpHotReloadService).GetMethod(
            "GetChangedFSharpProjects",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        var result = (ImmutableArray<ProjectInstanceId>)method.Invoke(service, [changedFiles, runningProjects])!;

        Assert.HasCount(1, result);
        Assert.AreEqual(projectId, result[0]);
    }

    [TestMethod]
    public void GetChangedFSharpProjects_IgnoresEditorTempFiles()
    {
        var projectDirectory = Path.Combine(Path.GetTempPath(), "dotnet-watch-fsharp-tests-temp");
        var projectPath = Path.Combine(projectDirectory, "TestApp.fsproj");
        var targetPath = Path.Combine(projectDirectory, "bin", "Debug", "net10.0", "TestApp.dll");
        var compilerPath = Path.Combine(projectDirectory, "fsc.dll");

        var projectId = new ProjectInstanceId(projectPath, "net10.0");
        var projectInfo = new FSharpProjectInfo(projectId, projectPath, "net10.0", targetPath, compilerPath, []);

        var service = new FSharpHotReloadService(NullLogger.Instance);
        var projectsField = typeof(FSharpHotReloadService).GetField("_projects", BindingFlags.Instance | BindingFlags.NonPublic)!;
        projectsField.SetValue(service, ImmutableDictionary<ProjectInstanceId, FSharpProjectInfo>.Empty.Add(projectId, projectInfo));

        var changedFiles = new List<ChangedFile>
        {
            new(
                new FileItem
                {
                    FilePath = Path.Combine(projectDirectory, ".Program.fs.swp"),
                    ContainingProjectPaths = []
                },
                ChangeKind.Update)
        };

        var runningProjects = ImmutableDictionary<string, ImmutableArray<RunningProject>>.Empty.Add(projectPath, []);

        var method = typeof(FSharpHotReloadService).GetMethod(
            "GetChangedFSharpProjects",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        var result = (ImmutableArray<ProjectInstanceId>)method.Invoke(service, [changedFiles, runningProjects])!;

        Assert.IsTrue(result.IsEmpty);
    }

    [TestMethod]
    public void GetChangedFSharpProjects_ReturnsEveryAffectedProjectInBatch()
    {
        var root = Path.Combine(Path.GetTempPath(), "dotnet-watch-fsharp-tests-batch");
        var appPath = Path.Combine(root, "App", "App.fsproj");
        var libPath = Path.Combine(root, "Lib", "Lib.fsproj");
        var appId = new ProjectInstanceId(appPath, "net10.0");
        var libId = new ProjectInstanceId(libPath, "net10.0");

        var projects = ImmutableDictionary<ProjectInstanceId, FSharpProjectInfo>.Empty
            .Add(appId, new FSharpProjectInfo(appId, appPath, "net10.0", Path.ChangeExtension(appPath, ".dll"), "fsc.dll", []))
            .Add(libId, new FSharpProjectInfo(libId, libPath, "net10.0", Path.ChangeExtension(libPath, ".dll"), "fsc.dll", []));

        var service = new FSharpHotReloadService(NullLogger.Instance);
        typeof(FSharpHotReloadService).GetField("_projects", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, projects);

        var changedFiles = new List<ChangedFile>
        {
            new(new FileItem { FilePath = Path.Combine(root, "App", "Program.fs"), ContainingProjectPaths = [appPath] }, ChangeKind.Update),
            new(new FileItem { FilePath = Path.Combine(root, "Lib", "Lib.fs"), ContainingProjectPaths = [libPath] }, ChangeKind.Update),
        };
        var runningProjects = ImmutableDictionary<string, ImmutableArray<RunningProject>>.Empty
            .Add(appPath, [])
            .Add(libPath, []);

        var method = typeof(FSharpHotReloadService).GetMethod(
            "GetChangedFSharpProjects",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var result = (ImmutableArray<ProjectInstanceId>)method.Invoke(service, [changedFiles, runningProjects])!;

        Assert.AreSequenceEqual(new[] { appId, libId }, result);
    }

    [TestMethod]
    public async Task TryEmitUpdatesAsync_BridgeUnavailableReportsReferencedProjectForRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "dotnet-watch-fsharp-tests-missing-bridge");
        var appPath = Path.Combine(root, "App", "App.fsproj");
        var libPath = Path.Combine(root, "Lib", "Lib.fsproj");
        var libId = new ProjectInstanceId(libPath, "net10.0");
        var libInfo = new FSharpProjectInfo(
            libId,
            libPath,
            "net10.0",
            Path.Combine(root, "Lib", "bin", "Debug", "net10.0", "Lib.dll"),
            Path.Combine(root, "missing", "FSharp.Compiler.Service.dll"),
            []);

        var service = new FSharpHotReloadService(NullLogger.Instance);
        typeof(FSharpHotReloadService).GetField("_projects", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, ImmutableDictionary<ProjectInstanceId, FSharpProjectInfo>.Empty.Add(libId, libInfo));

        var changedFile = new ChangedFile(
            new FileItem
            {
                FilePath = Path.Combine(root, "Lib", "Lib.fs"),
                ContainingProjectPaths = [libPath],
            },
            ChangeKind.Update);
        var runningProjects = ImmutableDictionary<string, ImmutableArray<RunningProject>>.Empty.Add(appPath, []);

        var result = await service.TryEmitUpdatesAsync([changedFile], runningProjects, CancellationToken.None);

        Assert.AreEqual(FSharpManagedUpdateStatus.RestartRequired, result.Status);
        Assert.IsTrue(result.Updates.IsEmpty);
        Assert.HasCount(1, result.Issues);
        Assert.AreEqual(libPath, result.Issues[0].ProjectPath);
    }

    [TestMethod]
    [DataRow("Program.fs", true)]
    [DataRow("payload.txt", true)]
    [DataRow("Project.fsproj", false)]
    [DataRow("Directory.Build.props", false)]
    public void IsSupportedChangedFile_LeavesProjectConfigurationOnRestartPath(string fileName, bool expected)
    {
        var method = typeof(FSharpHotReloadService).GetMethod(
            "IsSupportedChangedFile",
            BindingFlags.Static | BindingFlags.NonPublic)!;

        Assert.AreEqual(expected, (bool)method.Invoke(null, [Path.Combine("/tmp", fileName)])!);
    }

    [TestMethod]
    [ResourceLock(WellKnownResources.EnvironmentVariables)]
    [DataRow("0")]
    [DataRow("false")]
    public async Task KillSwitch_DisablesEntireBridge(string killSwitchValue)
    {
        var originalValue = Environment.GetEnvironmentVariable("DOTNET_WATCH_FSHARP_HOTRELOAD");
        try
        {
            Environment.SetEnvironmentVariable("DOTNET_WATCH_FSHARP_HOTRELOAD", killSwitchValue);

            var projectDirectory = Path.Combine(Path.GetTempPath(), "dotnet-watch-fsharp-tests-killswitch");
            var projectPath = Path.Combine(projectDirectory, "TestApp.fsproj");
            var targetPath = Path.Combine(projectDirectory, "bin", "Debug", "net10.0", "TestApp.dll");
            var compilerPath = Path.Combine(projectDirectory, "fsc.dll");

            var projectId = new ProjectInstanceId(projectPath, "net10.0");
            var projectInfo = new FSharpProjectInfo(projectId, projectPath, "net10.0", targetPath, compilerPath, []);

            // The kill switch is read once at construction.
            var service = new FSharpHotReloadService(NullLogger.Instance);

            // Simulate a discovered F# project so that, were the bridge enabled, the change below
            // would match the running project and produce a result carrying the project path.
            var projectsField = typeof(FSharpHotReloadService).GetField("_projects", BindingFlags.Instance | BindingFlags.NonPublic)!;
            projectsField.SetValue(service, ImmutableDictionary<ProjectInstanceId, FSharpProjectInfo>.Empty.Add(projectId, projectInfo));

            var changedFile = new ChangedFile(
                new FileItem
                {
                    FilePath = Path.Combine(projectDirectory, "Program.fs"),
                    ContainingProjectPaths = [projectPath]
                },
                ChangeKind.Update);

            var runningProjects = ImmutableDictionary<string, ImmutableArray<RunningProject>>.Empty.Add(projectPath, []);

            await service.StartSessionAsync(CancellationToken.None);
            var result = await service.TryEmitUpdatesAsync([changedFile], runningProjects, CancellationToken.None);

            // Disabled: the service behaves as if no F# projects exist.
            Assert.AreEqual(FSharpManagedUpdateStatus.NoChanges, result.Status);
            Assert.IsTrue(result.Updates.IsEmpty);
            Assert.IsTrue(result.Issues.IsEmpty);
            Assert.IsFalse(service.OwnsChangedFile(changedFile));
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_WATCH_FSHARP_HOTRELOAD", originalValue);
        }
    }

    [TestMethod]
    public void TryGetCommandLineDependencyPath_ParsesResourceLogicalNameSuffix()
    {
        var method = typeof(FSharpHotReloadService).GetMethod(
            "TryGetCommandLineDependencyPath",
            BindingFlags.Static | BindingFlags.NonPublic)!;

        var projectDirectory = Path.Combine(Path.GetTempPath(), "dotnet-watch-fsharp-tests-parse");
        var expectedPath = Path.GetFullPath(Path.Combine(projectDirectory, "Views", "MainPage.xaml"));
        var arguments = new object?[] { "--resource:Views/MainPage.xaml,MainPage.xaml", projectDirectory, null };

        var raw = method.Invoke(null, arguments);
        Assert.IsInstanceOfType(raw, typeof(bool));
        Assert.IsTrue((bool)raw!);

        Assert.IsInstanceOfType(arguments[2], typeof(string));
        Assert.AreEqual(expectedPath, (string)arguments[2]!);
    }
}
