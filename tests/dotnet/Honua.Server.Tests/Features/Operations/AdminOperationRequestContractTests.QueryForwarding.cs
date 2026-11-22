// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Operations.Domain;
using Honua.Server.Features.Operations;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.OperationsToolset;

public sealed partial class AdminOperationRequestContractTests
{
    [UnitTest]
    public async Task ServicesList_StorageLayerFilter_RemainsInQuery()
    {
        using var capture = new CaptureHandler();
        using var client = new HttpClient(capture);
        var executor = CreateApi("admin.services.list", client);
        await executor.SubmitAsync(new OperationRequest
        {
            OperationId = executor.OperationId,
            ConnectionId = "11111111-1111-1111-1111-111111111111",
            Parameters = new Dictionary<string, string?>
            {
                ["connectionId"] = "11111111-1111-1111-1111-111111111111",
                ["storageLayerId"] = "78",
            },
        }, new OperationPolicyContext());

        capture.Uri!.AbsolutePath.Should().Be("/api/v1/admin/services");
        capture.Uri.Query.Should().Contain("connectionId=11111111-1111-1111-1111-111111111111")
            .And.Contain("storageLayerId=78");
    }

    [UnitTest]
    public void EveryContractQueryParameter_IsForwardedByTheReplay()
    {
        using var stream = typeof(AdminApiOperationCatalog).Assembly.GetManifestResourceStream("Honua.Server.admin-api.json")!;
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;

        var unforwarded = AdminApiOperationCatalog.Definitions
            .SelectMany(definition => ContractQueryParameterNames(root, definition.OpenApiOperationId)
                .Where(name => definition.QueryParameters?.Contains(name) != true)
                .Select(name => $"{definition.OperationId}:{name}"))
            .ToArray();

        unforwarded.Should().BeEmpty("an input the tool schema advertises must reach the replayed admin route");
    }

    private static string[] ContractQueryParameterNames(JsonElement root, string openApiOperationId)
    {
        var operation = AdminApiOperationCatalog.FindOperation(root, openApiOperationId);
        if (!operation.TryGetProperty("parameters", out var parameters))
        {
            return [];
        }

        return parameters.EnumerateArray()
            .Select(parameter => ResolveParameter(root, parameter))
            .Where(parameter => parameter.GetProperty("in").GetString() == "query")
            .Select(parameter => parameter.GetProperty("name").GetString()!)
            .ToArray();
    }

    private static JsonElement ResolveParameter(JsonElement root, JsonElement parameter)
    {
        if (!parameter.TryGetProperty("$ref", out var reference))
        {
            return parameter;
        }

        var resolved = root;
        foreach (var segment in reference.GetString()!.TrimStart('#', '/').Split('/'))
        {
            resolved = resolved.GetProperty(segment);
        }

        return resolved;
    }
}
