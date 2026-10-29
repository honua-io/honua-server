// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Runtime.InteropServices;
using Honua.Worker.Gdal;
using Microsoft.Extensions.Configuration;

namespace Honua.Geoprocessing.Cli;

/// <summary>Developer-owned bind mounts must be written by the invoking Unix identity.</summary>
internal static partial class GpContainerConfiguration
{
    internal static IConfiguration Create(Func<string, string?>? environment = null, string? scratchRoot = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["GdalContainer:Image"] = Value("HONUA_GP_WORKER_IMAGE", GdalContainerProbe.DefaultImage),
            ["GdalContainer:DockerExecutable"] = Value("HONUA_GP_CONTAINER_RUNTIME", "docker"),
            ["GdalContainer:User"] = Value("HONUA_GP_CONTAINER_USER", CurrentUser()),
        };
        if (!string.IsNullOrWhiteSpace(scratchRoot))
        {
            // Fresh per-reproduction scratch. Never a path copied from the bundle.
            values["GdalWorker:ScratchRoot"] = scratchRoot;
            // File sinks and feature-stream spills use OutputRootDirectory. Keep it
            // under the reproduction scratch so deleting that tree removes them too.
            values["Geoprocessing:Executors:OutputRootDirectory"] = Path.Join(scratchRoot, "executor-output");
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        string Value(string name, string fallback)
            => environment(name) is { } value && !string.IsNullOrWhiteSpace(value) ? value : fallback;
    }

    internal static string CurrentUser()
        => OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()
            ? string.Create(CultureInfo.InvariantCulture, $"{GetEffectiveUserId()}:{GetEffectiveGroupId()}")
            : "1001:1001";

    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint GetEffectiveUserId();

    [LibraryImport("libc", EntryPoint = "getegid")]
    private static partial uint GetEffectiveGroupId();
}
