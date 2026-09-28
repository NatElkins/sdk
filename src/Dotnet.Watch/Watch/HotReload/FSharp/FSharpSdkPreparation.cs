// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Graph;
using Microsoft.Extensions.Logging;

namespace Microsoft.DotNet.Watch;

/// <summary>Owns SDK compiler eligibility before builds and invalidates the complete host after a compiler change.</summary>
internal sealed class FSharpSdkPreparation : IDisposable
{
    internal const string GrantsVariable = "FSHARP_WATCH_CAPABILITIES_FILE";
    internal const string SupportedVariable = "FSHARP_WATCH_COMPILER_SUPPORTED";
    private readonly DotNetWatchContext _context;
    private readonly string _grantsPath;
    private readonly string[] _selectionInputs;
    private readonly string _selectionHash;
    private readonly string _sdkHash;
    private ImmutableArray<FSharpCompilerIdentity> _identities = [];
    private string? _assignments;

    public bool HasFSharpProjects { get; private set; }
    public bool IsSupported { get; private set; }
    /// <summary>Includes absent ancestor files so their creation can change SDK selection.</summary>
    internal IReadOnlyList<string> SelectionInputs => _selectionInputs;

    /// <summary>Captures SDK selection and creates an empty capability grant before project evaluation.</summary>
    public FSharpSdkPreparation(DotNetWatchContext context)
    {
        _context = context;
        _grantsPath = Path.Combine(Path.GetTempPath(), $"fsharp-watch-{Guid.NewGuid():N}.props");
        // Set the import path before MSBuild creates its environment snapshot. The initial file grants nothing.
        new XDocument(new XElement("Project")).Save(_grantsPath);
        Environment.SetEnvironmentVariable(GrantsVariable, _grantsPath);
        Environment.SetEnvironmentVariable(SupportedVariable, "false");
        var inputs = new List<string>();
        for (var directory = new DirectoryInfo(context.EnvironmentOptions.WorkingDirectory); directory != null; directory = directory.Parent)
        {
            inputs.Add(Path.Combine(directory.FullName, "global.json"));
        }

        _selectionInputs = [.. inputs];
        _selectionHash = FSharpCompilerIdentity.HashFiles(_selectionInputs);
        _sdkHash = ReadSdkIdentity(context.EnvironmentOptions.SdkDirectory!);
    }

