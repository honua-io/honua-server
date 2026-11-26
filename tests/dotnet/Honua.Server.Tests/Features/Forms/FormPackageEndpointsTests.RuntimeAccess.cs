// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.Forms.Packages;
using Honua.Infrastructure.Authentication;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Forms;

public sealed partial class FormPackageEndpointsTests
{
    [IntegrationTest]
    [Operation(Operations.Configuration)]
    [Endpoint("GET /api/v1/forms/{formId}")]
    [Endpoint("GET /api/v1/forms/{formId}/versions/{formVersion}")]
    [Endpoint("POST /api/v1/forms/{formId}/submissions")]
    public async Task PublishedRuntime_EnforcesLayerScopesAndKeepsPublishingAdminOnly()
    {
        var published = await PublishPackageAsync(CreatePackage("Scoped inspection"));
        await using var services = _fixture.Services.CreateAsyncScope();
        var target = await services.ServiceProvider.GetRequiredService<IFormTargetMetadataResolver>()
            .ResolveAsync(published.Package.Target);
        var scope = $"{target.Service!.Metadata.Name}/{target.Resource!.Metadata.Name}";
        var keys = _fixture.Services.GetRequiredService<IAdminApiKeyStore>();
        var readerKey = await keys.CreateAsync("form-reader", [$"read:{scope}"], null, "test", CancellationToken.None);
        var workerKey = await keys.CreateAsync("form-worker", [$"read:{scope}", $"write:{scope}"], null, "test", CancellationToken.None);
        var unrelatedKey = await keys.CreateAsync(
            "form-unrelated", ["read:unrelated/unrelated", "write:unrelated/unrelated"], null, "test", CancellationToken.None);
        using var anonymous = _fixture.CreateClient();
        using var reader = _fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", readerKey.Key));
        using var worker = _fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", workerKey.Key));
        using var unrelated = _fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", unrelatedKey.Key));

        var path = $"/api/v1/forms/{published.FormId}";
        using var noCredential = await anonymous.GetAsync(path);
        noCredential.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var read = await reader.GetAsync(path);
        read.StatusCode.Should().Be(HttpStatusCode.OK);
        using var pinned = await worker.GetAsync($"{path}/versions/{published.Version}");
        pinned.StatusCode.Should().Be(HttpStatusCode.OK);
        using var outside = await unrelated.GetAsync($"{path}/versions/{published.Version}");
        outside.StatusCode.Should().Be(HttpStatusCode.NotFound);

        await AssertScopedSubmissionAsync(new(path, reader, worker) { Unrelated = unrelated });

        var draft = await CreateDraftAsync(CreatePackage("Scoped publish denied"));
        using var prohibited = await worker.PostAsync($"/api/v1/admin/forms/{draft.FormId}/versions/1/publish", null);
        prohibited.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private sealed record ScopedSubmissionClients(string Path, HttpClient Reader, HttpClient Worker)
    {
        public required HttpClient Unrelated { get; init; }
    }

    private static async Task AssertScopedSubmissionAsync(ScopedSubmissionClients clients)
    {
        var submission = CreateSubmission("scoped-worker", "Scoped record");
        using var readerContent = JsonContent(submission);
        using var denied = await clients.Reader.PostAsync($"{clients.Path}/submissions", readerContent);
        denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var unrelatedContent = JsonContent(submission);
        using var unrelatedWrite = await clients.Unrelated.PostAsync($"{clients.Path}/submissions", unrelatedContent);
        unrelatedWrite.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var workerContent = JsonContent(submission);
        using var edit = await clients.Worker.PostAsync($"{clients.Path}/submissions", workerContent);
        edit.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadJsonAsync(edit, FormPackageJsonContext.Default.FormSubmissionResponse);
        result.Status.Should().Be("accepted");
    }
}
