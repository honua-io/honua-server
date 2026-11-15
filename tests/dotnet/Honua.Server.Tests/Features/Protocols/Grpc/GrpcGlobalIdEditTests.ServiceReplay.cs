// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Grpc.Core;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation;
using Honua.Server.Features.Protocols.Grpc;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Proto = Geospatial.V1;

namespace Honua.Server.Tests.Features.Protocols.Grpc;

public sealed partial class GrpcGlobalIdEditTests
{
    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Endpoint("POST /geospatial.v1.FeatureService/ApplyServiceEdits")]
    public async Task ServiceReplay_AfterGlobalIdsBecomeMasked_FiltersEveryLayer(bool durable)
    {
        var second = GrpcFeatureServiceTests.CreateTriple(GrpcFeatureServiceTests.CreateService("test"), GlobalIdResource());
        second = second with
        {
            Publication = second.Publication with
            {
                Identifier = new MetadataV2PublicationIdentifier { Value = "1", IsNumeric = true }
            },
            StorageLayerId = 1
        };
        _resourceValidator.ValidateServiceLayerV2Async("test", 1, Arg.Any<CancellationToken>())
            .Returns(ResourceValidationResult.Success(second));
        var request = new Proto.ApplyServiceEditsRequest { ServiceId = "test", IdempotencyKey = "service-replay" };
        var original = new Proto.ApplyServiceEditsResponse();
        foreach (var layerId in new[] { 1, 0 })
        {
            request.Edits.Add(new Proto.ServiceLayerEdits { LayerId = layerId, Adds = { new Proto.Feature() } });
            original.Results.Add(new Proto.ServiceLayerEditResults
            {
                LayerId = layerId,
                Results = new Proto.ApplyEditsResponse
                {
                    AddResults = { new Proto.EditResult { ObjectId = 7 + layerId, Success = true, GlobalId = GlobalId } }
                }
            });
        }
        var fingerprint = GrpcServiceEditsReceipts.Fingerprint(request);
        var store = Substitute.For<IFeatureEditIdempotencyReceipts>();
        if (durable)
        {
#pragma warning disable CA2012 // NSubstitute setup for a ValueTask-returning member.
            store.TryReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(_ => new ValueTask<ReadOnlyMemory<byte>?>(GrpcServiceEditsReceipts.EncodeReceipt(original, fingerprint)));
#pragma warning restore CA2012
        }
        using var receipts = new GrpcServiceEditsReceipts(durable ? store : null);
        var masks = Substitute.For<IFieldMaskSource>();
        masks.ResolveAsync(Arg.Any<MetadataV2Resource>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ImmutableArray.Create("globalid")));
        var context = GrpcFeatureServiceTests.CreateCallContext(
            GrpcFeatureServiceTests.CreateAuthenticatedUser("editor"), tenantId: null,
            [new PermissionGrant { Service = "test", Layer = "*", Operation = "insert" }],
            services => { services.AddSingleton(masks); services.AddSingleton(receipts); });
        _contexts.Add(context);
        if (!durable)
        {
            var scope = GrpcServiceEditsReceipts.CreateScope(context.GetHttpContext(), new("test", -1, request.IdempotencyKey));
            using var entry = await receipts.EnterAsync(scope!, CancellationToken.None);
            entry.Complete(original, fingerprint);
        }

        var replayed = await _sut.ApplyServiceEdits(request, context);

        replayed.Results.Select(result => result.LayerId).Should().Equal(1, 0);
        replayed.Results.SelectMany(result => result.Results.AddResults).Select(result => result.ObjectId).Should().Equal(8, 7);
        replayed.Results.SelectMany(result => result.Results.AddResults).Should().OnlyContain(result => result.GlobalId.Length == 0);
        original.Results.SelectMany(result => result.Results.AddResults).Should().OnlyContain(result => result.GlobalId == GlobalId);
        AssertNothingRead();
    }
}
