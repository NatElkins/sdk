// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections;
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Microsoft.Build.Graph;
using Microsoft.DotNet.HotReload;
using Microsoft.Extensions.Logging;

namespace Microsoft.DotNet.Watch.HotReload.FSharp;

internal enum FSharpManagedUpdateStatus
{
    NoChanges,
    ReadyToApply,
    RestartRequired,
    Blocked,
}

internal readonly record struct FSharpManagedUpdate(string ProjectPath, HotReloadManagedCodeUpdate Update);

internal readonly record struct FSharpManagedUpdateResult(
    FSharpManagedUpdateStatus Status,
    ImmutableArray<FSharpManagedUpdate> Updates,
    string? ProjectPath,
    string? Message);

internal sealed class FSharpHotReloadService
{
    private readonly ILogger _logger;
    private readonly bool _trace;

    private ImmutableDictionary<ProjectInstanceId, FSharpProjectInfo> _projects = ImmutableDictionary<ProjectInstanceId, FSharpProjectInfo>.Empty;
    private FSharpReflectionHost? _host;
    private ProjectInstanceId? _activeProject;
    private object? _activeProjectOptions;

    public FSharpHotReloadService(ILogger logger)
    {
        _logger = logger;
        _trace = IsTraceEnabled();
    }

    public void UpdateProjects(ProjectGraph projectGraph)
    {
        _projects = FSharpProjectInfo.Collect(projectGraph, _logger);

        if (_activeProject is { } activeProject && !_projects.ContainsKey(activeProject))
        {
            EndSession();
        }
    }

    public ValueTask StartSessionAsync(CancellationToken cancellationToken)
    {
        // Session bootstrap is deferred until the first F# managed update request.
        return ValueTask.CompletedTask;
    }

    public void EndSession()
    {
        _host?.TryEndSession();
        _activeProject = null;
        _activeProjectOptions = null;
    }

    public async ValueTask<FSharpManagedUpdateResult> TryEmitUpdatesAsync(
        IReadOnlyList<ChangedFile> changedFiles,
        ImmutableDictionary<string, ImmutableArray<RunningProject>> runningProjects,
        CancellationToken cancellationToken)
    {
        var changedProject = TryGetChangedRunningFSharpProject(changedFiles, runningProjects);
        if (changedProject == null)
        {
            return new FSharpManagedUpdateResult(FSharpManagedUpdateStatus.NoChanges, [], null, null);
        }

        if (!_projects.TryGetValue(changedProject.Value, out var projectInfo))
        {
            return new FSharpManagedUpdateResult(FSharpManagedUpdateStatus.NoChanges, [], null, null);
        }

        if (!TryGetHost(projectInfo, out var host, out var hostError))
        {
            _logger.LogDebug(
                "F# managed hot reload bridge unavailable for '{ProjectPath}': {Message}. Falling back to restart.",
                projectInfo.ProjectPath,
                hostError);

            return new FSharpManagedUpdateResult(
                FSharpManagedUpdateStatus.RestartRequired,
                [],
                projectInfo.ProjectPath,
                hostError);
        }

        if (!EnsureSession(host, projectInfo, out var ensureStatus, out var ensureMessage))
        {
            return new FSharpManagedUpdateResult(ensureStatus, [], projectInfo.ProjectPath, ensureMessage);
        }

        var moduleId = TryGetModuleVersionId(projectInfo.TargetPath);
        if (moduleId == null)
        {
            var message = $"Unable to read module id from '{projectInfo.TargetPath}'.";
            return new FSharpManagedUpdateResult(FSharpManagedUpdateStatus.RestartRequired, [], projectInfo.ProjectPath, message);
        }

        var emit = host.EmitDelta(_activeProjectOptions!, cancellationToken);
        if (!emit.IsSuccess)
        {
            var mappedStatus = MapErrorStatus(emit.ErrorCase);

            if (emit.ErrorCase == "NoActiveSession")
            {
                if (_trace)
                {
                    _logger.LogDebug("F# hot reload session went inactive for '{ProjectPath}', restarting session.", projectInfo.ProjectPath);
                }

                EndSession();
                if (EnsureSession(host, projectInfo, out var retryStatus, out var retryMessage))
                {
                    emit = host.EmitDelta(_activeProjectOptions!, cancellationToken);
                    if (emit.IsSuccess)
                    {
                        mappedStatus = FSharpManagedUpdateStatus.ReadyToApply;
                    }
                    else
                    {
                        mappedStatus = MapErrorStatus(emit.ErrorCase);
                    }
                }
                else
                {
                    return new FSharpManagedUpdateResult(retryStatus, [], projectInfo.ProjectPath, retryMessage);
                }
            }

            if (mappedStatus == FSharpManagedUpdateStatus.NoChanges)
            {
                return new FSharpManagedUpdateResult(FSharpManagedUpdateStatus.NoChanges, [], null, null);
            }

            if (mappedStatus == FSharpManagedUpdateStatus.Blocked)
            {
                _logger.Log(MessageDescriptor.UnableToApplyChanges);
                if (!string.IsNullOrEmpty(emit.ErrorText))
                {
                    _logger.LogWarning("F# compilation blocked hot reload: {Message}", emit.ErrorText);
                }
            }
            else
            {
                _logger.LogDebug(
                    "F# managed hot reload requires restart for '{ProjectPath}': {ErrorCase} ({ErrorText})",
                    projectInfo.ProjectPath,
                    emit.ErrorCase,
                    emit.ErrorText);
            }

            return new FSharpManagedUpdateResult(mappedStatus, [], projectInfo.ProjectPath, emit.ErrorText);
        }

        var update = host.CreateManagedUpdate(moduleId.Value, emit.Value!);
        if (update == null)
        {
            return new FSharpManagedUpdateResult(
                FSharpManagedUpdateStatus.RestartRequired,
                [],
                projectInfo.ProjectPath,
                "Unable to decode F# delta payload.");
        }

        if (_trace)
        {
            _logger.LogDebug(
                "F# managed delta ready for '{ProjectPath}' (metadata={MetadataBytes} il={IlBytes} pdb={PdbBytes}).",
                projectInfo.ProjectPath,
                update.Value.MetadataDelta.Length,
                update.Value.ILDelta.Length,
                update.Value.PdbDelta.Length);
        }

        return new FSharpManagedUpdateResult(
            FSharpManagedUpdateStatus.ReadyToApply,
            [new FSharpManagedUpdate(projectInfo.ProjectPath, update.Value)],
            projectInfo.ProjectPath,
            null);
    }

