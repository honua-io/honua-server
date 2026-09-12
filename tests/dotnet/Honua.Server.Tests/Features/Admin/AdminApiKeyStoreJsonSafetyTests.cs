// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>Guards the durable registry's serialization when the test host enables reflection.</summary>
[Protocol(TestProtocols.Admin)]
[Operation(Operations.ApiKeyManagement)]
public sealed class AdminApiKeyStoreJsonSafetyTests
{
    [UnitTest]
    public void RedisRegistry_AllSerializerCalls_UseGeneratedMetadata()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Join(directory.FullName, "Honua.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var path = Path.Join(directory.FullName, "src/Honua.Hosting/Features/Authentication/AdminApiKeyStore.cs");
        var calls = File.ReadLines(path).Where(line => line.Contains("JsonSerializer.", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(calls);
        // The shipped host disables reflection. JIT HTTP tests alone silently accept the
        // unbound overloads that caused Production creation to return HTTP 500 (#4571).
        Assert.All(calls, call => Assert.Contains("AdminApiKeyStoreJsonContext.Default.AdminApiKeyRecord", call, StringComparison.Ordinal));
    }
}
