// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Authentication;
using Honua.Protocols.Ogc.Api.Features.Services;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Protocols.OgcApi.UnitTests.Features;

public sealed partial class OgcFeaturesEditGateTests
{
    [UnitTest]
    public async Task ReplaceAndBatchUpdate_WithActiveFieldMask_PreserveMaskedColumnsAndValidateEffectiveSnapshot()
    {
        var masks = Substitute.For<IFieldMaskSource>();
        masks.ResolveAsync(Arg.Any<MetadataV2Object>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ImmutableArray.Create("size")));
        var reader = Substitute.For<IFeatureReader>();
        reader.QueryAsync(7, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(QueryResult<Feature>.Create(1, [Row(42, "red", "S")])));

        foreach (var request in CreateReplaceRequests(objectId: 42, color: "red"))
        {
            var writer = new CountingWriter();
            var handlers = CreateHandlers(writer, reader);
            var result = await handlers.Transaction.ExecuteEditAsync(
                CreateMaskedContext(masks), 7, Resource(), request, CancellationToken.None);

            result.IsSuccess.Should().BeTrue();
            writer.ApplyCount.Should().Be(1);
            var batch = writer.LastBatch!.Value;
            var written = batch.Updates.IsDefaultOrEmpty
                ? batch.Operations.Single().Feature!.Value
                : batch.Updates.Single();
            written.PreserveOmittedMaskedAttributes.Should().BeFalse();
            written.PreservedAttributeNames.Should().Contain("size");
        }
    }

    private static OgcFeaturesEditRequest[] CreateReplaceRequests(long objectId, string color)
    {
        var feature = Feature.Create(objectId, null, ImmutableDictionary<string, object?>.Empty.Add("color", color));
        return
        [
            new OgcFeaturesEditRequest
            {
                Operation = OgcFeaturesEditOperation.Replace,
                ObjectId = objectId,
                Feature = feature,
            },
            new OgcFeaturesEditRequest
            {
                Operation = OgcFeaturesEditOperation.Batch,
                BatchOperations =
                [
                    new OgcFeaturesBatchEditOperation
                    {
                        Operation = OgcFeaturesEditOperation.Replace,
                        ObjectId = objectId,
                        Feature = feature,
                    }
                ],
            },
        ];
    }

    private static DefaultHttpContext CreateMaskedContext(IFieldMaskSource masks)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOptions<RbacOptions>>(Options.Create(new RbacOptions()));
        services.AddSingleton(masks);
        return new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
    }
}
