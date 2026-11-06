// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Exceptions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Protocols.OData.Services;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using NSubstitute;

namespace Honua.Protocols.OData.UnitTests.Services;

public sealed partial class ODataCrudOwnerEditPolicyTests
{
    [UnitTest]
    public async Task UpdateAndDelete_UseAuthorizedReaderForOwnerAdmission()
    {
        var fallback = Substitute.For<IFeatureReader>();
        fallback.QueryAsync(default, default!, default).ReturnsForAnyArgs(
            Task.FromException<QueryResult<Feature>>(new ResourceAccessDeniedException()));
        var reader = new OwnerReader();
        var writer = new CapturingWriter();
        var service = CreateService(fallback, writer, Principal("alice", admin: false));

        var update = await service.UpdateFeatureAsync(new ODataUpdateRequest(RouteLayerId, 42,
            Payload("after"), "https://example.test", null, null, StorageLayerId, false, Resource(Policy), reader));
        var delete = await service.DeleteFeatureAsync(new ODataDeleteRequest(RouteLayerId, 42,
            null, null, StorageLayerId, Resource(Policy), reader));

        update.IsSuccess.Should().BeTrue();
        delete.IsSuccess.Should().BeTrue();
        writer.ApplyCount.Should().Be(2);
        reader.QueryCount.Should().Be(2);
        await fallback.DidNotReceiveWithAnyArgs().QueryAsync(default, default!, default);
    }

    [UnitTest]
    public async Task GetUpdateDelete_NumericPolicyReadDenied_ReturnGenericForbidden()
    {
        var reader = DeniedReader();
        var writer = new CapturingWriter();
        var service = CreateService(reader, writer, Principal("alice", admin: false));
        var resource = Resource(null);

        var get = await service.GetFeatureAsync(RouteLayerId, 42, "https://example.test", reader, StorageLayerId, resource);
        var update = await service.UpdateFeatureAsync(new ODataUpdateRequest(RouteLayerId, 42, Payload("after"), "https://example.test",
            null, null, StorageLayerId, false, resource, reader));
        var delete = await service.DeleteFeatureAsync(new ODataDeleteRequest(RouteLayerId, 42, null, null, StorageLayerId, resource, reader));

        new[] { get.StatusCode, update.StatusCode, delete.StatusCode }.Should().OnlyContain(status => status == StatusCodes.Status403Forbidden);
        new[] { get.ErrorMessage, update.ErrorMessage, delete.ErrorMessage }.Should()
            .OnlyContain(message => message == "The operation is not permitted for this resource.");
        writer.ApplyCount.Should().Be(0);
    }

    [UnitTest]
    public async Task Create_NumericPolicyWriterDenied_ReturnsGenericForbidden()
    {
        var writer = Substitute.For<IFeatureWriter>();
        writer.ApplyEditsAsync(default, default, default).ReturnsForAnyArgs(
            Task.FromException<FeatureEditResult>(new ResourceAccessDeniedException()));
        var reader = new OwnerReader();
        var service = CreateService(reader, writer, Principal("alice", admin: false));

        var result = await service.CreateFeatureAsync(new ODataCreateRequest(RouteLayerId, Payload("after"), "https://example.test", StorageLayerId, Resource(null), reader));

        result.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        result.ErrorMessage.Should().Be("The operation is not permitted for this resource.");
    }

    [UnitTest]
    public async Task Create_CommittedWriteWithDeniedReadback_ReturnsIdentityAcknowledgement()
    {
        var writer = new CapturingWriter();
        var reader = DeniedReader();
        var service = CreateService(reader, writer, Principal("alice", admin: false));

        var result = await service.CreateFeatureAsync(new ODataCreateRequest(RouteLayerId, Payload("after"), "https://example.test", StorageLayerId, Resource(null), reader));

        result.StatusCode.Should().Be(StatusCodes.Status201Created);
        result.IsSuccess.Should().BeTrue();
        result.Data.Should().ContainKey("ObjectId").WhoseValue.Should().Be(77L);
        result.Data.Should().NotContainKey("name");
        result.ETag.Should().BeNull();
        writer.ApplyCount.Should().Be(1);
    }

    private static IFeatureReader DeniedReader()
    {
        var reader = Substitute.For<IFeatureReader>();
        reader.GetAsync(default, default, default).ReturnsForAnyArgs(
            Task.FromException<Feature?>(new ResourceAccessDeniedException()));
        return reader;
    }
}
