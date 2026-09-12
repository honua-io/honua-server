// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;

namespace Honua.Core.Tests.Raster.CogParser;

/// <summary>Prevents registered-cloud and imported raster serving claims from being conflated.</summary>
public sealed class CogLifecycleDocumentationTests
{
    [Theory]
    [InlineData("docs/reference/protocols/cloud-native-formats.md")]
    [InlineData("docs/guides/publish/publish-rasters.md")]
    public void CloudCogDocs_DeclareGaTileWorkflowAndRestrictions(string path)
    {
        var text = File.ReadAllText(Path.Combine(FindRepositoryRoot(), path));
        text.Should().Contain("ImageServer tile fallback");
        text.Should().Contain("COG is a 2026.1 GA target");
        text.Should().Contain("lossless PNG");
        text.Should().NotContain("pending an operator ruling");
        text.Should().Contain("GoogleMapsCompatible");
        text.Should().Contain("JPEGTables");
        text.Should().NotContain("Both surface through the same protocol adapters");
        text.Should().NotContain("registered raster serves through the same pipeline as imported rasters");
        text.Should().NotContain("other SRIDs are logged as potentially problematic");
    }
    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Honua.sln")))
            {
                return directory.FullName;
            }
        }
        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
