// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.DotNet.HotReload;

namespace Microsoft.DotNet.Watch;

/// <summary>Identifies the project that owns a Roslyn or F# runtime update.</summary>
internal readonly record struct ManagedCodeUpdateEnvelope(string ProjectPath, HotReloadManagedCodeUpdate Update);
