// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Security;

[Collection("Database")]
[Protocol(TestProtocols.OgcApiFeatures)]
[Operation(Operations.Security)]
public sealed class Cql2LiteralInputValidationTests(
    InputValidationIntegrationTests.ProductionApplication application)
    : IClassFixture<InputValidationIntegrationTests.ProductionApplication>
{
    [IntegrationTheory]
    [InlineData("audit -- note")]
    [InlineData("O'Brien -- note")]
    [InlineData("audit /* note */")]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task Items_EquivalentTextAndJsonLiterals_ReturnSameFeatures(string literal)
    {
        var text = $"name = '{literal.Replace("'", "''", StringComparison.Ordinal)}'";
        var json = JsonSerializer.Serialize(new { op = "=", args = new object[] { new { property = "name" }, literal } });
        foreach (var (language, filter) in new[] { ("cql2-text", text), ("cql2-json", json) })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"/ogc/features/collections/0/items?filter-lang={language}&filter={Uri.EscapeDataString(filter)}");
            request.Headers.Add("X-API-Key", "Valid-Test-Key123!");
            using var response = await application.Fixture.Client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.OK, body);
            using var document = JsonDocument.Parse(body);
            document.RootElement.GetProperty("features").GetArrayLength().Should().Be(0);
        }
    }
}
