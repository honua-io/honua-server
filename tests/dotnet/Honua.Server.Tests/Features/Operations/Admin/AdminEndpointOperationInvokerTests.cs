// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Globalization;
using System.Text;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.MultiTenancy.Domain;
using Honua.Core.Features.Operations.Domain;
using Honua.Server.Features.Operations.Admin;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Admin.OperationCatalog;

public sealed class AdminEndpointOperationInvokerTests
{
    private readonly AdminOpenApiOperationCatalog _catalog = new(FindAdminOpenApi());

    [UnitTest]
    public async Task InvokeAsync_CapturedTenantMissingFromCatalog_FailsClosed()
    {
        var endpointCalled = false;
        var definition = _catalog.GetRequired("admin.server.status");
        var tenantCatalog = Substitute.For<ITenantCatalog>();
        tenantCatalog.GetAsync("missing-tenant", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TenantRecord?>(null));
        var services = new ServiceCollection()
            .AddSingleton(tenantCatalog)
            .BuildServiceProvider();
        var invoker = CreateInvoker(
            services,
            definition,
            context =>
            {
                endpointCalled = true;
                context.Response.StatusCode = StatusCodes.Status200OK;
                return Task.CompletedTask;
            });

        var result = await invoker.InvokeAsync(
            definition,
            Request(definition),
            new OperationPolicyContext { TenantId = "missing-tenant" },
            definition.Descriptor.OperationId,
            CancellationToken.None);

        result.Status.Should().Be(OperationHandleStatus.Failed);
        result.Reason.Should().Be("Tenant access is unavailable.");
        result.Result!.Details["httpStatus"].Should()
            .Be(((int)HttpStatusCode.Forbidden).ToString(CultureInfo.InvariantCulture));
        endpointCalled.Should().BeFalse();
    }

    [UnitTest]
    public async Task InvokeAsync_RequestBodyExceedsConfiguredUploadLimit_Returns413BeforeRouting()
    {
        var definition = _catalog.GetRequired("admin.connection.create");
        var invoker = CreateInvoker(
            new ServiceCollection().BuildServiceProvider(),
            definition: null,
            handler: null,
            maxUploadBytes: 16);

        var result = await invoker.InvokeAsync(
            definition,
            Request(definition, "{\"value\":\"012345678901234567\"}"),
            new OperationPolicyContext(),
            definition.Descriptor.OperationId,
            CancellationToken.None);

        result.Status.Should().Be(OperationHandleStatus.Failed);
        result.Result!.Details["httpStatus"]
            .Should().Be(((int)HttpStatusCode.RequestEntityTooLarge).ToString(CultureInfo.InvariantCulture));
        result.Reason.Should().Contain("16-byte upload limit");
    }

    [UnitTest]
    public async Task InvokeAsync_LargeEndpointResponse_CapturesOnlyBoundedPrefix()
    {
        var definition = _catalog.GetRequired("admin.server.status");
        var response = new string('x', AdminEndpointOperationInvoker.MaxCapturedResponseBytes + 1024);
        var invoker = CreateInvoker(
            new ServiceCollection().BuildServiceProvider(),
            definition,
            context => context.Response.WriteAsync(response));

        var result = await invoker.InvokeAsync(
            definition,
            Request(definition),
            new OperationPolicyContext(),
            definition.Descriptor.OperationId,
            CancellationToken.None);

        result.Status.Should().Be(OperationHandleStatus.Completed);
        result.Result!.Details["response"].Should().Be("[REDACTED]",
            "a truncated non-JSON body cannot be proven credential-safe");
        result.Result.Details["responseTruncated"].Should().Be(bool.TrueString);
    }

    [UnitTest]
    public async Task InvokeAsync_XmlBody_RoundTripsRawUtf8Content()
    {
        var definition = _catalog.GetRequired("admin.layer.import-sld");
        const string xml = "<StyledLayerDescriptor version=\"1.0.0\" />";
        string? capturedBody = null;
        string? capturedContentType = null;
        var invoker = CreateInvoker(
            new ServiceCollection().BuildServiceProvider(),
            definition,
            async context =>
            {
                using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
                capturedBody = await reader.ReadToEndAsync();
                capturedContentType = context.Request.ContentType;
                context.Response.StatusCode = StatusCodes.Status200OK;
            });

        var result = await invoker.InvokeAsync(
            definition,
            Request(definition, xml),
            new OperationPolicyContext(),
            definition.Descriptor.OperationId,
            CancellationToken.None);

        result.Status.Should().Be(OperationHandleStatus.Completed);
        capturedBody.Should().Be(xml);
        capturedContentType.Should().Be("application/xml");
    }

