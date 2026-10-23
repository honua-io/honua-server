// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Infrastructure.Services;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests;

/// <summary>Budget refusals are complete protocol errors; a later bounded query still encodes successfully.</summary>
[Collection("Database.CoreEndpoints")]
[Protocol(ProtocolNames.FeatureServer, ProtocolNames.ODataV4)]
public sealed class GeoParquetBudgetEndpointTests
{
    [IntegrationTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    [Endpoint("GET /odata/Features({layerId})")]
    public async Task Query_ExceedsInputOrOutputBudget_ReturnsCompleteErrorAndCanEncodeNextRequest(bool odata, bool inputBudget)
    {
        var limits = new GeoParquetLimits
        {
            MaxEstimatedInputBytes = inputBudget ? 1 : 128 * 1024 * 1024,
            MaxResponseBytes = inputBudget ? 64 * 1024 * 1024 : 32
        };
        var fixture = new WebAppFixture().ConfigureServices(services =>
            services.PostConfigure<LimitsOptions>(options => options.GeoParquet = limits));
        if (odata)
        {
            fixture.UseSeed(Path.Join("tests", "seed", "odata.yaml"));
        }
        await fixture.InitializeAsync();
        try
        {
            var path = odata ? "/odata/Features(0)?$format=parquet&$top=2"
                : "/rest/services/test/FeatureServer/0/query?where=1%3D1&f=parquet&resultRecordCount=2";
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var response = await fixture.Client.GetAsync(path);
                response.StatusCode.Should().Be(odata ? HttpStatusCode.RequestEntityTooLarge : HttpStatusCode.OK);
                response.Content.Headers.ContentType!.MediaType.Should().Contain("json");
                var body = await response.Content.ReadAsStringAsync();
                body.Should().NotStartWith("PAR1", "the encoder must never send partial Parquet before a budget decision");
                using var error = JsonDocument.Parse(body);
                var envelope = error.RootElement.GetProperty("error");
                if (!odata)
                {
                    envelope.GetProperty("code").GetInt32().Should().Be(413);
                }
                body.Should().Contain(GeoParquetLimitExceededException.ClientMessage);
            }

            // The configured options object is shared with both adapters. Raise only the
            // budget and retry the same query to prove refusal left no broken writer state.
            limits.MaxEstimatedInputBytes = 128 * 1024 * 1024;
            limits.MaxResponseBytes = 64 * 1024 * 1024;
            using var accepted = await fixture.Client.GetAsync(path);
            accepted.StatusCode.Should().Be(HttpStatusCode.OK);
            accepted.Content.Headers.ContentType!.MediaType.Should().Be("application/vnd.apache.parquet");
            using var payload = new MemoryStream(await accepted.Content.ReadAsByteArrayAsync());
            using var reader = new ParquetSharp.Arrow.FileReader(payload);
            reader.NumRowGroups.Should().BeGreaterThan(0);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }
}
