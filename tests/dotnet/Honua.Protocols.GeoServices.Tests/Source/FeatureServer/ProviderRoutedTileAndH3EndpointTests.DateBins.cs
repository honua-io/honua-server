// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

public sealed partial class ProviderRoutedTileAndH3EndpointTests
{
    [IntegrationTest]
    [Protocol(TestProtocols.FeatureServer)]
    [Operation(Operations.QueryDateBins)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryDateBins")]
    public async Task QueryDateBins_SourceBackedLayer_UsesBindingScopedFeatureReader()
    {
        _fixture.SecondaryReader.ClearReceivedCalls();
        _fixture.FallbackReader.ClearReceivedCalls();
        var boundary = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        _fixture.SecondaryReader.QueryDateBinsAsync(
                Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<DateBinDefinition>(), Arg.Any<CancellationToken>())
            .Returns(ImmutableArray.Create<IReadOnlyDictionary<string, object?>>(
                new Dictionary<string, object?> { ["boundary"] = boundary, ["count"] = 3L }));
        var bin = Uri.EscapeDataString(JsonSerializer.Serialize(new { calendarBin = new { unit = "month" } }));

        var response = await _fixture.App.Client.GetAsync(
            $"/rest/services/routed/FeatureServer/0/queryDateBins?binField=observed_at&bin={bin}&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var features = document.RootElement.GetProperty("features");
        features.GetArrayLength().Should().Be(1);
        features[0].GetProperty("attributes").GetProperty("count").GetInt64().Should().Be(3L);
        await _fixture.SecondaryReader.Received().QueryDateBinsAsync(
            41, Arg.Any<FeatureQuery>(), Arg.Is<DateBinDefinition>(dateBin => dateBin.BinField == "observed_at"), Arg.Any<CancellationToken>());
        await _fixture.FallbackReader.DidNotReceive().QueryDateBinsAsync(
            Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<DateBinDefinition>(), Arg.Any<CancellationToken>());
    }
}
