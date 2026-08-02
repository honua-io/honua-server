// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Raster;
using Xunit;

namespace Honua.Architecture.Tests;

/// <summary>Architecture invariants for the canonical raster GP source contract.</summary>
[Trait("Category", "Architecture")]
public sealed class RasterSourceContractArchitectureTests
{
    private static readonly string[] _forbiddenProviderPrefixes =
    [
        "Amazon.",
        "Azure.",
        "Google.Cloud.",
        "Npgsql",
        "OSGeo.",
    ];

    [Fact]
    public void DescriptorHierarchy_ShouldRemainInCoreAndUseSourceGeneratedJson()
    {
        typeof(RasterSourceDescriptor).Assembly.GetName().Name.Should().Be("Honua.Core");
        RasterSourceJsonContext.Default.RasterSourceDescriptor.Should().NotBeNull();
        RasterSourceJsonContext.Default.PostgisRasterSourceDescriptor.Should().NotBeNull();
        RasterSourceJsonContext.Default.ObjectStoreCogRasterSourceDescriptor.Should().NotBeNull();
        RasterSourceJsonContext.Default.ObjectStoreZarrRasterSourceDescriptor.Should().NotBeNull();
        RasterSourceJsonContext.Default.StagedArtifactRasterSourceDescriptor.Should().NotBeNull();
        RasterSourceJsonContext.Default.InlineRasterSourceDescriptor.Should().NotBeNull();
    }

    [Fact]
    public void DescriptorPublicSurface_ShouldNotLeakProviderSdkTypes()
    {
        var contractTypes = typeof(RasterSourceDescriptor).Assembly
            .GetTypes()
            .Where(type => string.Equals(
                type.Namespace,
                typeof(RasterSourceDescriptor).Namespace,
                StringComparison.Ordinal))
            .ToArray();

        var leakedTypes = contractTypes
            .SelectMany(type => type.GetProperties().Select(property => property.PropertyType))
            .SelectMany(FlattenType)
            .Where(type => _forbiddenProviderPrefixes.Any(prefix =>
                (type.FullName ?? string.Empty).StartsWith(prefix, StringComparison.Ordinal)))
            .Select(type => type.FullName)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        leakedTypes.Should().BeEmpty(
            "canonical raster descriptors may carry only provider-neutral identifiers and metadata");
    }

    private static IEnumerable<Type> FlattenType(Type type)
    {
        yield return type;
        if (!type.IsGenericType)
        {
            yield break;
        }

        foreach (var argument in type.GetGenericArguments())
        {
            yield return argument;
        }
    }
}
