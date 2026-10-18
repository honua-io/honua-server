// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Infrastructure.Authentication;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Import;

/// <summary>Real upload transport and authentication with recording cloud/job adapters.</summary>
[Collection("Database")]
[SecurityTest]
[Protocol(TestProtocols.Admin)]
public sealed class ImportUploadOwnershipHttpTests
{
    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Operation(Operations.Import)]
    [Endpoint("POST /api/v1/admin/import/upload")]
    [Endpoint("GET /api/v1/admin/import/uploads")]
    [Endpoint("GET /api/v1/admin/import/uploads/{uploadId}/progress")]
    [Endpoint("POST /api/v1/admin/import/uploads/{uploadId}/cancel")]
    public async Task ReusedClientUploadId_IsolatedAcrossRealCredentials(bool cachedProgress)
    {
        const string clientId = "same-client-attempt";
        var records = new ConcurrentDictionary<string, UploadProgress>(StringComparer.Ordinal);
        var accepted = new ConcurrentQueue<ImportRequest>();
        var storage = Substitute.For<ICloudFileStorage>();
        var cache = Substitute.For<IUploadProgressStore>();
        var jobs = Substitute.For<IImportJobService>();
        jobs.QueueImportAsync(Arg.Any<ImportRequest>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(call => { accepted.Enqueue(call.ArgAt<ImportRequest>(0)); return Guid.NewGuid().ToString("N"); });
        storage.UploadAsync(Arg.Any<FileUploadRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var request = call.ArgAt<FileUploadRequest>(0);
            records[request.UploadId] = UploadProgress.CreateInitial(request.UploadId, request.FileName, request.SizeBytes ?? 0);
            return UploadResult.CreateSuccess(new CloudFile
            {
                FileId = Guid.NewGuid().ToString("N"),
                FileName = request.FileName,
                StoragePath = "imports/test",
                ContentType = request.ContentType,
                SizeBytes = request.SizeBytes ?? 0,
                UploadedAt = DateTimeOffset.UtcNow,
                Provider = CloudStorageProvider.AwsS3,
            });
        });
        storage.GetUploadProgressAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => records.GetValueOrDefault(call.ArgAt<string>(0)));
        storage.GetActiveUploadsAsync(Arg.Any<CancellationToken>()).Returns(_ => records.Values.ToArray());
        storage.CancelUploadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => records.ContainsKey(call.ArgAt<string>(0)));
        cache.GetProgressAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => cachedProgress ? records.GetValueOrDefault(call.ArgAt<string>(0)) : null);
        var fixture = new WebAppFixture().ReplaceService(storage).ReplaceService(cache).ReplaceService(jobs)
            .ConfigureWebHost(builder =>
            {
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_DEV_AUTH_ALLOW_BYPASS", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
                builder.UseSetting("OperatorApproval:PublishRequiresApproval", "false");
                builder.UseSetting("OperatorApproval:AdminExemptFromApproval", "true");
            });
        await fixture.InitializeAsync();
        try
        {
            var keys = fixture.Services.GetRequiredService<IAdminApiKeyStore>();
            var first = await keys.CreateAsync("upload-first", ["admin:write", "agent:workspace"],
                DateTimeOffset.UtcNow.AddMinutes(15), "upload-test", default);
            var second = await keys.CreateAsync("upload-second", ["admin:write", "agent:workspace"],
                DateTimeOffset.UtcNow.AddMinutes(15), "upload-test", default);
            var reader = await keys.CreateAsync("upload-reader", ["admin:read"],
                DateTimeOffset.UtcNow.AddMinutes(15), "upload-test", default);
            using var owner = fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", first.Key));
            using var other = fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", second.Key));
            using var readOnly = fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", reader.Key));

            await UploadAsync(owner, clientId, "owner.geojson");
            var firstStorageId = records.Keys.Single();
            firstStorageId.Should().NotBe(clientId);
            using (var foreign = await other.GetAsync($"/api/v1/admin/import/uploads/{clientId}/progress"))
                foreign.StatusCode.Should().Be(HttpStatusCode.NotFound);
            using (var cancel = await other.PostAsync($"/api/v1/admin/import/uploads/{clientId}/cancel", null))
                cancel.StatusCode.Should().Be(HttpStatusCode.NotFound);
            await storage.DidNotReceive().CancelUploadAsync(firstStorageId, Arg.Any<CancellationToken>());
            using (var denied = await readOnly.PostAsync($"/api/v1/admin/import/uploads/{clientId}/cancel", null))
                denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);

            // A second actor reusing the same public correlation ID must not overwrite the first actor's progress.
            await UploadAsync(other, clientId, "other.geojson");
            records.Should().HaveCount(2);
            accepted.Should().HaveCount(2);
            accepted.Select(request => request.Ownership!.OwnerActorId).Distinct().Should().HaveCount(2);
            accepted.Should().OnlyContain(request => request.Ownership!.TenantId == "public" && request.UploadId == clientId);
            foreach (var (client, fileName) in new[] { (owner, "owner.geojson"), (other, "other.geojson") })
            {
                using var response = await client.GetAsync($"/api/v1/admin/import/uploads/{clientId}/progress");
                response.StatusCode.Should().Be(HttpStatusCode.OK);
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                body.RootElement.GetProperty("uploadId").GetString().Should().Be(clientId);
                body.RootElement.GetProperty("fileName").GetString().Should().Be(fileName);
                using var list = await client.GetAsync("/api/v1/admin/import/uploads");
                list.StatusCode.Should().Be(HttpStatusCode.OK);
                using var uploads = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
                uploads.RootElement.GetProperty("uploads").EnumerateArray().Select(value => value.GetProperty("fileName").GetString())
                    .Should().Equal(fileName);
            }
            using var allowed = await owner.PostAsync($"/api/v1/admin/import/uploads/{clientId}/cancel", null);
            allowed.StatusCode.Should().Be(HttpStatusCode.OK);
            await storage.Received(1).CancelUploadAsync(firstStorageId, Arg.Any<CancellationToken>());
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static async Task UploadAsync(HttpClient client, string id, string fileName)
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent("""{"type":"FeatureCollection","features":[]}""", Encoding.UTF8, "application/geo+json"), "File", fileName);
        content.Add(new StringContent("upload_authority"), "TableName");
        content.Add(new StringContent("true"), "ForceBackground");
        content.Add(new StringContent(id), "UploadId");
        using var response = await client.PostAsync("/api/v1/admin/import/upload", content);
        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Accepted, payload);
        using var body = JsonDocument.Parse(payload);
        body.RootElement.GetProperty("uploadId").GetString().Should().Be(id);
        body.RootElement.GetProperty("jobId").GetString().Should().NotBeNullOrWhiteSpace();
    }
}
