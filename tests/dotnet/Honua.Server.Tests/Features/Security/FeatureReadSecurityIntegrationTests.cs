// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using NSubstitute;

namespace Honua.Server.Tests.Features.Security;

[Collection("Database")]
[Protocol(TestProtocols.TestQuality)]
public sealed class FeatureReadSecurityIntegrationTests
{
    [IntegrationTest]
    [Operation(Operations.Query)]
    public async Task GetAsync_FeatureAndRasterShareNumericLayer_EnforcesFeatureMask()
    {
        var masks = Substitute.For<IFieldMaskSource>();
        masks.ResolveAsync(Arg.Any<MetadataV2Resource>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ImmutableArray<string>.Empty));
        var fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro)
            .ReplaceService(masks);
        await fixture.InitializeAsync();
        try
        {
            var graph = fixture.GetCurrentV2GraphSnapshot().Graph;
            Assert.Contains(graph.Resources, resource => resource.Type == MetadataV2ResourceType.RasterDataset);
            Assert.Contains(graph.StorageBindings, binding => binding.Locator == "rasters" && binding.StorageLayerId == 0);
            Assert.Contains(graph.StorageBindings, binding => binding.Locator == "features" && binding.StorageLayerId == 0);
            var id = await fixture.InsertFeatureAsync(0, "visible name");
            var original = (await fixture.GetService<IFeatureReader>().GetAsync(0, id))!.Value;
            await fixture.GetService<IFeatureWriter>().UpdateAsync(0,
                original with { Attributes = original.Attributes.SetItem("population", 12345L) });
            masks.ResolveAsync(Arg.Any<MetadataV2Resource>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(ImmutableArray.Create("population")));

            var feature = (await fixture.GetService<IFeatureReader>().GetAsync(0, id))!.Value;

            Assert.Equal("visible name", feature.Attributes["name"]);
            Assert.False(feature.Attributes.ContainsKey("population"));
        }
        finally { await fixture.DisposeAsync(); }
    }
}