    private ProjectInstanceId? TryGetChangedRunningFSharpProject(
        IReadOnlyList<ChangedFile> changedFiles,
        ImmutableDictionary<string, ImmutableArray<RunningProject>> runningProjects)
    {
        foreach (var file in changedFiles)
        {
            if (!IsFSharpSourcePath(file.Item.FilePath))
            {
                continue;
            }

            foreach (var containingProjectPath in file.Item.ContainingProjectPaths)
            {
                if (!runningProjects.ContainsKey(containingProjectPath))
                {
                    continue;
                }

                foreach (var projectId in _projects.Keys)
                {
                    if (PathUtilities.OSSpecificPathComparer.Equals(projectId.ProjectPath, containingProjectPath))
                    {
                        return projectId;
                    }
                }
            }
        }

        return null;
    }

    private bool EnsureSession(FSharpReflectionHost host, FSharpProjectInfo projectInfo, out FSharpManagedUpdateStatus status, out string? message)
    {
        status = FSharpManagedUpdateStatus.NoChanges;
        message = null;

        if (_activeProject is { } activeProject &&
            _activeProjectOptions != null &&
            activeProject.Equals(projectInfo.ProjectId))
        {
            return true;
        }

        EndSession();

        if (!host.TryCreateProjectOptions(projectInfo, out var projectOptions, out message))
        {
            status = FSharpManagedUpdateStatus.RestartRequired;
            return false;
        }

        var start = host.StartSession(projectOptions!, CancellationToken.None);
        if (!start.IsSuccess)
        {
            status = MapErrorStatus(start.ErrorCase);
            message = start.ErrorText;

            if (status == FSharpManagedUpdateStatus.Blocked)
            {
                _logger.Log(MessageDescriptor.UnableToApplyChanges);
            }

            return false;
        }

        _activeProject = projectInfo.ProjectId;
        _activeProjectOptions = projectOptions;
        return true;
    }

    private bool TryGetHost(FSharpProjectInfo projectInfo, out FSharpReflectionHost host, out string? message)
    {
        host = _host!;
        message = null;

        if (_host != null)
        {
            host = _host;
            return true;
        }

        if (FSharpReflectionHost.TryCreate(projectInfo, _logger, _trace, out var createdHost, out message))
        {
            _host = createdHost;
            host = createdHost;
            return true;
        }

        return false;
    }

