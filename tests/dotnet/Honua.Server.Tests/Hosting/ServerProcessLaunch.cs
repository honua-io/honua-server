// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using FluentAssertions;

namespace Honua.Server.Tests.Hosting;

// Starts the server from the cached test payload as a separate process in the Production environment, with
// inherited settings removed so the test alone decides the composition.
internal static class ServerProcessLaunch
{
    public static ProcessStartInfo CreateProductionStartInfo(
        IEnumerable<string> inheritedKeysToRemove,
        IReadOnlyDictionary<string, string> environment)
    {
        var serverDirectory = AppContext.BaseDirectory;
        var serverAssembly = Path.Join(serverDirectory, "Honua.Server.dll");
        File.Exists(serverAssembly).Should().BeTrue("the cached test payload contains the server runtime ({0})", serverDirectory);

        var start = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = serverDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(serverAssembly);
        foreach (var key in inheritedKeysToRemove)
        {
            start.Environment.Remove(key);
        }

        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        start.Environment["DOTNET_ENVIRONMENT"] = "Production";
        start.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
        foreach (var (key, value) in environment)
        {
            start.Environment[key] = value;
        }

        return start;
    }
}
