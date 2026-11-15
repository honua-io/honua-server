// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Grpc.Core;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.TestKit.Attributes;
using NSubstitute;
using Proto = Geospatial.V1;

namespace Honua.Server.Tests.Features.Protocols.Grpc;

public sealed partial class GrpcGlobalIdEditTests
{
    [UnitTest]
    [Endpoint("POST /geospatial.v1.FeatureService/ApplyEdits")]
    public async Task AddWithExistingGlobalId_IsRejectedWithoutWriting()
    {
        var request = new Proto.ApplyEditsRequest { ServiceId = "test", LayerId = 0 };
        request.Adds.Add(new Proto.Feature { GlobalId = GlobalId });

        var act = async () => await _sut.ApplyEdits(request, CallContext("insert"));

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        AssertNothingWritten();
    }

    [UnitTest]
    [Endpoint("POST /geospatial.v1.FeatureService/ApplyEdits")]
    public async Task AddWithoutGlobalId_GetsAServerAssignedIdentity()
    {
        _featureReader.QueryAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(QueryResult<Feature>.Empty());
        _featureWriter.ApplyEditsAsync(default, default, default).ReturnsForAnyArgs(Task.FromResult(
            FeatureEditResult.Success(1, 0, 0, createResults: [EditOperationResult.Success(7)])));
        var request = new Proto.ApplyEditsRequest { ServiceId = "test", LayerId = 0 };
        request.Adds.Add(new Proto.Feature());

        var response = await _sut.ApplyEdits(request, CallContext("insert"));

        var written = (FeatureEditBatch)_featureWriter.ReceivedCalls()
            .Single(call => call.GetMethodInfo().Name == nameof(IFeatureWriter.ApplyEditsAsync))
            .GetArguments()[1]!;
        var assigned = written.Creates.Single().Attributes["GlobalID"].Should().BeOfType<string>().Subject;
        Guid.TryParseExact(assigned, "D", out _).Should().BeTrue();
        response.AddResults.Single().GlobalId.Should().Be(assigned);
    }

    [UnitTest]
    [Endpoint("POST /geospatial.v1.FeatureService/ApplyEdits")]
    public async Task AddWithNonStringRawGlobalId_IsRejectedWithoutWriting()
    {
        var request = new Proto.ApplyEditsRequest { ServiceId = "test", LayerId = 0 };
        var add = new Proto.Feature();
        add.Attributes["GlobalID"] = new Proto.AttributeValue { Int32Value = 123 };
        request.Adds.Add(add);

        var act = async () => await _sut.ApplyEdits(request, CallContext("insert"));

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        AssertNothingWritten();
    }

    [UnitTest]
    [Endpoint("POST /geospatial.v1.FeatureService/ApplyEdits")]
    public async Task AddWithConflictingGlobalIds_IsRejectedWithoutWriting()
    {
        _featureReader.QueryAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(QueryResult<Feature>.Empty());
        var request = new Proto.ApplyEditsRequest { ServiceId = "test", LayerId = 0 };
        var add = new Proto.Feature { GlobalId = GlobalId };
        add.Attributes["GlobalID"] = new Proto.AttributeValue { StringValue = Guid.NewGuid().ToString("D") };
        request.Adds.Add(add);

        var act = async () => await _sut.ApplyEdits(request, CallContext("insert"));

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        AssertNothingRead();
        AssertNothingWritten();
    }

    [UnitTest]
    [Endpoint("POST /geospatial.v1.FeatureService/ApplyEdits")]
    public async Task AddsRepeatingAGlobalIdThroughRawAttributes_AreRejectedWithoutWriting()
    {
        var request = new Proto.ApplyEditsRequest { ServiceId = "test", LayerId = 0 };
        var raw = new Proto.Feature();
        raw.Attributes["GlobalID"] = new Proto.AttributeValue { StringValue = "{" + GlobalId.ToUpperInvariant() + "}" };
        request.Adds.Add(raw);
        request.Adds.Add(new Proto.Feature { GlobalId = GlobalId });

        var act = async () => await _sut.ApplyEdits(request, CallContext("insert"));

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        AssertNothingWritten();
    }

}
