// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

// Layer validateSQL checks column names against the layer for every sqlType form, not only
// the clause-type forms (#9107).
[Collection("Database")]
[Protocol(TestProtocols.FeatureServer)]
public sealed class FeatureServerMaintenanceValidateSqlTests : IClassFixture<FeatureServerMaintenanceTests.ReadOnlyProHost>
{
    private readonly HttpClient _client;

    public FeatureServerMaintenanceValidateSqlTests(FeatureServerMaintenanceTests.ReadOnlyProHost host) => _client = host.Fixture.Client;

    [IntegrationTheory]
    [InlineData("")]
    [InlineData("&sqlType=standard")]
    [InlineData("&sqlType=native")]
    [Operation(Operations.ValidateSql)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/validateSQL")]
    public async Task ValidateSql_UnknownColumn_IsInvalidForEverySqlTypeForm(string sqlTypeQuery)
    {
        var root = await ValidateAsync("no_such_column = 1", sqlTypeQuery);

        root.GetProperty("isValidSQL").GetBoolean().Should().BeFalse();
        root.GetProperty("validationError").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [IntegrationTheory]
    [InlineData("")]
    [InlineData("&sqlType=standard")]
    [Operation(Operations.ValidateSql)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/validateSQL")]
    public async Task ValidateSql_KnownColumn_IsValidWithoutClauseType(string sqlTypeQuery)
    {
        var root = await ValidateAsync("objectid > 0", sqlTypeQuery);

        root.GetProperty("isValidSQL").GetBoolean().Should().BeTrue();
    }

    private async Task<JsonElement> ValidateAsync(string sql, string sqlTypeQuery)
    {
        var response = await _client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/validateSQL?sql={Uri.EscapeDataString(sql)}{sqlTypeQuery}&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }
}
