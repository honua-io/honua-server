// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Operations.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Server.Features.Operations;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using NSubstitute;

namespace Honua.Server.Tests.Features.Operations;

public sealed class AdminOperationsGaContractTests
{
    [UnitTest]
    public async Task ReleasePackageList_Pagination_RemainsInQuery()
    {
        using var capture = new CaptureHandler();
        using var client = new HttpClient(capture);
        var executor = CreateOperate("admin.metadata.release-packages.list", client);
        await executor.SubmitAsync(new OperationRequest
        {
            OperationId = executor.OperationId,
            Parameters = new Dictionary<string, string?> { ["limit"] = "1", ["offset"] = "2" }
        }, new OperationPolicyContext());

        capture.Uri!.AbsolutePath.Should().Be("/api/v1/admin/metadata/release-packages");
        capture.Uri.Query.Should().Be("?limit=1&offset=2");
    }

    [UnitTest]
    public async Task SetLayerEnabled_ServiceFilter_RemainsInQuery()
    {
        using var capture = new CaptureHandler();
        using var client = new HttpClient(capture);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(AdminApiOperationExecutor.HttpClientName).Returns(client);
        var executor = new AdminApiOperationExecutor(
            AdminApiOperationCatalog.Definitions.Single(d => d.OperationId == "admin.layer.set-enabled"),
            factory, Context(), new InMemoryAdminApiKeyStore(TimeProvider.System), TimeProvider.System,
            new OperationLineageAttestationStore(TimeProvider.System));
        await executor.SubmitAsync(new OperationRequest
        {
            OperationId = executor.OperationId,
            ConnectionId = "11111111-1111-1111-1111-111111111111",
            Parameters = new Dictionary<string, string?>
            {
                ["layerId"] = "1", ["serviceName"] = "roads", ["enabled"] = "true"
            }
        }, new OperationPolicyContext());

        capture.Uri!.Query.Should().Be("?serviceName=roads");
        capture.Uri.AbsolutePath.Should().EndWith("/layers/1/enabled");
    }

    [UnitTest]
    public async Task ReleasePackageCreate_NumericText_RemainsAString()
    {
        using var capture = new CaptureHandler();
        using var client = new HttpClient(capture);
        var executor = CreateOperate("admin.metadata.release-packages.create", client);
        await executor.SubmitAsync(new OperationRequest
        {
            OperationId = executor.OperationId,
            Parameters = new Dictionary<string, string?> { ["sourceEnvironment"] = "staging", ["title"] = "2026" }
        }, new OperationPolicyContext());

        using var body = JsonDocument.Parse(capture.Body!);
        body.RootElement.GetProperty("title").ValueKind.Should().Be(JsonValueKind.String);
        body.RootElement.GetProperty("title").GetString().Should().Be("2026");
    }

    [UnitTest]
    public async Task MetadataPrevalidate_MissingRequiredTarget_IsInvalid()
    {
        using var capture = new CaptureHandler();
        using var client = new HttpClient(capture);
        var executor = CreateOperate("admin.metadata.prevalidate", client);
        var validation = await executor.ValidateAsync(new OperationRequest { OperationId = executor.OperationId });
        validation.IsValid.Should().BeFalse("the published schema requires targetEnvironment and a release package");
    }

    private static AdminOperateOperationExecutor CreateOperate(string id, HttpClient client)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(AdminOperateOperationExecutor.HttpClientName).Returns(client);
        return new AdminOperateOperationExecutor(AdminOperateOperationCatalog.Definitions.Single(d => d.OperationId == id),
            factory, Context(), new InMemoryAdminApiKeyStore(TimeProvider.System), TimeProvider.System,
            new OperationLineageAttestationStore(TimeProvider.System));
    }

    private static IHttpContextAccessor Context()
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString("localhost", 8080);
        context.Request.Scheme = "http";
        context.Connection.LocalPort = 8080;
        return new HttpContextAccessor { HttpContext = context };
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public Uri? Uri { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }
}
