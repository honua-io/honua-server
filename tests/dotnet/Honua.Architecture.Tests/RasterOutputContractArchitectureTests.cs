// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Raster;
using Xunit;

namespace Honua.Architecture.Tests;

/// <summary>Architecture invariants for metadata-only raster GP outputs.</summary>
[Trait("Category", "Architecture")]
public sealed class RasterOutputContractArchitectureTests
{
    [Fact]
    public void DescriptorHierarchy_ShouldRemainInCoreAndUseSourceGeneratedJson()
    {
        typeof(RasterOutputDescriptor).Assembly.GetName().Name.Should().Be("Honua.Core");
        RasterOutputJsonContext.Default.RasterOutputDescriptor.Should().NotBeNull();
        RasterOutputJsonContext.Default.ObjectStoreRasterOutputDescriptor.Should().NotBeNull();
        RasterOutputJsonContext.Default.PostgisRasterOutputDescriptor.Should().NotBeNull();
        RasterOutputJsonContext.Default.InlineRasterOutputDescriptor.Should().NotBeNull();
        RasterOutputJsonContext.Default.StagedRasterOutputDescriptor.Should().NotBeNull();
        RasterOutputJsonContext.Default.RasterOutputPublicationManifest.Should().NotBeNull();
    }

    [Fact]
    public void ReferencedDescriptors_ShouldNotExposeBytesUrlsStreamsOrProviderTypes()
    {
        var referencedTypes = new[]
        {
            typeof(ObjectStoreRasterOutputDescriptor),
            typeof(PostgisRasterOutputDescriptor),
            typeof(StagedRasterOutputDescriptor),
            typeof(RasterOutputRegistrationCommand)
        };
        var forbiddenPrefixes = new[]
        {
            "Amazon.",
            "Azure.",
            "Google.Cloud.",
            "Npgsql",
            "OSGeo."
        };

        var properties = referencedTypes.SelectMany(type => type.GetProperties()).ToArray();

        properties.Should().NotContain(property => property.PropertyType == typeof(byte[])
            || typeof(Stream).IsAssignableFrom(property.PropertyType)
            || property.PropertyType == typeof(Uri));
        properties.Select(property => property.PropertyType.FullName ?? string.Empty)
            .Should().NotContain(typeName => forbiddenPrefixes.Any(prefix =>
                typeName.StartsWith(prefix, StringComparison.Ordinal)));
    }

    [Fact]
    public void ServingProject_ShouldNotReferenceGdalWorkerOrNativeGdalPackages()
    {
        var root = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var serverProject = ArchitectureTestHelpers.CombinePath(
            root,
            "src",
            "Honua.Server",
            "Honua.Server.csproj");

        ArchitectureTestHelpers.DirectProjectReferenceNames(serverProject)
            .Should().NotContain("Honua.Worker.Gdal");
        ArchitectureTestHelpers.DirectPackageReferenceNames(serverProject)
            .Should().NotContain(name => name.Contains("GDAL", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("OSGeo", StringComparison.OrdinalIgnoreCase));
    }
}
