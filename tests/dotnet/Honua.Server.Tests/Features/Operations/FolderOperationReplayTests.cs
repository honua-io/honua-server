// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Honua.Core.Features.Operations.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Server.Features.Operations;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using NSubstitute;

namespace Honua.Server.Tests.Features.OperationsToolset;

public sealed class FolderOperationReplayTests
{
    [UnitTest]
    public async Task FolderMove_ApprovedReplay_PreservesNamespaceAndEtag()
    {
        using var handler = new FolderHandler(HttpStatusCode.NoContent);
        using var client = new HttpClient(handler);
        var executor = CreateExecutor("service.move", client);
        var request = new OperationRequest
        {
            OperationId = executor.OperationId,
            ServiceName = "test",
            Parameters = new Dictionary<string, string?> { ["folderId"] = null, ["namespace"] = "workspace", ["If-Match"] = "\"current\"" }
        };
        (await executor.ValidateAsync(request)).IsValid.Should().BeTrue();
        var result = await executor.SubmitAsync(request, new OperationPolicyContext { ApprovedProposalId = "approved", TenantId = "public" });
        result.Status.Should().Be(OperationHandleStatus.Completed);
        handler.Precondition.Should().Be("\"current\"");
        handler.Query.Should().Be("?namespace=workspace");
        handler.Tenant.Should().Be("public");
        handler.Body.Should().Be("{\"folderId\":null}");
        result.Result!.Details["etag"].Should().Be("\"updated\"");
    }

    [UnitTest]
    public async Task FolderList_ThroughCanonicalOperation_ExposesSnapshotEtag()
    {
        using var handler = new FolderHandler(HttpStatusCode.OK);
        using var client = new HttpClient(handler);
        var executor = CreateExecutor("folder.list", client);
        var result = await executor.SubmitAsync(new OperationRequest { OperationId = executor.OperationId }, new OperationPolicyContext());
        result.Result!.Details["etag"].Should().Be("\"updated\"");
        AdminApiOperationCatalog.Descriptors.Single(d => d.OperationId == executor.OperationId)
            .OutputSchema.Should().Contain(parameter => parameter.Name == "etag");
    }

    [UnitTest]
    public async Task FolderCreate_WithoutName_FailsCanonicalValidation()
    {
        using var client = new HttpClient();
        var executor = CreateExecutor("folder.create", client);
        var parameters = new Dictionary<string, string?> { ["If-Match"] = "\"current\"" };
        var request = new OperationRequest { OperationId = executor.OperationId, Parameters = parameters };
        (await executor.ValidateAsync(request)).IsValid.Should().BeFalse();
        parameters["name"] = "Survey";
        (await executor.ValidateAsync(request)).IsValid.Should().BeTrue();
    }

    private static AdminApiOperationExecutor CreateExecutor(string operationId, HttpClient client)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(AdminApiOperationExecutor.HttpClientName).Returns(client);
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString("localhost");
        context.Connection.LocalPort = 8080;
        return new AdminApiOperationExecutor(AdminApiOperationCatalog.Definitions.Single(d => d.OperationId == operationId),
            factory, new HttpContextAccessor { HttpContext = context }, new InMemoryAdminApiKeyStore(), TimeProvider.System,
            new OperationLineageAttestationStore(TimeProvider.System));
    }

    private sealed class FolderHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public string? Precondition { get; private set; }
        public string? Body { get; private set; }
        public string? Query { get; private set; }
        public string? Tenant { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Query = request.RequestUri!.Query;
            Tenant = request.Headers.TryGetValues("X-Honua-Tenant", out var tenants) ? tenants.Single() : null;
            Precondition = request.Headers.IfMatch.SingleOrDefault()?.ToString();
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(status == HttpStatusCode.NoContent ? string.Empty : "{}")
            };
            response.Headers.ETag = new EntityTagHeaderValue("\"updated\"");
            return response;
        }
    }
}
