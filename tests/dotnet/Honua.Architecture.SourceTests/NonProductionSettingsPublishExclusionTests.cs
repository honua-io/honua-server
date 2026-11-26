// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Honua.Architecture.Tests;

[Trait("Category", "Architecture")]
public sealed class NonProductionSettingsPublishExclusionTests
{
    private static readonly string[] ExcludedSettingsFiles =
    [
        "appsettings.Development.json",
        "appsettings.Test.json",
    ];

    [ArchitectureTest]
    public void ServerProject_ExcludesDevelopmentAndTestSettingsFromPublishOutput()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var projectPath = ArchitectureTestHelpers.CombinePath(repositoryRoot, "src", "Honua.Server", "Honua.Server.csproj");
        var document = XDocument.Load(projectPath);

        foreach (var fileName in ExcludedSettingsFiles)
        {
            var entry = document.Descendants("Content")
                .SingleOrDefault(element => string.Equals((string?)element.Attribute("Update"), fileName, StringComparison.Ordinal));
            entry.Should().NotBeNull("'{0}' must be explicitly excluded from publish output so non-production settings stay out of published images.", fileName);
            entry!.Element("CopyToPublishDirectory")?.Value.Should().Be("Never");
            entry.Element("CopyToOutputDirectory")?.Value.Should().Be("PreserveNewest");
        }
    }
}
