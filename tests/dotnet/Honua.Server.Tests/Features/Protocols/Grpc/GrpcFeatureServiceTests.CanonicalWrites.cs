// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Grpc.Core;
using Honua.Core.Configuration;
using Honua.Core.Features.Authorization;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Events.Outbox;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Security;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Server.Features.Protocols.Grpc;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Events;
using Honua.Infrastructure.Services;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using NSubstitute;
using Proto = Geospatial.V1;

namespace Honua.Server.Tests.Features.Protocols.Grpc;

public sealed partial class GrpcFeatureServiceTests
{
    private const string CanonicalServiceName = "canonical-primary";
    private const string SecondaryServiceName = "canonical-secondary";
    private const int CanonicalStorageLayerId = 11;

    public static TheoryData<string[]> CanonicalAndSecondaryWriterRoles() => new()
    {
        new[] { $"data-editor:{SecondaryServiceName}", $"data-editor:{CanonicalServiceName}" },
        new[] { "admin" },
    };

    [UnitTest]
    [Endpoint("POST /grpc/geospatial.v1.FeatureService/ApplyEdits")]
    [Operation(Operations.ApplyEdits)]
    public async Task ApplyEdits_SecondaryPublicationWithoutCanonicalServiceGrant_IsPermissionDenied()
    {
        using var receipts = new GrpcApplyEditsReceipts();
        var sut = CreateSharedResourceSut(receipts);
        using var context = CreateCallContext(CreateAuthenticatedUser($"data-editor:{SecondaryServiceName}"));

        var act = async () => await sut.ApplyEdits(CreateSecondaryInsertRequest(), context);

        var exception = await act.Should().ThrowAsync<RpcException>();
        exception.Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
        AssertNoReadWriteOrOutbox();
    }

    [UnitTheory]
    [MemberData(nameof(CanonicalAndSecondaryWriterRoles))]
    [Endpoint("POST /grpc/geospatial.v1.FeatureService/ApplyEdits")]
    [Operation(Operations.ApplyEdits)]
    public async Task ApplyEdits_SecondaryPublicationWithCanonicalServiceGrant_Writes(string[] roles)
    {
        using var receipts = new GrpcApplyEditsReceipts();
        var sut = CreateSharedResourceSut(receipts);
        using var context = CreateCallContext(CreateAuthenticatedUser(roles));
        _featureWriter
            .ApplyEditsAsync(default, default, default)
            .ReturnsForAnyArgs(Task.FromResult(FeatureEditResult.Success(createdCount: 1, updatedCount: 0, deletedCount: 0)));

        _ = await sut.ApplyEdits(CreateSecondaryInsertRequest(), context);

        var write = _featureWriter.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IFeatureWriter.ApplyEditsAsync))
            .Should().ContainSingle().Subject;
        write.GetArguments()[0].Should().Be(CanonicalStorageLayerId);
    }

    private static Proto.ApplyEditsRequest CreateSecondaryInsertRequest()
    {
        var request = CreateApplyEditsRequest(editKinds: 1);
        request.ServiceId = SecondaryServiceName;
        return request;
    }

    private HonuaFeatureService CreateSharedResourceSut(GrpcApplyEditsReceipts receipts)
    {
        _graphProvider.SetGraph(new TestMetadataV2GraphBuilder()
            .AddResource("resource.shared", "shared", fields: _testResource.SchemaFields)
            .AddStorageBinding("storage.shared", "resource.shared", "public.shared", storageLayerId: CanonicalStorageLayerId)
            .AddService(CanonicalServiceName, CanonicalServiceName, protocols: [GrpcProtocolName])
            .AddService(SecondaryServiceName, SecondaryServiceName, protocols: [GrpcProtocolName])
            .AddPublication(
                "pub.shared.primary",
                CanonicalServiceName,
                "resource.shared",
                layerIndex: 0,
                storageBindingId: "storage.shared",
                isPrimary: true)
            .AddPublication(
                "pub.shared.secondary",
                SecondaryServiceName,
                "resource.shared",
                layerIndex: 0,
                storageBindingId: "storage.shared")
            .Build());

        return new HonuaFeatureService(
            new ResourceValidator(_graphProvider), _featureReader, _featureWriter, _streamingStore,
            new CommonQueryValidator(Options.Create(new LimitsOptions())),
            new SpatialReferenceResolver(_crsDetectionService, _crsRegistry),
            new FeatureMutationEventService(
                _featureChangeEventPublisher,
                outboxCapabilityProvider: _outboxCapabilityProvider),
            Options.Create(new LimitsOptions()),
            Options.Create(new GrpcOptions()),
            NullLogger<HonuaFeatureService>.Instance,
            receipts);
    }

}
