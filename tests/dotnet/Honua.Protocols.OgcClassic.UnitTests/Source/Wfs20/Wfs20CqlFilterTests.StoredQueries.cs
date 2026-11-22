// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Protocols.Ogc.Classic.Wfs20.Services;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wfs20;

public sealed partial class Wfs20CqlFilterTests
{
    [UnitTest]
    [Operation(Operations.Query)]
    [InterfaceOperation(TestProtocols.Wfs20, "GetFeature")]
    [Endpoint("GET /wfs")]
    public async Task StoredQueryGetFeatureById_ValidCqlFilter_PreservesIdAndTranslatedPredicate()
    {
        FeatureQuery? capturedQuery = null;
        var (handler, _) = CreateHandler(query => capturedQuery = query);

        var (status, body) = await ExecuteStoredQueryAsync(handler, "population > 100");

        status.Should().Be(StatusCodes.Status200OK, body);
        body.Should().Contain("Honolulu");
        capturedQuery.Should().NotBeNull();
        capturedQuery!.Value.SqlFilter!.Sql.Should().Be("population > 100");
        capturedQuery.Value.ObjectIds!.Value.Should().Equal(1L);
        WfsCqlFilterHelper.Current.Should().BeNull();
    }

    [UnitTheory]
    [InlineData("population >>>")]
    [InlineData("secret_code = 'abc'")]
    [InlineData("population > 100;population < 200")]
    [Operation(Operations.Query)]
    [InterfaceOperation(TestProtocols.Wfs20, "GetFeature")]
    [Endpoint("GET /wfs")]
    public async Task StoredQueryGetFeatureById_InvalidCqlFilter_RejectsBeforeReading(string cqlFilter)
    {
        FeatureQuery? capturedQuery = null;
        var (handler, _) = CreateHandler(query => capturedQuery = query);

        var (status, body) = await ExecuteStoredQueryAsync(handler, cqlFilter);

        status.Should().Be(StatusCodes.Status400BadRequest, body);
        body.Should().Contain("InvalidParameterValue").And.Contain("cql_filter");
        capturedQuery.Should().BeNull();
        WfsCqlFilterHelper.Current.Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [InterfaceOperation(TestProtocols.Wfs20, "GetFeature")]
    [Endpoint("GET /wfs")]
    public async Task StoredQueryGetFeatureById_EmptyCqlFilter_PreservesLegacyQuery()
    {
        FeatureQuery? capturedQuery = null;
        var (handler, _) = CreateHandler(query => capturedQuery = query);

        var (status, body) = await ExecuteStoredQueryAsync(handler, " ");

        status.Should().Be(StatusCodes.Status200OK, body);
        capturedQuery.Should().NotBeNull();
        capturedQuery!.Value.SqlFilter.Should().BeNull();
        capturedQuery.Value.ObjectIds!.Value.Should().Equal(1L);
    }

    private static async Task<(int Status, string Body)> ExecuteStoredQueryAsync(
        Wfs20Handler handler,
        string cqlFilter)
    {
        await using var services = CreateWfsHttpServices();
        var context = CreateWfsHttpContext(services, cqlFilter);
        var result = await handler.HandleStoredQueryGetFeatureAsync(
            context, "urn:ogc:def:query:OGC-WFS::GetFeatureById", "cities.1", null, null, null, CancellationToken.None);
        return await ExecuteResultAsync(context, result);
    }
}