    /// <summary>Tracks SDK build dependencies that remain loaded for the lifetime of this watcher.</summary>
    internal static string ReadSdkIdentity(string sdkDirectory)
    {
        // Task isolation allows SDK NuGet versions to differ from the tool. A replacement requires a fresh task context.
        var dependencies = Directory.EnumerateFiles(sdkDirectory).Where(path =>
            Path.GetFileName(path).StartsWith("NuGet.", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(path).StartsWith("Microsoft.Build.", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(path).StartsWith("MSBuild.", StringComparison.OrdinalIgnoreCase));
        return FSharpCompilerIdentity.HashFiles(dependencies);
    }

    /// <summary>Requests a fresh watcher before changed SDK or compiler inputs can affect a baseline.</summary>
    public void VerifyUnchanged()
    {
        if (_selectionHash != FSharpCompilerIdentity.HashFiles(_selectionInputs) ||
            _sdkHash != ReadSdkIdentity(_context.EnvironmentOptions.SdkDirectory!))
        {
            throw new FSharpCompilerChangedException();
        }

        foreach (var identity in _identities)
        {
            identity.VerifyUnchanged();
        }
    }

    /// <summary>Probes evaluated SDK compilers and grants experimental flags only to compatible projects.</summary>
    public void Prepare(ProjectGraph graph)
    {
        VerifyUnchanged();
        var projects = graph.ProjectNodes.Select(node => node.ProjectInstance)
            .Where(project => Path.GetExtension(project.FullPath).Equals(".fsproj", StringComparison.OrdinalIgnoreCase) && project.GetTargetFramework() != "")
            .OrderBy(project => project.FullPath, StringComparer.Ordinal).ThenBy(project => project.GetTargetFramework(), StringComparer.Ordinal).ToArray();
        HasFSharpProjects = projects.Length != 0;
        var assignments = string.Join("\n", projects.Select(project => $"{project.FullPath}|{project.GetTargetFramework()}|{project.GetPropertyValue("DotnetFscCompilerPath")}|{project.GetPropertyValue("Optimize")}|{project.GetPropertyValue("SupportsHotReload")}"));
        if (_assignments != null)
        {
            if (_assignments != assignments)
            {
                throw new FSharpCompilerChangedException();
            }

            return;
        }

        _assignments = assignments;
        if (!HasFSharpProjects)
        {
            return;
        }

        var compilerPaths = projects.Select(project => project.GetPropertyValue("DotnetFscCompilerPath").Trim().Trim('"'))
            .Distinct(PathUtilities.OSSpecificPathComparer).ToArray();
        var sdkCompiler = Path.Combine(_context.EnvironmentOptions.SdkDirectory!, "FSharp", "fsc.dll");
        string? reason = null;
        if (_context.Options.NoHotReload || FSharpHotReloadService.IsDisabled() ||
            Environment.GetEnvironmentVariable("FSHARP_WATCH_ENABLE_HOT_RELOAD") == "false")
        {
            reason = "F# hot reload is disabled by the watcher configuration.";
        }
        else if (projects.Any(project => project.GetPropertyValue("SupportsHotReload").Equals("false", StringComparison.OrdinalIgnoreCase) ||
                                    project.GetPropertyValue("Optimize").Equals("true", StringComparison.OrdinalIgnoreCase)))
        {
            reason = "Hot reload is disabled or optimization is enabled for an F# project.";
        }
        else if (compilerPaths.Length != 1 || string.IsNullOrWhiteSpace(compilerPaths[0]) || !PathUtilities.OSSpecificPathComparer.Equals(Path.GetFullPath(compilerPaths[0]), Path.GetFullPath(sdkCompiler)))
        {
            reason = "The project graph does not use one compiler from the selected SDK.";
        }

        var probes = compilerPaths.Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => reason == null
                ? FSharpHotReloadService.ProbeCompiler(path, _context.Logger)
                : new FSharpCompilerProbe(false, reason, path, File.Exists(path) ? FSharpCompilerIdentity.Read(path) : null)).ToArray();
        _identities = [.. probes.Select(probe => probe.Identity).OfType<FSharpCompilerIdentity>()];
        reason ??= probes.FirstOrDefault(probe => !probe.Supported)?.Reason;
        IsSupported = reason == null && probes.Length == 1 && probes[0].Supported;
        Environment.SetEnvironmentVariable(SupportedVariable, IsSupported ? "true" : "false");
        if (!IsSupported)
        {
            _context.Logger.LogInformation("F# hot reload unavailable: {Reason} Edits use rebuild and restart.", reason ?? "The SDK compiler is unavailable.");
            return;
        }

        var document = new XElement("Project");
        foreach (var project in projects)
        {
            var condition = $"'$(MSBuildProjectFullPath)' == '{ProjectCollection.Escape(project.FullPath)}' and " +
                            $"'$(TargetFramework)' == '{ProjectCollection.Escape(project.GetTargetFramework())}' and " +
                            $"'$(DotnetFscCompilerPath)' == '{ProjectCollection.Escape(project.GetPropertyValue("DotnetFscCompilerPath"))}'";
            document.Add(new XElement("PropertyGroup", new XAttribute("Condition", condition), new XElement("FSharpWatchHotReloadSupported", "true")));
        }

        new XDocument(document).Save(_grantsPath);
        _context.Logger.LogInformation("F# hot reload uses SDK compiler '{CompilerPath}'.", probes[0].CompilerPath);
        VerifyUnchanged();
    }

    /// <summary>Verifies fallback compilation outside live output directories before the watcher stops an application.</summary>
    internal async Task<bool> PreflightAsync(IEnumerable<string> inputPaths, Func<bool> hasFurtherChanges, CancellationToken cancellationToken)
    {
        VerifyUnchanged();
        var inputs = inputPaths.ToArray();
        var before = FSharpCompilerIdentity.HashFiles(inputs);
        var artifacts = Directory.CreateTempSubdirectory("fsharp-watch-preflight-").FullName;
        try
        {
            foreach (var project in _context.RootProjects)
            {
                var arguments = new List<string> { "build", project.ProjectOrEntryPointFilePath };
                arguments.AddRange(_context.BuildArguments);
                if (_context.MainProjectOptions?.TargetFramework is { } framework)
                {
                    arguments.AddRange(["--framework", framework]);
                }

                arguments.AddRange(["--artifacts-path", artifacts, "-nologo", "-consoleLoggerParameters:NoSummary;Verbosity=minimal"]);
                var process = new ProcessSpec
                {
                    Executable = _context.EnvironmentOptions.GetMuxerPath(),
                    WorkingDirectory = _context.EnvironmentOptions.WorkingDirectory,
                    Arguments = arguments,
                    OnOutput = line => _context.BuildLogger.LogInformation("{BuildOutput}", line.Content),
                };
                if (await _context.ProcessRunner.RunAsync(process, _context.Logger, launchResult: null, cancellationToken) != 0)
                {
                    _context.Logger.LogInformation("F# rebuild failed. The current application remains running.");
                    return false;
                }
            }

            VerifyUnchanged();
            if (before != FSharpCompilerIdentity.HashFiles(inputs) || hasFurtherChanges())
            {
                _context.Logger.LogInformation("Inputs changed during the F# rebuild check. The current application remains running.");
                return false;
            }

            return true;
        }
        finally
        {
            try { Directory.Delete(artifacts, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Removes the private capability grant and its process environment values.</summary>
    public void Dispose()
    {
        Environment.SetEnvironmentVariable(GrantsVariable, null);
        Environment.SetEnvironmentVariable(SupportedVariable, null);
        File.Delete(_grantsPath);
    }
}
