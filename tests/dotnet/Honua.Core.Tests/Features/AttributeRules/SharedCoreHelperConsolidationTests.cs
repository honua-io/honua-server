// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.AttributeRules;
using Honua.Core.Features.FileImport.Services;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.Raster;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.AttributeRules;

public sealed class SharedCoreHelperConsolidationTests
{
    [UnitTest]
    public void AttributeRuleExpression_TryToDouble_ParsesNumericPrimitivesAndStrings()
    {
        AttributeRuleExpression.TryToDouble(42, out var intVal).Should().BeTrue();
        intVal.Should().Be(42d);

        AttributeRuleExpression.TryToDouble(3.5m, out var decVal).Should().BeTrue();
        decVal.Should().Be(3.5d);

        AttributeRuleExpression.TryToDouble("12.25", out var strVal).Should().BeTrue();
        strVal.Should().Be(12.25d);

        AttributeRuleExpression.TryToDouble("not-a-number", out _).Should().BeFalse();
        AttributeRuleExpression.TryToDouble(null, out _).Should().BeFalse();
    }

    [UnitTest]
    public void SceneAnalysisService_InterpolateGeodesic_ComputesEndpointsAndMidpoint()
    {
        var start = SceneAnalysisService.InterpolateGeodesic(0, 0, 10, 0, 0.0);
        start.Should().Be((0, 0));

        var end = SceneAnalysisService.InterpolateGeodesic(0, 0, 10, 0, 1.0);
        end.Should().Be((10, 0));

        var mid = SceneAnalysisService.InterpolateGeodesic(0, 0, 10, 0, 0.5);
        mid.Longitude.Should().BeApproximately(5.0, 1e-6);
        mid.Latitude.Should().BeApproximately(0.0, 1e-6);
    }

    [UnitTest]
    public void InMemoryImportJobService_TryDeleteTempFile_RemovesExistingFileAndIgnoresNull()
    {
        var tempFile = Path.Join(Path.GetTempPath(), $"honua-import-temp-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(tempFile, "payload");

        InMemoryImportJobService.TryDeleteTempFile(tempFile);
        File.Exists(tempFile).Should().BeFalse();

        var act = () => InMemoryImportJobService.TryDeleteTempFile(null);
        act.Should().NotThrow();
    }

    [UnitTest]
    public async Task OgcApiFeaturesImportService_GetJsonDocumentAsync_PreservesAcceptHeaderOrder()
    {
        string[]? observedAccept = null;
        using var handler = new StubJsonHandler(req =>
        {
            observedAccept = req.Headers.Accept.Select(static h => h.MediaType ?? string.Empty).ToArray();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json")
            };
        });
        using var httpClient = new HttpClient(handler);

        var previous = MigrationLinkedUrlPolicy.Current;
        MigrationLinkedUrlPolicy.Current = new MigrationLinkedUrlPolicy(
            AllowUnsafeLocalUrls: true,
            AllowedHostSuffixes: null);
        try
        {
            using var doc = await OgcApiFeaturesImportService.GetJsonDocumentAsync(
                httpClient,
                new OgcApiFeaturesImportService.JsonDocumentFetchRequest
                {
                    Uri = new Uri("https://example.test/ogc/collections"),
                    PrimaryAccept = "application/json",
                    SecondaryAccept = "application/geo+json"
                },
                CancellationToken.None);

            doc.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
            observedAccept.Should().Equal("application/json", "application/geo+json");
        }
        finally
        {
            MigrationLinkedUrlPolicy.Current = previous;
        }
    }

    private sealed class StubJsonHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