    [UnitTest]
    public async Task InvokeAsync_RawBinaryBodies_DecodeBase64AndEnforceDecodedSize()
    {
        byte[] expected = [0, 1, 2, 255];
        var encoded = Convert.ToBase64String(expected);
        foreach (var openApiOperationId in new[] { "uploadLicense", "uploadLicenseFile", "importTileCachePackage" })
        {
            var definition = _catalog.Definitions.Single(item => item.OpenApiOperationId == openApiOperationId);
            byte[]? captured = null;
            var invoker = CreateInvoker(
                new ServiceCollection().BuildServiceProvider(),
                definition,
                async context =>
                {
                    using var stream = new MemoryStream();
                    await context.Request.Body.CopyToAsync(stream);
                    captured = stream.ToArray();
                    context.Response.StatusCode = StatusCodes.Status200OK;
                },
                maxUploadBytes: expected.Length);

            var result = await invoker.InvokeAsync(
                definition,
                Request(definition, encoded),
                new OperationPolicyContext(),
                definition.Descriptor.OperationId,
                CancellationToken.None);

            result.Status.Should().Be(OperationHandleStatus.Completed, openApiOperationId);
            captured.Should().Equal(expected);
        }

        var upload = _catalog.Definitions.Single(item => item.OpenApiOperationId == "uploadLicense");
        var overLimitInvoker = CreateInvoker(
            new ServiceCollection().BuildServiceProvider(),
            definition: null,
            handler: null,
            maxUploadBytes: expected.Length);
        var overLimit = await overLimitInvoker.InvokeAsync(
            upload,
            Request(upload, Convert.ToBase64String([0, 1, 2, 3, 4])),
            new OperationPolicyContext(),
            upload.Descriptor.OperationId,
            CancellationToken.None);
        overLimit.Status.Should().Be(OperationHandleStatus.Failed);
        overLimit.Result!.Details["httpStatus"].Should().Be("413");
    }

    [UnitTest]
    public async Task InvokeAsync_MultipartBinaryBinding_UsesSchemaFormatInsteadOfPropertyName()
    {
        var definition = _catalog.Definitions.Single(
            item => item.OpenApiOperationId == "importGeocoderReferenceData");
        byte[] expected = Encoding.UTF8.GetBytes("address,city\n1 Main St,Honolulu\n");
        byte[]? captured = null;
        string? capturedFileName = null;
        var invoker = CreateInvoker(
            new ServiceCollection().BuildServiceProvider(),
            definition,
            async context =>
            {
                var form = await context.Request.ReadFormAsync();
                var file = form.Files.GetFile("referenceData");
                file.Should().NotBeNull();
                capturedFileName = file!.FileName;
                using var stream = new MemoryStream();
                await file.CopyToAsync(stream);
                captured = stream.ToArray();
                form["mode"].ToString().Should().Be("replace");
                context.Response.StatusCode = StatusCodes.Status200OK;
            },
            maxUploadBytes: 1024);
        var body = $$"""{"referenceData":"{{Convert.ToBase64String(expected)}}","referenceDataFileName":"reference.csv","mode":"replace"}""";

        var result = await invoker.InvokeAsync(
            definition,
            Request(definition, body),
            new OperationPolicyContext(),
            definition.Descriptor.OperationId,
            CancellationToken.None);

        result.Status.Should().Be(OperationHandleStatus.Completed);
        captured.Should().Equal(expected);
        capturedFileName.Should().Be("reference.csv");
    }

    [UnitTest]
    public async Task InvokeAsync_InvalidBinaryBase64_FailsBeforeEndpointExecution()
    {
        var endpointCalled = false;
        var definition = _catalog.Definitions.Single(item => item.OpenApiOperationId == "uploadLicense");
        var invoker = CreateInvoker(
            new ServiceCollection().BuildServiceProvider(),
            definition,
            context =>
            {
                endpointCalled = true;
                return Task.CompletedTask;
            });

        var result = await invoker.InvokeAsync(
            definition,
            Request(definition, "not-base64"),
            new OperationPolicyContext(),
            definition.Descriptor.OperationId,
            CancellationToken.None);

        result.Status.Should().Be(OperationHandleStatus.Failed);
        result.Result!.Details["httpStatus"].Should().Be("400");
        endpointCalled.Should().BeFalse();
    }

