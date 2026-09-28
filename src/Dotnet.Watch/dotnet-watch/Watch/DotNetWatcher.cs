// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;
using Microsoft.Build.Graph;
using Microsoft.Extensions.Logging;

namespace Microsoft.DotNet.Watch;

internal static class DotNetWatcher
{
    public static async Task WatchAsync(DotNetWatchContext context, CancellationToken shutdownCancellationToken)
    {
        using var compilerPreparation = Environment.GetEnvironmentVariable("FSHARP_WATCH_SDK_DIRECTORY") != null
            ? new FSharpSdkPreparation(context)
            : null;
        var cancelledTaskSource = new TaskCompletionSource();
        shutdownCancellationToken.Register(state => ((TaskCompletionSource)state!).TrySetResult(),
            cancelledTaskSource);

        if (context.EnvironmentOptions.SuppressMSBuildIncrementalism)
        {
            context.Logger.LogDebug("MSBuild incremental optimizations suppressed.");
        }

        var environmentBuilder = new Dictionary<string, string>();

        ChangedFile? changedFile = null;
        var buildEvaluator = new BuildEvaluator(context);

        for (var iteration = 0; ; iteration++)
        {
            if (await buildEvaluator.EvaluateAsync(changedFile, shutdownCancellationToken) is not { } evaluationResult)
            {
                context.Logger.LogError("Failed to find a list of files to watch");
                return;
            }

            StaticFileHandler? staticFileHandler;
            ProjectGraphNode? projectRootNode;
            if (evaluationResult.ProjectGraph != null)
            {
                compilerPreparation?.Prepare(evaluationResult.ProjectGraph.Graph);
                projectRootNode = evaluationResult.ProjectGraph.Graph.GraphRoots.Single();
                staticFileHandler = new StaticFileHandler(context.Logger, evaluationResult.ProjectGraph, context.BrowserRefreshServerFactory);
            }
            else
            {
                context.Logger.LogDebug("Unable to determine if this project is a webapp.");
                projectRootNode = null;
                staticFileHandler = null;
            }

            var processSpec = new ProcessSpec
            {
                Executable = context.EnvironmentOptions.GetMuxerPath(),
                WorkingDirectory = context.EnvironmentOptions.WorkingDirectory,
                IsUserApplication = true,
                Arguments = buildEvaluator.GetProcessArguments(iteration),
                EnvironmentVariables =
                {
                    [EnvironmentVariables.Names.DotnetWatch] = "1",
                    [EnvironmentVariables.Names.DotnetWatchIteration] = (iteration + 1).ToString(CultureInfo.InvariantCulture),
                }
            };

            var browserRefreshServer = projectRootNode != null && HotReloadAppModel.InferFromProject(context, projectRootNode) is WebApplicationAppModel webAppModel
                ? await context.BrowserRefreshServerFactory.GetOrCreateBrowserRefreshServerAsync(projectRootNode, webAppModel, shutdownCancellationToken)
                : null;

            browserRefreshServer?.ConfigureLaunchEnvironment(environmentBuilder, enableHotReload: false);

            Action<OutputLine>? outputObserver = null;
            if (projectRootNode != null)
            {
                Debug.Assert(context.MainProjectOptions != null);
                outputObserver = context.BrowserLauncher.TryGetBrowserLaunchOutputObserver(projectRootNode, context.MainProjectOptions, browserRefreshServer, shutdownCancellationToken);
            }

            processSpec.RedirectOutput(outputObserver, context.ProcessOutputReporter, context.EnvironmentOptions, projectRootNode?.GetDisplayName() ?? "");

            foreach (var (name, value) in environmentBuilder)
            {
                processSpec.EnvironmentVariables.Add(name, value);
            }

            // Reset for next run
            buildEvaluator.RequiresRevaluation = false;

            if (shutdownCancellationToken.IsCancellationRequested)
            {
                return;
            }

            using var currentRunCancellationSource = new CancellationTokenSource();
            using var combinedCancellationSource = CancellationTokenSource.CreateLinkedTokenSource(shutdownCancellationToken, currentRunCancellationSource.Token);
            using var fileSetWatcher = new FileWatcher(context.Logger, context.EnvironmentOptions);

            fileSetWatcher.WatchContainingDirectories(evaluationResult.Files.Keys, includeSubdirectories: true);
            var inputRevision = 0;
            fileSetWatcher.OnFileChange += change =>
            {
                if (evaluationResult.Files.ContainsKey(change.Path) || evaluationResult.ProjectGraph?.BuildFiles.Contains(change.Path) == true)
                {
                    Interlocked.Increment(ref inputRevision);
                }
            };

            var processTask = context.ProcessRunner.RunAsync(processSpec, context.Logger, launchResult: null, combinedCancellationSource.Token);

            Task<ChangedFile?> fileSetTask = Task.FromResult<ChangedFile?>(null);
            Task finishedTask = Task.CompletedTask;

            context.Logger.Log(MessageDescriptor.WaitingForChanges);

            try
            {
                while (true)
                {
                    fileSetTask = fileSetWatcher.WaitForFileChangeAsync(evaluationResult.Files, startedWatching: null, combinedCancellationSource.Token);
                    finishedTask = await Task.WhenAny(processTask, fileSetTask, cancelledTaskSource.Task);

                    if (staticFileHandler != null && finishedTask == fileSetTask && fileSetTask.Result.HasValue)
                    {
                        if (await staticFileHandler.HandleFileChangesAsync([fileSetTask.Result.Value], combinedCancellationSource.Token))
                        {
                            // We're able to handle the file change event without doing a full-rebuild.
                            continue;
                        }
                    }

                    if (compilerPreparation is { HasFSharpProjects: true } && finishedTask == fileSetTask && fileSetTask.Result.HasValue)
                    {
                        var inputs = evaluationResult.Files.Keys.Concat(evaluationResult.ProjectGraph?.BuildFiles.AsEnumerable() ?? []);
                        bool ready;
                        int revision;
                        do
                        {
                            revision = Volatile.Read(ref inputRevision);
                            // A correction during preflight must be checked without another file event.
                            ready = await compilerPreparation.PreflightAsync(inputs, () => revision != Volatile.Read(ref inputRevision), combinedCancellationSource.Token);
                        }
                        while (revision != Volatile.Read(ref inputRevision));

                        if (!ready)
                        {
                            continue;
                        }
                    }

                    break;
                }
            }
            finally
            {
                // Compiler replacement also leaves this loop. Always terminate the old process before exit 75.
                currentRunCancellationSource.Cancel();
                try
                {
                    await Task.WhenAll(processTask, fileSetTask);
                }
                catch (OperationCanceledException) when (combinedCancellationSource.IsCancellationRequested)
                {
                }
            }

            if (finishedTask == cancelledTaskSource.Task || shutdownCancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (finishedTask == processTask)
            {
                // Process exited. Redo evalulation
                buildEvaluator.RequiresRevaluation = true;

                // Now wait for a file to change before restarting process
                changedFile = await fileSetWatcher.WaitForFileChangeAsync(
                    evaluationResult.Files,
                    startedWatching: () => context.Logger.Log(MessageDescriptor.WaitingForFileChangeBeforeRestarting),
                    shutdownCancellationToken);
            }
            else
            {
                Debug.Assert(finishedTask == fileSetTask);
                changedFile = fileSetTask.Result;
                Debug.Assert(changedFile != null, "ChangedFile should only be null when cancelled");
                context.Logger.LogInformation("File changed: {Path}", changedFile.Value.Item.FilePath);
            }
        }
    }
}