    private static FSharpManagedUpdateStatus MapErrorStatus(string? errorCase)
        => errorCase switch
        {
            "NoChanges" => FSharpManagedUpdateStatus.NoChanges,
            "CompilationFailed" => FSharpManagedUpdateStatus.Blocked,
            _ => FSharpManagedUpdateStatus.RestartRequired,
        };

    private static Guid? TryGetModuleVersionId(string assemblyPath)
    {
        try
        {
            using var stream = File.OpenRead(assemblyPath);
            using var peReader = new PEReader(stream);
            var metadataReader = peReader.GetMetadataReader();
            return metadataReader.GetGuid(metadataReader.GetModuleDefinition().Mvid);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsFSharpSourcePath(string path)
    {
        var extension = Path.GetExtension(path);
        return string.Equals(extension, ".fs", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".fsi", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".fsx", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTraceEnabled()
    {
        var value = Environment.GetEnvironmentVariable("DOTNET_WATCH_TRACE_FSHARP_HOTRELOAD");
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FSharpReflectionHost
    {
        private readonly ILogger _logger;
        private readonly bool _trace;
        private readonly object _checker;
        private readonly MethodInfo _getProjectOptions;
        private readonly MethodInfo _startSession;
        private readonly MethodInfo _emitDelta;
        private readonly MethodInfo _endSession;
        private readonly MethodInfo _runSynchronously;

        private FSharpReflectionHost(
            ILogger logger,
            bool trace,
            object checker,
            MethodInfo getProjectOptions,
            MethodInfo startSession,
            MethodInfo emitDelta,
            MethodInfo endSession,
            MethodInfo runSynchronously)
        {
            _logger = logger;
            _trace = trace;
            _checker = checker;
            _getProjectOptions = getProjectOptions;
            _startSession = startSession;
            _emitDelta = emitDelta;
            _endSession = endSession;
            _runSynchronously = runSynchronously;
        }

        public static bool TryCreate(
            FSharpProjectInfo projectInfo,
            ILogger logger,
            bool trace,
            out FSharpReflectionHost host,
            out string? error)
        {
            host = null!;
            error = null;

            var servicePath = Environment.GetEnvironmentVariable("DOTNET_WATCH_FSHARP_COMPILER_SERVICE_PATH");
            if (string.IsNullOrEmpty(servicePath))
            {
                var compilerDirectory = Path.GetDirectoryName(projectInfo.DotnetFscCompilerPath);
                servicePath = compilerDirectory == null ? null : Path.Combine(compilerDirectory, "FSharp.Compiler.Service.dll");
            }

            if (string.IsNullOrEmpty(servicePath) || !File.Exists(servicePath))
            {
                error = "FSharp.Compiler.Service.dll with hot reload APIs was not found.";
                return false;
            }

            try
            {
                var assembly = Assembly.LoadFrom(servicePath);
                var checkerType = assembly.GetType("FSharp.Compiler.CodeAnalysis.FSharpChecker", throwOnError: true)!;

                var createMethod = checkerType.GetMethod("Create", BindingFlags.Public | BindingFlags.Static)
                    ?? throw new MissingMethodException(checkerType.FullName, "Create");

                var checker = createMethod.Invoke(null, createMethod.GetParameters().Select(_ => (object?)null).ToArray())
                    ?? throw new InvalidOperationException("FSharpChecker.Create returned null.");

                var getProjectOptions = checkerType.GetMethod("GetProjectOptionsFromCommandLineArgs", BindingFlags.Public | BindingFlags.Instance)
                    ?? throw new MissingMethodException(checkerType.FullName, "GetProjectOptionsFromCommandLineArgs");

                var startSession = checkerType.GetMethod("StartHotReloadSession", BindingFlags.Public | BindingFlags.Instance)
                    ?? throw new MissingMethodException(checkerType.FullName, "StartHotReloadSession");

                var emitDelta = checkerType.GetMethod("EmitHotReloadDelta", BindingFlags.Public | BindingFlags.Instance)
                    ?? throw new MissingMethodException(checkerType.FullName, "EmitHotReloadDelta");

                var endSession = checkerType.GetMethod("EndHotReloadSession", BindingFlags.Public | BindingFlags.Instance)
                    ?? throw new MissingMethodException(checkerType.FullName, "EndHotReloadSession");

                var fsharpAsyncType = Type.GetType("Microsoft.FSharp.Control.FSharpAsync, FSharp.Core", throwOnError: true)!;
                var runSynchronously = fsharpAsyncType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .First(method => method.Name == "RunSynchronously" && method.IsGenericMethod && method.GetParameters().Length == 3);

                host = new FSharpReflectionHost(logger, trace, checker, getProjectOptions, startSession, emitDelta, endSession, runSynchronously);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public bool TryCreateProjectOptions(FSharpProjectInfo projectInfo, out object? projectOptions, out string? error)
        {
            projectOptions = null;
            error = null;

            try
            {
                var args = projectInfo.CommandLineArgs;
                if (!args.Any(static arg => string.Equals(arg, "--enable:hotreloaddeltas", StringComparison.OrdinalIgnoreCase)))
                {
                    args = args.Add("--enable:hotreloaddeltas");
                }

                projectOptions = _getProjectOptions.Invoke(_checker, [projectInfo.ProjectPath, args.ToArray(), null, null, null]);
                return projectOptions != null;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public FSharpInvocationResult StartSession(object projectOptions, CancellationToken cancellationToken)
            => InvokeResult(_startSession, [projectOptions, null], cancellationToken);

        public FSharpInvocationResult EmitDelta(object projectOptions, CancellationToken cancellationToken)
            => InvokeResult(_emitDelta, [projectOptions, null], cancellationToken);

        public HotReloadManagedCodeUpdate? CreateManagedUpdate(Guid moduleId, object delta)
        {
            try
            {
                var deltaType = delta.GetType();
                var metadata = (byte[]?)deltaType.GetProperty("Metadata")?.GetValue(delta) ?? [];
                var il = (byte[]?)deltaType.GetProperty("IL")?.GetValue(delta) ?? [];

                var pdbOption = deltaType.GetProperty("Pdb")?.GetValue(delta);
                var pdb = pdbOption == null
                    ? []
                    : (byte[]?)pdbOption.GetType().GetProperty("Value")?.GetValue(pdbOption) ?? [];

                var updatedTypesEnumerable = deltaType.GetProperty("UpdatedTypes")?.GetValue(delta) as IEnumerable;
                var updatedTypes = updatedTypesEnumerable == null
                    ? ImmutableArray<int>.Empty
                    : updatedTypesEnumerable.Cast<object>().Select(Convert.ToInt32).ToImmutableArray();

                return new HotReloadManagedCodeUpdate(
                    moduleId,
                    ImmutableArray.CreateRange(metadata),
                    ImmutableArray.CreateRange(il),
                    ImmutableArray.CreateRange(pdb),
                    updatedTypes,
                    ImmutableArray<string>.Empty);
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Failed to materialize F# managed update payload: {Message}", ex.Message);
                return null;
            }
        }

        public void TryEndSession()
        {
            try
            {
                _ = _endSession.Invoke(_checker, null);
            }
            catch (Exception ex)
            {
                if (_trace)
                {
                    _logger.LogDebug("Ignoring F# session cleanup failure: {Message}", ex.Message);
                }
            }
        }

        private FSharpInvocationResult InvokeResult(MethodInfo method, object?[] args, CancellationToken cancellationToken)
        {
            try
            {
                var asyncComputation = method.Invoke(_checker, args)
                    ?? throw new InvalidOperationException($"{method.Name} returned null.");

                var asyncResultType = method.ReturnType.GetGenericArguments().Single();
                var runSync = _runSynchronously.MakeGenericMethod(asyncResultType);
                var result = runSync.Invoke(null, [asyncComputation, null, null])
                    ?? throw new InvalidOperationException($"{method.Name} returned null result.");

                var tag = (int)(result.GetType().GetProperty("Tag")?.GetValue(result) ?? -1);
                if (tag == 0)
                {
                    var value = result.GetType().GetProperty("ResultValue")?.GetValue(result);
                    return new FSharpInvocationResult(true, value, null, null);
                }

                var error = result.GetType().GetProperty("ErrorValue")?.GetValue(result);
                var errorText = error?.ToString() ?? "Unknown error";
                var errorCase = ParseErrorCase(errorText);
                return new FSharpInvocationResult(false, null, errorCase, errorText);
            }
            catch (Exception ex)
            {
                return new FSharpInvocationResult(false, null, null, ex.Message);
            }
        }

        private static string? ParseErrorCase(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            var delimiters = new[] { ' ', '(', ':' };
            var index = text.IndexOfAny(delimiters);
            return index > 0 ? text[..index] : text;
        }

        internal readonly record struct FSharpInvocationResult(bool IsSuccess, object? Value, string? ErrorCase, string? ErrorText);
    }
}
