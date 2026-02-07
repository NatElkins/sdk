// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.IO;
using Microsoft.Build.Graph;
using Microsoft.Extensions.Logging;

namespace Microsoft.DotNet.Watch.HotReload.FSharp;

internal sealed record FSharpProjectInfo(
    ProjectInstanceId ProjectId,
    string ProjectPath,
    string TargetFramework,
    string TargetPath,
    string DotnetFscCompilerPath,
    ImmutableArray<string> CommandLineArgs)
{
    public static ImmutableDictionary<ProjectInstanceId, FSharpProjectInfo> Collect(ProjectGraph projectGraph, ILogger logger)
    {
        var trace = IsTraceEnabled();
        var builder = ImmutableDictionary.CreateBuilder<ProjectInstanceId, FSharpProjectInfo>();

        foreach (var node in projectGraph.ProjectNodes)
        {
            if (!IsFSharpProject(node))
            {
                continue;
            }

            var projectPath = node.ProjectInstance.FullPath;
            var targetFramework = node.GetTargetFramework();
            var targetPath = node.ProjectInstance.GetPropertyValue(PropertyNames.TargetPath);
            var dotnetFscCompilerPath = node.ProjectInstance.GetPropertyValue("DotnetFscCompilerPath");
            var commandLineArgs = node.ProjectInstance.GetItems("FscCommandLineArgs").Select(item => item.EvaluatedInclude).ToImmutableArray();

            if (string.IsNullOrEmpty(targetPath) || string.IsNullOrEmpty(dotnetFscCompilerPath) || commandLineArgs.IsEmpty)
            {
                if (trace)
                {
                    logger.LogDebug(
                        "Skipping F# project '{ProjectPath}' for managed updates (TargetPath='{TargetPath}', DotnetFscCompilerPath='{CompilerPath}', ArgCount={ArgCount}).",
                        projectPath,
                        targetPath,
                        dotnetFscCompilerPath,
                        commandLineArgs.Length);
                }

                continue;
            }

            var projectId = new ProjectInstanceId(projectPath, targetFramework);
            var projectInfo = new FSharpProjectInfo(
                projectId,
                projectPath,
                targetFramework,
                Path.GetFullPath(targetPath),
                Path.GetFullPath(dotnetFscCompilerPath),
                commandLineArgs);

            if (trace)
            {
                logger.LogDebug(
                    "F# hot reload project discovered: '{ProjectPath}' ({TargetFramework}), compiler='{CompilerPath}', args={ArgCount}.",
                    projectInfo.ProjectPath,
                    projectInfo.TargetFramework,
                    projectInfo.DotnetFscCompilerPath,
                    projectInfo.CommandLineArgs.Length);
            }

            builder[projectId] = projectInfo;
        }

        return builder.ToImmutable();
    }

    private static bool IsFSharpProject(ProjectGraphNode node)
    {
        if (Path.GetExtension(node.ProjectInstance.FullPath).Equals(".fsproj", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var language = node.ProjectInstance.GetPropertyValue("Language");
        return string.Equals(language, "F#", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTraceEnabled()
    {
        var value = Environment.GetEnvironmentVariable("DOTNET_WATCH_TRACE_FSHARP_HOTRELOAD");
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }
}
