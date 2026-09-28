// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;

namespace Microsoft.DotNet.Watch;

/// <summary>Content identity of one compiler and its adjacent dependency closure.</summary>
internal sealed record FSharpCompilerIdentity(string CompilerPath, string ContentHash)
{
    public static FSharpCompilerIdentity Read(string compilerPath)
    {
        compilerPath = Path.GetFullPath(compilerPath);
        if (!File.Exists(compilerPath))
        {
            throw new FileNotFoundException("The evaluated F# compiler does not exist.", compilerPath);
        }

        var directory = Path.GetDirectoryName(compilerPath)!;
        // Include manifests and runtime configuration, plus dependencies that appear after startup.
        var files = Directory.EnumerateFiles(directory).Where(path => Path.GetExtension(path) is ".dll" or ".exe" or ".json" or ".so" or ".dylib");
        return new(compilerPath, HashFiles(files));
    }

    internal static string HashFiles(IEnumerable<string> paths)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in paths.Distinct(PathUtilities.OSSpecificPathComparer).Order(StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(path + "\0"));
            if (File.Exists(path))
            {
                using var file = File.OpenRead(path);
                hash.AppendData(SHA256.HashData(file));
            }
            else
            {
                hash.AppendData([0]);
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public void VerifyUnchanged()
    {
        try
        {
            if (Read(CompilerPath) == this)
            {
                return;
            }
        }
        catch (IOException)
        {
            // A compiler replacement can temporarily remove a file.
        }

        throw new FSharpCompilerChangedException();
    }
}

/// <summary>Requests a fresh watcher before another baseline or delta can use a changed compiler.</summary>
internal sealed class FSharpCompilerChangedException() : Exception("The SDK or F# compiler changed. A fresh watch session is required.");

/// <summary>Loads compiler dependencies without sharing the watcher's compiler assembly identities.</summary>
internal sealed class FSharpCompilerLoadContext(string compilerPath) : AssemblyLoadContext(isCollectible: true)
{
    private readonly string _directory = Path.GetDirectoryName(Path.GetFullPath(compilerPath))!;
    private readonly AssemblyDependencyResolver _resolver = new(Path.GetFullPath(compilerPath));

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var adjacent = Path.Combine(_directory, assemblyName.Name + ".dll");
        var path = File.Exists(adjacent) ? adjacent : _resolver.ResolveAssemblyToPath(assemblyName);
        if (path != null)
        {
            return LoadFromAssemblyPath(path);
        }

        if (assemblyName.Name is "FSharp.Core" or "FSharp.Compiler.Service")
        {
            throw new FileNotFoundException("The SDK compiler dependency is absent.", adjacent);
        }

        return null;
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
        => _resolver.ResolveUnmanagedDllToPath(unmanagedDllName) is { } path ? LoadUnmanagedDllFromPath(path) : 0;
}

/// <summary>The private probe result uses values that do not retain compiler types.</summary>
internal sealed record FSharpCompilerProbe(bool Supported, string? Reason, string CompilerPath, FSharpCompilerIdentity? Identity);
