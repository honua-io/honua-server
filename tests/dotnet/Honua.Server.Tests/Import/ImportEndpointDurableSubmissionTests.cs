// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Helpers;

namespace Honua.Server.Tests.Import;

[Collection(WebAppFixtureJobSubstrateExtensions.JobSubstrateCollection)]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Import)]
public sealed class ImportEndpointDurableSubmissionTests(DatabaseFixtureAdapter database)
{
    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/geoservices/start")]
    public async Task StartGeoservices_WhenAccepted_PersistsDurableExecutionJob()
    {
        await AssertDurableSubmissionAsync("geoservices", new
        {
            ServiceUrl = "https://example.com/arcgis/rest/services/Imported/FeatureServer",
            LayerId = 0,
            TableName = $"durable_source_{Guid.NewGuid():N}"[..28]
        }, "GeoservicesImport");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/geoserver/start")]
    public async Task StartGeoServer_WhenAccepted_PersistsDurableExecutionJob()
    {
        await AssertDurableSubmissionAsync("geoserver", new
        {
            GeoServerRestUrl = "https://example.com/geoserver/rest",
            DryRun = true
        }, "GeoServerImport");
    }

    private async Task AssertDurableSubmissionAsync(string source, object request, string expectedKind)
    {
        await database.DataSource.ClearJobSubstrateAsync();
        var fixture = new WebAppFixture()
            .WithProductionJobSubstrate()
            .WithTestLicense(HonuaEdition.Enterprise)
            .ConfigureServices(ImportWorkerTestServices.RemoveHostedImportWorkers);
        await fixture.InitializeAsync();
        try
        {
            var jobStore = fixture.GetService<IExecutionJobStore>();
            jobStore.Should().BeOfType<PostgresExecutionJobStore>();

            using var response = await fixture.Client.PostAsJsonAsync($"/api/v1/admin/import/{source}/start", request);
            var body = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.Accepted, body);
            using var document = JsonDocument.Parse(body);
            var jobId = document.RootElement.GetProperty("jobId").GetString();
            jobId.Should().NotBeNullOrWhiteSpace();
            var job = await jobStore.GetAsync(jobId!);
            job.Should().NotBeNull("accepted imports must survive loss of the submitting replica");
            job!.Spec.Kind.ToString().Should().Be(expectedKind);
        }
        finally
        {
            await fixture.DisposeAsync();
            await database.DataSource.ClearJobSubstrateAsync();
        }
    }
}
