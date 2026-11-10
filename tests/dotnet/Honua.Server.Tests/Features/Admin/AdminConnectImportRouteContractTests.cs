// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Server.Features.Operations;

namespace Honua.Server.Tests.Features.Admin;

public sealed class AdminConnectImportRouteContractTests
{
    public static IEnumerable<object[]> OperationIds => AdminConnectImportOperationCatalog.Definitions
        .Select(definition => new object[] { definition.OperationId });

    [Theory]
    [MemberData(nameof(OperationIds))]
    public void OperationPath_MatchesOpenApiRoute(string operationId)
    {
        var definition = AdminConnectImportOperationCatalog.Definitions.Single(item => item.OperationId == operationId);
        using var stream = typeof(AdminConnectImportOperationCatalog).Assembly
            .GetManifestResourceStream("Honua.Server.admin-api.json")!;
        using var document = JsonDocument.Parse(stream);
        var method = definition.Method.Method.ToLowerInvariant();
        var path = document.RootElement.GetProperty("paths").EnumerateObject().Single(path =>
            path.Value.TryGetProperty(method, out var operation) &&
            operation.GetProperty("operationId").GetString() == definition.OpenApiOperationId);

        definition.Path.Should().Be(path.Name, "the executor must bind the path parameters advertised by OpenAPI");
    }
}
