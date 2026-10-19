// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Runtime.InteropServices;
using Honua.Worker.Gdal;
using Microsoft.Extensions.Configuration;

namespace Honua.Geoprocessing.Cli;

/// <summary>Developer-owned bind mounts must be written by the invoking Unix identity.</summary>
internal static partial class GpContainerConfiguration
{
    internal static IConfiguration Create(Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        return new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["GdalContainer:Image"] = Value("HONUA_GP_WORKER_IMAGE", GdalContainerProbe.DefaultImage),
            ["GdalContainer:DockerExecutable"] = Value("HONUA_GP_CONTAINER_RUNTIME", "docker"),
            ["GdalContainer:User"] = Value("HONUA_GP_CONTAINER_USER", CurrentUser()),
        }).Build();

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