    [UnitTest]
    public void Redact_NestedCredentialValues_RemovesSecretsAndPreservesReferences()
    {
        const string response =
            """{"data":{"apiKey":"hnua_secret","nested":[{"clientSecret":"oauth-secret","secretReference":"aws-sm://honua/control-plane","accessKey":"cloud-secret"}],"token":"bearer-secret","connectionString":"Host=db;Password=secret"}}""";

        var redacted = AdminOperationResponseRedactor.Redact(response);

        redacted.Should().NotContain("hnua_secret");
        redacted.Should().NotContain("oauth-secret");
        redacted.Should().NotContain("bearer-secret");
        redacted.Should().NotContain("cloud-secret");
        redacted.Should().NotContain("Host=db");
        redacted.Should().Contain("aws-sm://honua/control-plane");
        using var document = System.Text.Json.JsonDocument.Parse(redacted);
        document.RootElement.GetProperty("data").GetProperty("apiKey").GetString().Should().Be("[REDACTED]");
        document.RootElement.GetProperty("data").GetProperty("nested")[0]
            .GetProperty("clientSecret").GetString().Should().Be("[REDACTED]");
    }

    [UnitTest]
    public void Redact_SignedArtifactUrls_StripsBearerComponentsAndPreservesPublicOrProxyUrls()
    {
        const string response =
            """{"data":{"accessUrl":"https://objects.example.com/jobs/result.tif?X-Amz-Credential=secret&X-Amz-Signature=signature","downloadUrl":"https://account.blob.core.windows.net/jobs/result.tif?sv=2026&sig=sas-secret#fragment","publicUrl":"https://cdn.example.com/public/result.tif","proxyUrl":"/api/v1/jobs/job-1/results/result?download=true"}}""";

        var redacted = AdminOperationResponseRedactor.Redact(response);

        redacted.Should().NotContain("secret").And.NotContain("signature").And.NotContain("fragment");
        using var document = System.Text.Json.JsonDocument.Parse(redacted);
        var data = document.RootElement.GetProperty("data");
        data.GetProperty("accessUrl").GetString().Should().Be("https://objects.example.com/jobs/result.tif");
        data.GetProperty("downloadUrl").GetString().Should().Be("https://account.blob.core.windows.net/jobs/result.tif");
        data.GetProperty("publicUrl").GetString().Should().Be("https://cdn.example.com/public/result.tif");
        data.GetProperty("proxyUrl").GetString().Should().Be("/api/v1/jobs/job-1/results/result?download=true");
    }

    private static AdminEndpointOperationInvoker CreateInvoker(
        IServiceProvider services,
        AdminOpenApiOperationDefinition? definition,
        RequestDelegate? handler,
        long maxUploadBytes = 1024 * 1024)
    {
        EndpointDataSource[] sources = definition is null || handler is null
            ? []
            : [CreateDataSource(definition, handler)];
        return new AdminEndpointOperationInvoker(
            sources,
            Substitute.For<IAuthorizationPolicyProvider>(),
            Substitute.For<IPolicyEvaluator>(),
            services,
            TimeProvider.System,
            Options.Create(new LimitsOptions { MaxUploadSizeBytes = maxUploadBytes }));
    }

    private static DefaultEndpointDataSource CreateDataSource(
        AdminOpenApiOperationDefinition definition,
        RequestDelegate handler)
    {
        var builder = new RouteEndpointBuilder(handler, RoutePatternFactory.Parse(definition.Path), order: 0);
        builder.Metadata.Add(new HttpMethodMetadata([definition.Method]));
        return new DefaultEndpointDataSource(builder.Build());
    }

    private static OperationRequest Request(AdminOpenApiOperationDefinition definition, string? body = null)
        => new()
        {
            OperationId = definition.Descriptor.OperationId,
            Parameters = body is null
                ? new Dictionary<string, string?>(StringComparer.Ordinal)
                : new Dictionary<string, string?>(StringComparer.Ordinal) { ["body"] = body },
        };

    private static string FindAdminOpenApi()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "admin-openapi.json"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "docs", "developer", "api-specs", "admin-api.json")),
        };

        return candidates.First(File.Exists);
    }
}
