// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using NSubstitute;

namespace Honua.Server.Tests.Import;

/// <summary>
/// Integration tests for OGC WFS import endpoints.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Import)]
public sealed class OgcWfsImportEndpointTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.Client;
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/ogc-wfs/start")]
    public async Task Import_UnownedTarget_Returns409AfterValidation()
    {
        var service = Substitute.For<IOgcWfsImportService>();
        service.ImportFeaturesAsync(Arg.Any<OgcWfsImportRequest>(), Arg.Any<CancellationToken>())
            .Returns(new OgcWfsImportResult
            {
                Success = false,
                SourceServiceUrl = "https://example.com/wfs",
                Manifest = new MigrationManifestArtifact
                {
                    SourceKind = "ogc-wfs",
                    Source = new MigrationSourceIdentity { DisplayName = "WFS", BaseUrl = "https://example.com/wfs", ServiceType = "WFS" },
                    Summary = new MigrationManifestSummary()
                },
                ParityEvidence = new MigrationParityEvidenceArtifact
                {
                    SourceKind = "ogc-wfs",
                    Source = new MigrationSourceIdentity { DisplayName = "WFS", BaseUrl = "https://example.com/wfs", ServiceType = "WFS" },
                    OverallState = "fail",
                    Summary = "Unowned target refused.",
                    CutoverReadiness = new MigrationCutoverReadinessSummary { State = "fail" }
                },
                ErrorCode = ImportTableOwnershipException.Code,
                ErrorMessage = ImportTableOwnershipException.CreateSafeMessage("honua_data.roads")
            });
        var fixture = new WebAppFixture().ReplaceService<IOgcWfsImportService>(service);
        await fixture.InitializeAsync();
        try
        {
            using var invalid = await fixture.Client.PostAsJsonAsync("/api/v1/admin/import/ogc-wfs/start", new { });
            invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            await service.DidNotReceive().ImportFeaturesAsync(Arg.Any<OgcWfsImportRequest>(), Arg.Any<CancellationToken>());

            using var response = await fixture.Client.PostAsJsonAsync("/api/v1/admin/import/ogc-wfs/start", new { ServiceUrl = "https://example.com/wfs", ApplyMode = true, OverwriteExisting = true, TargetSchema = "honua_data" });
            var payload = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.Conflict, payload);
            payload.Should().Contain(ImportTableOwnershipException.Code).And.Contain("honua_data.roads");
            await service.Received(1).ImportFeaturesAsync(Arg.Any<OgcWfsImportRequest>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/ogc-wfs/start")]
    public async Task Start_WithMissingServiceUrl_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/admin/import/ogc-wfs/start", new
        {
            DryRun = true
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("serviceUrl is required.");
    }
}
