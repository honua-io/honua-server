// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Operations.Services;
using Honua.Infrastructure.Authentication;
using Honua.Server.Features.Operations;
using Microsoft.AspNetCore.Http;
using NSubstitute;

namespace Honua.Server.Tests.Features.OperationsToolset;

[Trait("Tier", "Fast")]
public sealed class AdminOperationRequestContractTests
{
    [Fact]
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

    [Theory]
    [InlineData("roads", "?serviceName=roads")]
    [InlineData("roads & rail?#%", "?serviceName=roads%20%26%20rail%3F%23%25")]
    public async Task SetLayerEnabled_ServiceFilter_RemainsInQuery(string service, string query)
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
                ["layerId"] = "1",
                ["serviceName"] = service,
                ["enabled"] = "true"
            }
        }, new OperationPolicyContext());

        capture.Uri!.Query.Should().Be(query);
        capture.Uri.AbsolutePath.Should().EndWith("/layers/1/enabled");
    }

    [Theory]
    [InlineData("2026")]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("null")]
    [InlineData("\"quoted\"")]
    [InlineData("{\"title\":1}")]
    public async Task ReleasePackageCreate_DeclaredText_RemainsAString(string title)
    {
        using var capture = new CaptureHandler();
        using var client = new HttpClient(capture);
        var executor = CreateOperate("admin.metadata.release-packages.create", client);
        await executor.SubmitAsync(new OperationRequest
        {
            OperationId = executor.OperationId,
            Parameters = new Dictionary<string, string?> { ["sourceEnvironment"] = "staging", ["title"] = title }
        }, new OperationPolicyContext());

        using var body = JsonDocument.Parse(capture.Body!);
        body.RootElement.GetProperty("title").ValueKind.Should().Be(JsonValueKind.String);
        body.RootElement.GetProperty("title").GetString().Should().Be(title);
    }

    [Fact]
    public async Task MetadataPrevalidate_MissingRequiredTarget_IsInvalid()
    {
        using var capture = new CaptureHandler();
        using var client = new HttpClient(capture);
        var executor = CreateOperate("admin.metadata.prevalidate", client);
        var validation = await executor.ValidateAsync(new OperationRequest { OperationId = executor.OperationId });
        validation.IsValid.Should().BeFalse("the published schema requires targetEnvironment and a release package");
    }

    [Theory]
    [InlineData("admin.metadata.release-packages.create", "sourceEnvironment")]
    [InlineData("admin.metadata.releases.activate", "packageId")]
    [InlineData("admin.metadata.releases.activate", "targetEnvironment")]
    [InlineData("admin.metadata.releases.activate", "resourceSemanticId")]
    [InlineData("admin.metadata.releases.activate", "newFieldName")]
    [InlineData("admin.cache.invalidate", "scope")]
    [InlineData("admin.license.upload", "body")]
    public async Task Validate_MissingDeclaredRequiredField_IsInvalid(string operationId, string missing)
    {
        using var capture = new CaptureHandler();
        using var client = new HttpClient(capture);
        var executor = CreateOperate(operationId, client);
        var parameters = new Dictionary<string, string?>
        {
            ["sourceEnvironment"] = "staging",
            ["packageId"] = "package-a",
            ["targetEnvironment"] = "production",
            ["resourceSemanticId"] = "layer-a",
            ["newFieldName"] = "status",
            ["scope"] = "all",
            ["body"] = "license",
        };
        parameters.Remove(missing);
        var result = await executor.ValidateAsync(new OperationRequest { OperationId = operationId, Parameters = parameters });
        result.IsValid.Should().BeFalse();
        result.Messages.Should().Contain(message => message.Contains(missing, StringComparison.OrdinalIgnoreCase));
        capture.Uri.Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MetadataPrevalidate_RequiresExactlyOnePackage(bool both)
    {
        using var capture = new CaptureHandler();
        using var client = new HttpClient(capture);
        var executor = CreateOperate("admin.metadata.prevalidate", client);
        var parameters = new Dictionary<string, string?> { ["targetEnvironment"] = "production" };
        if (both)
        {
            parameters["releasePackageId"] = "11111111-1111-1111-1111-111111111111";
            parameters["releasePackage"] = "{}";
        }
        var validation = await executor.ValidateAsync(new OperationRequest { OperationId = executor.OperationId, Parameters = parameters });
        validation.IsValid.Should().BeFalse();
        capture.Uri.Should().BeNull();
    }

    [Fact]
    public async Task DryRun_MissingRequiredFields_DoesNotReturnSuccessfulReceipt()
    {
        using var capture = new CaptureHandler();
        using var client = new HttpClient(capture);
        var executor = CreateOperate("admin.metadata.prevalidate", client);
        using var catalog = new OperationCatalog([new ContractProvider()], TimeProvider.System);
        var dispatcher = new OperationDispatcher(catalog,
            [executor], new AllowAllPolicyDecisionPoint(), TimeProvider.System);
        var result = await dispatcher.SubmitAsync(new OperationRequest { OperationId = executor.OperationId, DryRun = true },
            new OperationPolicyContext());
        result.Status.Should().Be(OperationHandleStatus.Failed);
        capture.Uri.Should().BeNull();
    }

    [Fact]
    public async Task ReleasePackageCreate_StructuredInputs_PreserveDeclaredJsonTypes()
    {
        using var capture = new CaptureHandler();
        using var client = new HttpClient(capture);
        var executor = CreateOperate("admin.metadata.release-packages.create", client);
        await executor.SubmitAsync(new OperationRequest
        {
            OperationId = executor.OperationId,
            Parameters = new Dictionary<string, string?>
            {
                ["sourceEnvironment"] = "staging",
                ["desiredRevision"] = "2026",
                ["targetEnvironments"] = "[\"production\"]",
                ["changeClasses"] = "{\"layer-a\":\"metadata\"}",
            },
        }, new OperationPolicyContext());
        using var body = JsonDocument.Parse(capture.Body!);
        body.RootElement.GetProperty("desiredRevision").GetInt64().Should().Be(2026);
        body.RootElement.GetProperty("targetEnvironments")[0].GetString().Should().Be("production");
        body.RootElement.GetProperty("changeClasses").GetProperty("layer-a").GetString().Should().Be("metadata");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Validate_EmptyRequiredValues_AreInvalid(string? value)
    {
        using var capture = new CaptureHandler();
        using var client = new HttpClient(capture);
        var executor = CreateOperate("admin.metadata.release-packages.create", client);
        var validation = await executor.ValidateAsync(new OperationRequest
        {
            OperationId = executor.OperationId,
            Parameters = new Dictionary<string, string?> { ["sourceEnvironment"] = value }
        });
        validation.IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("layer", "serviceId")]
    [InlineData("service", "serviceId")]
    [InlineData("collection", "collectionId")]
    public async Task Validate_CacheScopeRequiresItsTarget(string scope, string missing)
    {
        using var capture = new CaptureHandler();
        using var client = new HttpClient(capture);
        var executor = CreateOperate("admin.cache.invalidate", client);
        var validation = await executor.ValidateAsync(new OperationRequest
        {
            OperationId = executor.OperationId,
            Parameters = new Dictionary<string, string?> { ["scope"] = scope }
        });
        validation.IsValid.Should().BeFalse();
        validation.Messages.Should().Contain(message => message.Contains(missing, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Validate_InlinePackageRequiresDeclaredNestedFields()
    {
        using var capture = new CaptureHandler();
        using var client = new HttpClient(capture);
        var executor = CreateOperate("admin.metadata.prevalidate", client);
        var validation = await executor.ValidateAsync(new OperationRequest
        {
            OperationId = executor.OperationId,
            Parameters = new Dictionary<string, string?> { ["targetEnvironment"] = "production", ["releasePackage"] = "{}" }
        });
        validation.IsValid.Should().BeFalse();
        validation.Messages.Should().Contain(message => message.Contains("releasePackage.sourceEnvironment", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Validate_PersistedPackageAndTarget_AreValidWithoutHttp()
    {
        using var capture = new CaptureHandler();
        using var client = new HttpClient(capture);
        var executor = CreateOperate("admin.metadata.prevalidate", client);
        var validation = await executor.ValidateAsync(new OperationRequest
        {
            OperationId = executor.OperationId,
            Parameters = new Dictionary<string, string?>
            {
                ["targetEnvironment"] = "production",
                ["releasePackageId"] = "11111111-1111-1111-1111-111111111111"
            }
        });
        validation.IsValid.Should().BeTrue();
        capture.Uri.Should().BeNull();
    }

    [Theory]
    [InlineData("scope", "typo")]
    [InlineData("layerId", "1.5")]
    [InlineData("layerId", "2147483648")]
    public async Task Validate_InvalidScalarOrEnum_IsRejected(string parameter, string value)
    {
        using var client = new HttpClient(new CaptureHandler());
        var executor = CreateOperate("admin.cache.invalidate", client);
        var parameters = new Dictionary<string, string?> { ["scope"] = "layer", ["serviceId"] = "roads", ["layerId"] = "1" };
        parameters[parameter] = value;
        (await executor.ValidateAsync(new OperationRequest { OperationId = executor.OperationId, Parameters = parameters }))
            .IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[null]")]
    public async Task Validate_NonNullableScriptCollection_RejectsExplicitNull(string scripts)
    {
        using var client = new HttpClient(new CaptureHandler());
        var executor = CreateOperate("admin.metadata.prevalidate", client);
        var request = new OperationRequest
        {
            OperationId = executor.OperationId,
            Parameters = new Dictionary<string, string?>
            {
                ["targetEnvironment"] = "production",
                ["releasePackageId"] = "11111111-1111-1111-1111-111111111111",
                ["dataScripts"] = scripts
            }
        };
        (await executor.ValidateAsync(request)).IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_NullableOptionalNumber_AllowsExplicitNull()
    {
        using var client = new HttpClient(new CaptureHandler());
        var executor = CreateOperate("admin.cache.invalidate", client);
        (await executor.ValidateAsync(new OperationRequest
        {
            OperationId = executor.OperationId,
            Parameters = new Dictionary<string, string?> { ["scope"] = "all", ["layerId"] = "null" }
        })).IsValid.Should().BeTrue();
    }

    private sealed class ContractProvider : IOperationDescriptorProvider
    {
        public string ProviderId => ServicePublishOperation.ProviderId;

        public Task<IReadOnlyList<IOperationDescriptor>> ListDescriptorsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<IOperationDescriptor>>(AdminOperateOperationCatalog.Descriptors);
    }

    private static AdminOperateOperationExecutor CreateOperate(string id, HttpClient client)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(AdminOperateOperationExecutor.HttpClientName).Returns(client);
        return new AdminOperateOperationExecutor(AdminOperateOperationCatalog.Definitions.Single(d => d.OperationId == id),
            factory, Context(), new InMemoryAdminApiKeyStore(TimeProvider.System), TimeProvider.System,
            new OperationLineageAttestationStore(TimeProvider.System));
    }

    private static HttpContextAccessor Context()
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
