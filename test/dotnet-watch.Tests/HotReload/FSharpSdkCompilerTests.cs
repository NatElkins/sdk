// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Logging.Abstractions;

namespace Microsoft.DotNet.Watch.UnitTests;

[TestClass]
public class FSharpSdkCompilerTests
{
    [TestMethod]
    [DataRow("fsc.dll")]
    [DataRow("FSharp.Core.dll")]
    [DataRow("fsc.deps.json")]
    [DataRow("fsc.runtimeconfig.json")]
    public void CompilerIdentityDetectsReplacementWithSameSizeAndTimestamp(string changedFile)
    {
        var directory = Directory.CreateTempSubdirectory("watch-compiler-identity-");
        try
        {
            var compiler = Path.Combine(directory.FullName, "fsc.dll");
            File.WriteAllText(compiler, "original");
            var changedPath = Path.Combine(directory.FullName, changedFile);
            File.WriteAllText(changedPath, "original");
            var timestamp = File.GetLastWriteTimeUtc(changedPath);
            var identity = FSharpCompilerIdentity.Read(compiler);

            File.WriteAllText(changedPath, "modified");
            File.SetLastWriteTimeUtc(changedPath, timestamp);

            Assert.Throws<FSharpCompilerChangedException>(identity.VerifyUnchanged);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void CompilerIdentityDetectsNewDependencyAndMissingCompiler()
    {
        var directory = Directory.CreateTempSubdirectory("watch-compiler-dependency-");
        try
        {
            var compiler = Path.Combine(directory.FullName, "fsc.dll");
            File.WriteAllText(compiler, "original");
            var identity = FSharpCompilerIdentity.Read(compiler);
            File.WriteAllText(Path.Combine(directory.FullName, "Dependency.dll"), "new");
            Assert.Throws<FSharpCompilerChangedException>(identity.VerifyUnchanged);

            identity = FSharpCompilerIdentity.Read(compiler);
            File.Delete(compiler);
            Assert.Throws<FSharpCompilerChangedException>(identity.VerifyUnchanged);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void CompilerContextKeepsItsCoreSeparateFromTheDefaultContext()
    {
        var compilerDirectory = Path.Combine(SdkTestContext.Current.ToolsetUnderTest.SdkFolderUnderTest, "FSharp");
        var defaultCore = Assembly.LoadFrom(Path.Combine(compilerDirectory, "FSharp.Core.dll"));
        var context = new FSharpCompilerLoadContext(Path.Combine(compilerDirectory, "fsc.dll"));
        try
        {
            var compilerCore = context.LoadFromAssemblyName(new AssemblyName("FSharp.Core"));
            Assert.AreNotSame(defaultCore, compilerCore);
            Assert.AreSame(context, AssemblyLoadContext.GetLoadContext(compilerCore));
            Assert.AreEqual(Path.GetFullPath(Path.Combine(compilerDirectory, "FSharp.Core.dll")), compilerCore.Location);
        }
        finally
        {
            context.Unload();
        }
    }

    [TestMethod]
    public void MissingCompilerProbeReturnsAnUnsupportedResult()
    {
        var result = FSharpHotReloadService.ProbeCompiler(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "fsc.dll"), NullLogger.Instance);
        Assert.IsFalse(result.Supported);
        Assert.IsNotNull(result.Reason);
        Assert.IsNull(result.Identity);
    }

    [TestMethod]
    public void SessionProbeRejectsRequiredNamesWithIncompatibleSignatures()
    {
        var host = typeof(FSharpHotReloadService).GetNestedType("FSharpReflectionHost", BindingFlags.NonPublic)!;
        var api = host.GetNestedType("SessionObjectApi", BindingFlags.NonPublic)!;
        var probe = api.GetMethod("TryCreate", BindingFlags.Static | BindingFlags.Public)!;
        Assert.IsNull(probe.Invoke(null, [typeof(IncompatibleChecker)]));
    }

    private sealed class IncompatibleChecker
    {
        public IncompatibleSession CreateHotReloadSession(object capabilities) => new();
    }

    private sealed class IncompatibleSession : IDisposable
    {
        public object AddProject(object snapshot) => snapshot;
        public object EmitDelta(object snapshot) => snapshot;
        public void UpdateCapabilities(string capabilities) { }
        public void Commit() { }
        public void Discard() { }
        public void Dispose() { }
    }
}
