// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
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

    /// <summary>
    /// Audit C3 goal-state assertion: <c>IDatabaseConnectionProvider</c> must
    /// not leak <c>System.Data.Common.DbConnection</c> /
    /// <c>System.Data.Common.DbTransaction</c> through its public surface.
    /// </summary>
    /// <remarks>
    /// Currently skipped. The session abstraction (<c>IDatabaseSession</c> /
    /// <c>IDatabaseSessionFactory</c>) ships side-by-side with the legacy
    /// provider; the leaky members will be removed once the follow-on
    /// row-mapping / typed-parameter / bulk-load facilities land. Remove
    /// the <c>Skip</c> and update the implementations in lockstep at that
    /// point. See ADR-0046 for the migration plan and triage rationale.
    /// </remarks>
    [Fact(Skip = "Audit C3 deferred — see ADR-0046. Unskip when the leaky members are removed.")]
    [Trait("Category", "Architecture")]
    public void DatabaseConnectionProvider_ShouldNotLeak_AdoNetTypes()
    {
        var providerInterface = typeof(Honua.Core.Features.Infrastructure.Abstractions.IDatabaseConnectionProvider);

        var bannedTypes = new[]
        {
            typeof(System.Data.Common.DbConnection),
            typeof(System.Data.Common.DbTransaction),
        };

        var leakyMembers = new List<string>();
        foreach (var method in providerInterface.GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (MentionsAny(method.ReturnType, bannedTypes))
            {
                leakyMembers.Add($"return:{method.Name}");
            }

            foreach (var parameter in method.GetParameters())
            {
                if (MentionsAny(parameter.ParameterType, bannedTypes))
                {
                    leakyMembers.Add($"{method.Name}({parameter.Name})");
                }
            }
        }

        leakyMembers.Should().BeEmpty(
            "IDatabaseConnectionProvider must not leak System.Data.Common types — leaks: {0}",
            string.Join(", ", leakyMembers));
    }

    private static bool MentionsAny(Type type, IReadOnlyList<Type> bannedTypes)
    {
        if (bannedTypes.Any(banned => banned.IsAssignableFrom(type)))
        {
            return true;
        }

        if (type.IsGenericType)
        {
            foreach (var argument in type.GetGenericArguments())
            {
                if (MentionsAny(argument, bannedTypes))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static readonly string[] CoreBannedGeometryPackages =
    {
        "FlatGeobuf",
        "Parquet.Net",
        "Snappier",
        "NetTopologySuite",
        "NetTopologySuite.IO.GeoJSON",
    };

    /// <summary>
    /// Asserts that the geometry-subsystem extraction kept the heavy geospatial
    /// packages (FlatGeobuf / Parquet.Net / Snappier / NetTopologySuite) out of
    /// <c>Honua.Core.csproj</c>. Those packages now live with the readers, the
    /// Spec subsystem, the WKT/GeoJSON-consuming filter parsers, and the
    /// NlQuery filter-plan compiler inside <c>Honua.Geometry</c>.
    /// </summary>
    [ArchitectureTest]
    public void HonuaCoreCsproj_ShouldNotReference_GeometryFileFormatPackages()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var csprojPath = Path.Combine(
            repositoryRoot,
            "src",
            "Honua.Core",
            "Honua.Core.csproj");

        File.Exists(csprojPath).Should().BeTrue("Honua.Core.csproj must exist at the canonical path");

        var document = XDocument.Load(csprojPath);
        var packageReferences = document
            .Descendants("PackageReference")
            .Select(element => element.Attribute("Include")?.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToList();

        foreach (var banned in CoreBannedGeometryPackages)
        {
            packageReferences
                .Should()
                .NotContain(
                    package => package.Equals(banned, StringComparison.OrdinalIgnoreCase),
                    "Honua.Core.csproj must not directly reference {0} — the geometry file-format readers belong in Honua.Geometry",
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
