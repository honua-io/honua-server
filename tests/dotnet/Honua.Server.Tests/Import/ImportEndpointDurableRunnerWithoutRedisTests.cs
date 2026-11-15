// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Server.Features.Import;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Import;

[Collection(WebAppFixtureJobSubstrateExtensions.JobSubstrateCollection)]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Import)]
public sealed class ImportEndpointDurableRunnerWithoutRedisTests(DatabaseFixtureAdapter database)
{
    private const int FeatureCount = 25;
    private const string RedisConnectionStringName = "redis";
    private const string RedisConnectionStringKey = "ConnectionStrings:" + RedisConnectionStringName;
    private const string AspireRedisConnectionStringKey = "Aspire:StackExchange:Redis:ConnectionString";

    private static readonly TimeSpan _importDeadline = TimeSpan.FromSeconds(90);
    private static readonly JsonSerializerOptions _progressJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/upload")]
    [Endpoint("GET /api/v1/admin/import/jobs/{jobId}")]
    public async Task UploadImport_ForcedBackgroundOnHostWithoutRedis_SucceedsOnDurableRunnerAndRowsAreReadable()
    {
        await database.DataSource.ClearJobSubstrateAsync();
        var fixture = new WebAppFixture()
            .WithProductionJobSubstrate()
            .ConfigureWebHost(WithoutRedisConnectionString)
            .ConfigureServices(KeepHostComposedImportJobService);
        await fixture.InitializeAsync();
        try
        {
            var configuration = fixture.GetService<IConfiguration>();
            configuration.GetConnectionString(RedisConnectionStringName).Should().BeNullOrEmpty();
            configuration[AspireRedisConnectionStringKey].Should().BeNullOrEmpty();
            fixture.Services.GetService<StackExchange.Redis.IConnectionMultiplexer>().Should().BeNull();

            fixture.GetService<IBackgroundImportRunnerInfo>()
                .Should().BeOfType<BackgroundImportRunnerDecision>()
                .Which.Mode.Should().Be(BackgroundImportRunnerMode.DurableLocal);
            fixture.GetService<IImportJobService>().Should().BeOfType<DurableImportJobService>();
            var jobStore = fixture.GetService<IExecutionJobStore>();
            jobStore.Should().BeOfType<PostgresExecutionJobStore>();

            var tableName = $"durable_no_redis_{Guid.NewGuid():N}"[..28];
            using var accepted = await fixture.Client.PostAsync("/api/v1/admin/import/upload", CreateForcedBackgroundUpload(tableName));
            var acceptedBody = await accepted.Content.ReadAsStringAsync();
            accepted.StatusCode.Should().Be(HttpStatusCode.Accepted, acceptedBody);
            using var acceptedDocument = JsonDocument.Parse(acceptedBody);
            var jobId = acceptedDocument.RootElement.GetProperty("jobId").GetString();
            jobId.Should().NotBeNullOrWhiteSpace();

            var progress = await WaitForTerminalProgressAsync(fixture.Client, jobId!);
            progress.Status.Should().Be(ImportStatus.Completed, progress.ErrorMessage);
            progress.FeaturesProcessed.Should().Be(FeatureCount);

            var job = await jobStore.GetAsync(jobId!);
            job.Should().NotBeNull("the durable runner records every background import in the job store");
            job!.Spec.Kind.Should().Be(ExecutionJobKind.FileImport);
            job.Status.Should().Be(ExecutionJobStatus.Succeeded, job.ErrorMessage);

            (await DurableImportTestData.CountImportedRowsAsync(fixture, tableName)).Should().Be(FeatureCount);
        }
        finally
        {
            await fixture.DisposeAsync();
            await database.DataSource.ClearJobSubstrateAsync();
        }
    }

    // The empty value outranks a Redis address in the environment of the test process, and the host
    // reads the Aspire key only when this one is null. The Aspire key stays unset: any value under
    // it switches the host to its Aspire composition.
    private static void WithoutRedisConnectionString(IWebHostBuilder builder)
        => builder.UseSetting(RedisConnectionStringKey, string.Empty);

    // The fixture registers the provider services a second time, which adds an in-process import job
    // service after the one the host composed. The host's registration is the first.
    private static void KeepHostComposedImportJobService(IServiceCollection services)
    {
        var laterRegistrations = services
            .Where(descriptor => descriptor.ServiceType == typeof(IImportJobService))
            .Skip(1)
            .ToArray();
        foreach (var registration in laterRegistrations)
        {
            services.Remove(registration);
        }
    }

    private static MultipartFormDataContent CreateForcedBackgroundUpload(string tableName)
    {
        var file = new ByteArrayContent(DurableImportTestData.PointFeatureCollection(FeatureCount));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return new MultipartFormDataContent
        {
            { file, "File", $"{tableName}.geojson" },
            { new StringContent(tableName), "TableName" },
            { new StringContent("true"), "OverwriteExisting" },
            { new StringContent("true"), "ForceBackground" },
        };
    }

    private static async Task<ImportProgress> WaitForTerminalProgressAsync(HttpClient client, string jobId)
    {
        var deadline = DateTimeOffset.UtcNow + _importDeadline;
        ImportProgress? progress = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var response = await client.GetAsync($"/api/v1/admin/import/jobs/{jobId}");
            var body = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.OK, body);
            progress = JsonSerializer.Deserialize<ImportProgress>(body, _progressJson);
            if (progress?.Status is ImportStatus.Completed or ImportStatus.Failed or ImportStatus.Cancelled)
            {
                return progress;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        throw new TimeoutException(
            $"File-import job {jobId} did not finish within {_importDeadline}. Last status: {progress?.Status.ToString() ?? "<none>"}.");
    }
}
