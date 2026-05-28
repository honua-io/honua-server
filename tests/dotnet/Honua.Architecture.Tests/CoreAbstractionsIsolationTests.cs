// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Xml.Linq;
using FluentAssertions;
using NetArchTest.Rules;
using Xunit;

namespace Honua.Architecture.Tests;

/// <summary>
/// Enforces the Honua.Core.Abstractions isolation contract: the abstractions assembly is
/// the contract surface that protocol assemblies (modularization Phase 1) consume in
/// place of Honua.Core. It must not pull a transitive dependency on heavy data-layer
/// or geospatial libraries, so protocol assemblies stay light-weight.
/// </summary>
/// <remarks>
/// See <c>docs/contributor/adr/0041-core-abstractions-extraction.md</c> for the rationale
/// and the multi-PR sequencing plan that moves the rest of the abstractions surface
/// into this assembly.
/// </remarks>
[Trait("Category", "Architecture")]
public sealed class CoreAbstractionsIsolationTests
{
    private static readonly string[] BannedPackages =
    {
        "Npgsql",
        "NetTopologySuite",
        "AWSSDK",
        "Parquet",
        "FlatGeobuf",
    };

    [ArchitectureTest]
    public void Abstractions_ShouldNotDependOn_HeavyPackages()
    {
        var abstractionsAssembly = typeof(Honua.Core.Features.Infrastructure.Events.Outbox.IFeatureChangeOutboxRepository).Assembly;

        var result = Types.InAssembly(abstractionsAssembly)
            .ShouldNot()
            .HaveDependencyOnAny(BannedPackages)
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "Honua.Core.Abstractions must not depend on heavy packages: {0}. Failing types: {1}",
            string.Join(", ", BannedPackages),
            result.FailingTypeNames is null ? "(none reported)" : string.Join(", ", result.FailingTypeNames));
    }

    [ArchitectureTest]
    public void AbstractionsCsproj_ShouldNotReference_HeavyPackages()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var csprojPath = Path.Combine(
            repositoryRoot,
            "src",
            "Honua.Core.Abstractions",
            "Honua.Core.Abstractions.csproj");

        File.Exists(csprojPath).Should().BeTrue("Honua.Core.Abstractions.csproj must exist at the canonical path");

        var document = XDocument.Load(csprojPath);
        var packageReferences = document
            .Descendants("PackageReference")
            .Select(element => element.Attribute("Include")?.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToList();

        foreach (var banned in BannedPackages)
        {
            packageReferences
                .Should()
                .NotContain(
                    package => package.StartsWith(banned, StringComparison.OrdinalIgnoreCase),
                    "Honua.Core.Abstractions.csproj must not directly reference {0}",
                    banned);
        }
    }

    [ArchitectureTest]
    public void AbstractionsCsproj_ShouldNotReference_HonuaCore()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var csprojPath = Path.Combine(
            repositoryRoot,
            "src",
            "Honua.Core.Abstractions",
            "Honua.Core.Abstractions.csproj");

        var document = XDocument.Load(csprojPath);
        var projectReferences = document
            .Descendants("ProjectReference")
            .Select(element => element.Attribute("Include")?.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToList();

        projectReferences
            .Should()
            .NotContain(
                value => value.Contains("Honua.Core.csproj", StringComparison.OrdinalIgnoreCase),
                "Honua.Core.Abstractions must not depend on Honua.Core (the dependency direction is reversed by design)");
    }
}
