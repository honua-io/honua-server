// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.CustomCode.Harness;
using Xunit;

namespace Honua.CustomCode.Harness.Tests;

public sealed class UserProjectBuilderTests
{
    [Fact]
    public void Build_RestoresOnlyFromTheWarmPackageCache()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            File.WriteAllText(Path.Join(root.FullName, "tool.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            IReadOnlyList<string>? captured = null;
            var builder = new UserProjectBuilder((args, _) =>
            {
                captured = args;
                return new ProcessResult(0, "build ok", "");
            });

            builder.Build(root.FullName, "tool.csproj", Path.Join(root.FullName, "out"));

            captured.Should().NotBeNull();
            var source = captured!.ToList().IndexOf("--source");
            source.Should().BeGreaterThan(0);
            captured[source + 1].Should().Be(UserProjectBuilder.WarmPackageSource);
            captured.Should().ContainSingle(arg => arg == "--source");
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void Build_IgnoresFeedsTheUserProjectAdds()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            File.WriteAllText(Path.Join(root.FullName, "tool.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            IReadOnlyList<string>? captured = null;
            var builder = new UserProjectBuilder((args, _) =>
            {
                captured = args;
                return new ProcessResult(0, "build ok", "");
            });

            builder.Build(root.FullName, "tool.csproj", Path.Join(root.FullName, "out"));

            captured.Should().Contain("-p:RestoreAdditionalProjectSources=")
                .And.Contain("-p:RestoreAdditionalProjectFallbackFolders=");
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void RemoveCredentials_StartsTheBuildWithoutJobCredentials()
    {
        var psi = new System.Diagnostics.ProcessStartInfo("dotnet");
        psi.Environment["HONUA_JOB_TOKEN"] = "scoped";
        psi.Environment["HONUA_JOB_REDEMPTION_CODE"] = "code";
        psi.Environment["PATH"] = "/usr/bin";

        UserProjectBuilder.RemoveCredentials(psi);

        psi.Environment.Should().NotContainKey("HONUA_JOB_TOKEN").And.NotContainKey("HONUA_JOB_REDEMPTION_CODE");
        psi.Environment.Should().ContainKey("PATH");
    }
}
