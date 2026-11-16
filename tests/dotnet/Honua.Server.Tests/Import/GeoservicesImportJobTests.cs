// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Honua.TestKit.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace Honua.Server.Tests.Import;

[Collection(WebAppFixtureJobSubstrateExtensions.JobSubstrateCollection)]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Import)]
public sealed class GeoservicesImportJobTests(DatabaseFixtureAdapter database)
{
    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/geoservices/start")]
    public async Task Import_AcceptedOnOneNode_RunsOnAnotherAfterTheFirstStops()
    {
        await database.DataSource.ClearJobSubstrateAsync();
        var tableName = $"replica_import_{Guid.NewGuid():N}"[..28];
        const string sourceUrl = "https://example.com/arcgis/rest/services/Replica/FeatureServer";
        var observed = new TaskCompletionSource<GeoservicesImportRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var importService = ObserveImport(observed);

        WebAppFixture? producer = CreateHost().ConfigureServices(RemoveExecutionWorker);
        WebAppFixture? consumer = null;
        try
        {
            await producer.InitializeAsync();
            var jobId = await SubmitAsync(producer, sourceUrl, tableName);

            var queued = await producer.GetService<IExecutionJobStore>().GetAsync(jobId);
            queued.Should().NotBeNull("accepted imports must persist their request before acknowledging submission");
            queued!.Status.Should().Be(ExecutionJobStatus.Queued);
            queued.Spec.Kind.ToString().Should().Be("GeoservicesImport");
            await producer.DisposeAsync();
            producer = null;

            consumer = CreateHost().ReplaceService<IGeoservicesImportService>(importService);
            await consumer.InitializeAsync();
            var recovered = await observed.Task.WaitAsync(TimeSpan.FromSeconds(30));
            recovered.JobId.Should().Be(jobId);
            recovered.ServiceUrl.Should().Be(sourceUrl);
            recovered.LayerId.Should().Be(3);
            recovered.TableName.Should().Be(tableName);
        }
        finally
        {
            await CleanupAsync(consumer, producer);
        }
    }

    private async Task CleanupAsync(WebAppFixture? consumer, WebAppFixture? producer)
    {
        if (consumer != null)
        {
            await consumer.DisposeAsync();
        }

        if (producer != null)
        {
            await producer.DisposeAsync();
        }

        await database.DataSource.ClearJobSubstrateAsync();
    }

    private static IGeoservicesImportService ObserveImport(
        TaskCompletionSource<GeoservicesImportRequest> observed)
    {
        var importService = Substitute.For<IGeoservicesImportService>();
        importService.ImportLayerAsync(Arg.Any<GeoservicesImportRequest>(),
                Arg.Any<IProgress<GeoservicesImportProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.ArgAt<GeoservicesImportRequest>(0);
                observed.TrySetResult(request);
                return GeoservicesImportResult.CreateFailure(request.TableName, request.ServiceUrl,
                    request.LayerId, "Test source deliberately has no data.");
            });

        return importService;
    }

    private static async Task<string> SubmitAsync(WebAppFixture host, string sourceUrl, string tableName)
    {
        using var response = await host.Client.PostAsJsonAsync("/api/v1/admin/import/geoservices/start", new
        {
            ServiceUrl = sourceUrl,
            LayerId = 3,
            TableName = tableName,
            AutoPublish = false
        });
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Accepted, body);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("jobId").GetString()!;
    }

    private static WebAppFixture CreateHost() => new WebAppFixture()
        .WithProductionJobSubstrate()
        .WithTestLicense(HonuaEdition.Enterprise)
        .ConfigureServices(ImportWorkerTestServices.RemoveHostedImportWorkers);

    private static void RemoveExecutionWorker(IServiceCollection services)
    {
        foreach (var descriptor in services.Where(descriptor =>
                     descriptor.ServiceType == typeof(IHostedService) &&
                     descriptor.ImplementationType == typeof(JobExecutionService)).ToArray())
        {
            services.Remove(descriptor);
        }
    }
}
