// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Grpc.Core;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Server.Features.Protocols.Grpc;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.DataProtection;

namespace Honua.Server.Tests.Features.Protocols.Grpc;

[Protocol(TestProtocols.Grpc)]
[Operation(Operations.Query)]
public sealed class GrpcRelatedPageTokenTests
{
    private readonly EphemeralDataProtectionProvider _protection = new();
    private static readonly DateTimeOffset _now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    [UnitTheory]
    [InlineData(null)]
    [InlineData(42L)]
    public void Continuation_RoundTripsEmptyOriginAndRelatedRowPositions(long? relatedId)
    {
        var position = new RelatedPageCursor(long.MaxValue, relatedId);
        var token = new GrpcRelatedPageTokens(_protection, "query-and-caller").Encode(position, _now);

        new GrpcRelatedPageTokens(_protection, "query-and-caller").Decode(token, _now)
            .Should().Be(position);
    }

    [UnitTest]
    public void Continuation_DifferentQueryOrCallerCannotReuseToken()
    {
        var token = new GrpcRelatedPageTokens(_protection, "caller-a").Encode(new(1, 2), _now);
        var action = () => new GrpcRelatedPageTokens(_protection, "caller-b").Decode(token, _now);

        action.Should().Throw<RpcException>().Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [UnitTheory]
    [InlineData(-1)]
    [InlineData(901)]
    public void Continuation_RejectsFutureOrExpiredToken(int seconds)
    {
        var token = new GrpcRelatedPageTokens(_protection, "query").Encode(new(1, null), _now);
        var action = () => new GrpcRelatedPageTokens(_protection, "query").Decode(token, _now.AddSeconds(seconds));

        action.Should().Throw<RpcException>().Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [UnitTheory]
    [InlineData("not-a-token")]
    [InlineData("")]
    public void Continuation_RejectsMalformedToken(string token)
    {
        var action = () => new GrpcRelatedPageTokens(_protection, "query").Decode(token, _now);

        action.Should().Throw<RpcException>().Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [UnitTest]
    public void Continuation_RejectsTamperedToken()
    {
        var token = new GrpcRelatedPageTokens(_protection, "query").Encode(new(1, 2), _now);
        token = (token[0] == 'A' ? "B" : "A") + token[1..];
        var action = () => new GrpcRelatedPageTokens(_protection, "query").Decode(token, _now);

        action.Should().Throw<RpcException>().Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }
}
