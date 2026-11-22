// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Operations.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Server.Features.Operations;
using Honua.TestKit.Attributes;
using NSubstitute;

namespace Honua.Server.Tests.Features.OperationsToolset;

public sealed partial class AdminOperationRequestContractTests
{
    [UnitTest]
    public async Task ConnectImport_RefreshExtents_ForwardsServiceNameInQueryAndExcludesRouteAndQueryFromBody()
    {
        using var capture = new CaptureHandler();
        using var client = new HttpClient(capture);
        var executor = CreateConnectImport("admin.connections.extents.refresh", client);
        await executor.SubmitAsync(new OperationRequest
        {
            OperationId = executor.OperationId,
            Parameters = new Dictionary<string, string?>
            {
                ["id"] = "11111111-1111-1111-1111-111111111111",
                ["serviceName"] = "roads",
            },
        }, new OperationPolicyContext());

        capture.Uri!.AbsolutePath.Should().Be("/api/v1/admin/connections/11111111-1111-1111-1111-111111111111/layers/extents/refresh");
        capture.Uri.Query.Should().Be("?serviceName=roads");
        using var body = JsonDocument.Parse(capture.Body!);
        body.RootElement.TryGetProperty("id", out _).Should().BeFalse();
        body.RootElement.TryGetProperty("serviceName", out _).Should().BeFalse();
    }

    [UnitTest]
    public async Task ConnectImport_GetWithRouteParameter_DoesNotLeakRouteParameterIntoQuery()
    {
        using var capture = new CaptureHandler();
        using var client = new HttpClient(capture);
        var executor = CreateConnectImport("admin.connections.layers.list", client);
        await executor.SubmitAsync(new OperationRequest
        {
            OperationId = executor.OperationId,
            Parameters = new Dictionary<string, string?>
            {
                ["id"] = "11111111-1111-1111-1111-111111111111",
                ["serviceName"] = "roads",
                ["page"] = "2",
            },
        }, new OperationPolicyContext());

        capture.Uri!.AbsolutePath.Should().Be("/api/v1/admin/connections/11111111-1111-1111-1111-111111111111/layers");
        capture.Uri.Query.Should().Contain("serviceName=roads").And.Contain("page=2");
        capture.Uri.Query.Should().NotContain("id=");
    }

    [UnitTest]
    public async Task ConnectImport_PutWithRouteParameter_DoesNotLeakRouteParameterIntoJsonBody()
    {
        using var capture = new CaptureHandler();
        using var client = new HttpClient(capture);
        var executor = CreateConnectImport("admin.connections.update", client);
        await executor.SubmitAsync(new OperationRequest
        {
            OperationId = executor.OperationId,
            Parameters = new Dictionary<string, string?>
            {
                ["id"] = "11111111-1111-1111-1111-111111111111",
                ["name"] = "warehouse",
            },
        }, new OperationPolicyContext());

        capture.Uri!.AbsolutePath.Should().Be("/api/v1/admin/connections/11111111-1111-1111-1111-111111111111");
        capture.Uri.Query.Should().BeEmpty();
        using var body = JsonDocument.Parse(capture.Body!);
        body.RootElement.GetProperty("name").GetString().Should().Be("warehouse");
        body.RootElement.TryGetProperty("id", out _).Should().BeFalse();
    }

    [UnitTest]
    public async Task ConnectImport_EveryContractQueryParameter_IsForwardedAndNeverLeaked()
    {
        using var stream = typeof(AdminConnectImportOperationCatalog).Assembly.GetManifestResourceStream("Honua.Server.admin-api.json")!;
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;

        foreach (var definition in AdminConnectImportOperationCatalog.Definitions)
        {
            var queryParams = ConnectImportQueryParameterNames(root, definition.OpenApiOperationId);
            if (queryParams.Length == 0)
            {
                continue;
            }

            var parameters = new Dictionary<string, string?>(StringComparer.Ordinal);
            var routeNames = definition.Path.Split('/')
                .Where(static segment => segment.StartsWith('{') && segment.EndsWith('}'))
                .Select(static segment => segment[1..^1])
                .ToArray();
            foreach (var routeName in routeNames)
            {
                parameters[routeName] = "route-val";
            }

            foreach (var queryName in queryParams)
            {
                parameters[queryName] = "query-val";
            }

            using var capture = new CaptureHandler();
            using var client = new HttpClient(capture);
            var executor = CreateConnectImport(definition.OperationId, client);
            await executor.SubmitAsync(new OperationRequest
            {
                OperationId = definition.OperationId,
                Parameters = parameters,
            }, new OperationPolicyContext());

            foreach (var queryName in queryParams)
            {
                capture.Uri!.Query.Should().Contain($"{queryName}=query-val",
                    $"{definition.OperationId} must forward query parameter '{queryName}'");
            }

            foreach (var routeName in routeNames)
            {
                capture.Uri!.Query.Should().NotContain($"{routeName}=",
                    $"{definition.OperationId} must not leak route parameter '{routeName}' into the query string");
            }
        }
    }

    private static string[] ConnectImportQueryParameterNames(JsonElement root, string openApiOperationId)
    {
        var operation = AdminConnectImportOperationCatalog.FindOperation(root, openApiOperationId);
        if (!operation.TryGetProperty("parameters", out var parameters))
        {
            return [];
        }

        return parameters.EnumerateArray()
            .Select(parameter => AdminOperationSchema.Resolve(root, parameter).Schema)
            .Where(parameter => parameter.GetProperty("in").GetString() == "query")
            .Select(parameter => parameter.GetProperty("name").GetString()!)
            .ToArray();
    }

    private static AdminConnectImportOperationExecutor CreateConnectImport(string id, HttpClient client)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(AdminConnectImportOperationExecutor.HttpClientName).Returns(client);
        return new AdminConnectImportOperationExecutor(
            AdminConnectImportOperationCatalog.Definitions.Single(definition => definition.OperationId == id),
            factory, Context(), new InMemoryAdminApiKeyStore(TimeProvider.System), TimeProvider.System,
            new OperationLineageAttestationStore(TimeProvider.System));
    }
}
