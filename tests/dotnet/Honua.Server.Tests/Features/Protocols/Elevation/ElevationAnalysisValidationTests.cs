// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Server.Features.Protocols.Elevation;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Protocols.Elevation;

[Protocol(TestProtocols.Elevation)]
public sealed class ElevationAnalysisValidationTests
{
    [UnitTest]
    [Operation(Operations.ErrorHandling)]
    public async Task ValidateDatasetAsync_PublicationWithoutLayerIndexOrStorageBinding_ReturnsNotFoundInsteadOfLayerZero()
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IMetadataV2GraphProvider>(new TestMetadataV2GraphProvider(CreateUnboundElevationGraph()))
            .AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>()
            .BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };

        var result = await ElevationAnalysisValidation.ValidateDatasetAsync(context, "dem-unbound", CancellationToken.None);

        result.IsValid.Should().BeFalse();
        result.ErrorResult.Should().NotBeNull();
        await result.ErrorResult!.ExecuteAsync(context);
        context.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    private static MetadataV2Graph CreateUnboundElevationGraph()
    {
        var active = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active };
        return new MetadataV2Graph
        {
            Resources =
            [
                new MetadataV2Object
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "dem-unbound", Name = "dem-unbound" },
                    Status = active,
                    Type = MetadataV2ObjectType.RasterDataset,
                    AccessPolicy = new AccessPolicy { AllowAnonymous = true }
                }
            ],
            Services =
            [
                new MetadataV2Service
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "svc-elev", Name = "elev" },
                    Status = active,
                    Protocols = [ServiceProtocols.Elevation]
                }
            ],
            Publications =
            [
                new MetadataV2Layer
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "pub-unbound", Name = "dem-unbound" },
                    Status = active,
                    ServiceId = "svc-elev",
                    ResourceId = "dem-unbound",
                    LayerIndex = null
                }
            ]
        };
    }
}
