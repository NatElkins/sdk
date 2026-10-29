#!/usr/bin/env python3
"""Verify the standalone watch build and focused bridge units with an isolated SDK fixture."""

import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[1]
DOTNET = ROOT / ".dotnet" / "dotnet"
WATCH_PROJECT = "src/Dotnet.Watch/dotnet-watch/dotnet-watch.csproj"
TEST_PROJECT = "test/dotnet-watch.Tests/dotnet-watch.Tests.csproj"
FILTER = "|".join(
    "FullyQualifiedName~" + name
    for name in (
        "FSharpHotReloadServiceTests",
        "FSharpReflectionHostTests",
        "FSharpSdkCompilerTests",
        "FileWatcherTests",
    )
)


def run(arguments, **kwargs):
    """Run a command from the repository root and preserve its failure status."""
    subprocess.run([str(value) for value in arguments], cwd=ROOT, check=True, **kwargs)


def main():
    run(["bash", "eng/common/dotnet.sh", "--info"])
    versions = ET.parse(ROOT / "eng/Version.Details.props").getroot()
    runtime = versions.findtext(".//MicrosoftNETCorePlatformsPackageVersion")
    aspnet = versions.findtext(".//MicrosoftAspNetCoreComponentsSdkAnalyzersPackageVersion")
    if not runtime or not aspnet:
        raise RuntimeError("The SDK test runtime versions are absent.")
    run([ROOT / "eng/dotnetup/dotnetup", "runtime", "install",
         f"runtime@{runtime}", f"aspnetcore@{aspnet}", "--install-path", ROOT / ".dotnet",
         "--untracked", "--no-progress"])
    run([DOTNET, "build", WATCH_PROJECT, "-c", "Release", "-p:SdkTargetFramework=net10.0", "-v:minimal"])
    run([DOTNET, "build", WATCH_PROJECT, "-c", "Release", "-v:minimal"])
    framework = subprocess.check_output(
        [str(DOTNET), "msbuild", WATCH_PROJECT, "-getProperty:TargetFramework"], cwd=ROOT, text=True
    ).strip()
    sdk = subprocess.check_output([str(DOTNET), "--version"], cwd=ROOT, text=True).strip()
    scratch = ROOT / "artifacts/tmp"
    scratch.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="fsharp-watch-units-", dir=scratch) as temporary:
        artifacts = Path(temporary) / "artifacts"
        fixture = artifacts / "bin/redist/Release/dotnet"
        shutil.copytree(ROOT / ".dotnet", fixture, symlinks=True)
        watch = fixture / f"sdk/{sdk}/DotnetTools/dotnet-watch/{sdk}/tools/{framework}/any"
        if watch.exists():
            shutil.rmtree(watch)
        shutil.copytree(ROOT / f"artifacts/bin/dotnet-watch/Release/{framework}", watch)
        # These units use SDK MSBuild and FSharp.Core, plus the freshly built watch assemblies.
        # Installed application scenarios belong to the separate package acceptance gate.
        environment = dict(os.environ, DOTNET_SDK_ARTIFACTS_DIR=str(artifacts))
        run([DOTNET, "scripts/RunTests.cs", "--", "--project", TEST_PROJECT,
             "--configuration", "Release", "--filter", FILTER], env=environment)


if __name__ == "__main__":
    main()
