// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Infrastructure;

public sealed class ProgramMiddlewareOrderTests
{
    [UnitTest]
    public void Program_RegistersSerilogRequestLogging_BeforeShortCircuitingMiddleware()
    {
        var source = File.ReadAllText(ResolveProgramPath());

        var serilogIndex = source.IndexOf("app.UseSerilogRequestLogging(", StringComparison.Ordinal);
        var exceptionIndex = source.IndexOf("app.UseGlobalExceptionHandling();", StringComparison.Ordinal);
        var authIndex = source.IndexOf("app.UseApiKeyAuthentication();", StringComparison.Ordinal);
        var outputCacheIndex = source.IndexOf("app.UseOutputCache();", StringComparison.Ordinal);

        serilogIndex.Should().BeGreaterThan(-1);
        exceptionIndex.Should().BeGreaterThan(-1);
        authIndex.Should().BeGreaterThan(-1);
        outputCacheIndex.Should().BeGreaterThan(-1);

        serilogIndex.Should().BeLessThan(exceptionIndex);
        serilogIndex.Should().BeLessThan(authIndex);
        serilogIndex.Should().BeLessThan(outputCacheIndex);
    }

    private static string ResolveProgramPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "Honua.Server", "Program.cs");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate src/Honua.Server/Program.cs from the test base directory.");
    }
}
