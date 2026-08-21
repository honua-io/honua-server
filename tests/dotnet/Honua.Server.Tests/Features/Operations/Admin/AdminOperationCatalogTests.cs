// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Operations.Domain;
using Honua.Server.Features.Operations.Admin;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Admin.OperationCatalog;

/// <summary>
/// Inventory and schema-drift coverage for the 2026.1 Admin API operation catalog.
/// </summary>
public sealed class AdminOperationCatalogTests
{
    private readonly AdminOpenApiOperationCatalog _catalog = new(FindAdminOpenApi());

    [UnitTest]
    public void Catalog_ContainsEveryScopedOperationExactlyOnce()
    {
        // Every source operation must have exactly one executable catalog definition.
        _catalog.OpenApiOperationIds.Should().HaveCount(396);
        _catalog.OpenApiOperationIds.Should().OnlyHaveUniqueItems();
        _catalog.Definitions.Should().HaveCount(396);
        _catalog.Definitions.Select(definition => definition.Descriptor.OperationId)
            .Should().OnlyHaveUniqueItems();
        _catalog.Definitions.Select(definition => definition.OpenApiOperationId)
            .Should().OnlyHaveUniqueItems();
        _catalog.Definitions.Select(definition => definition.OpenApiOperationId)
            .Should().BeEquivalentTo(_catalog.OpenApiOperationIds);
        _catalog.Definitions.GroupBy(definition => definition.Lane)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal)
            .Should().BeEquivalentTo(new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["A"] = 23,
                ["B"] = 21,
                ["C"] = 49,
                ["D"] = 26,
                ["OpenAPI"] = 277,
            });
    }

    [UnitTest]
    public void Catalog_UsesSemanticAdminIdsAndOpenApiBindings()
    {
        foreach (var definition in _catalog.Definitions)
        {
            definition.Descriptor.OperationId.Should().MatchRegex("^admin\\.[a-z0-9-]+\\.[a-z0-9-]+$");
            definition.Path.Should().StartWith("/api/v1/admin/");
            definition.Method.Should().BeOneOf("GET", "POST", "PUT", "PATCH", "DELETE");
            definition.Descriptor.InputJsonSchema.Should().NotBeNull();
            definition.Descriptor.OutputJsonSchema.Should().NotBeNull();
        }

        _catalog.GetRequired("admin.server.status").OpenApiOperationId.Should().Be("getAdminVersion");
        _catalog.GetRequired("admin.connection.create").Path.Should().Be("/api/v1/admin/connections");
        _catalog.GetRequired("admin.connection.create").Method.Should().Be("POST");
    }

    [UnitTest]
    public void ConnectionCreate_InputSchemaComesFromOpenApiAndMarksSecrets()
    {
        var schema = _catalog.GetRequired("admin.connection.create").Descriptor.InputJsonSchema!.Value;
        var body = schema.GetProperty("properties").GetProperty("body");

        body.GetProperty("required").EnumerateArray().Select(value => value.GetString())
            .Should().Contain(["name", "host", "databaseName", "username"]);
        var properties = body.GetProperty("properties");
        properties.GetProperty("secretReference").GetProperty("format").GetString().Should().Be("secret_ref");
        properties.GetProperty("password").GetProperty("format").GetString().Should().Be("password");
        properties.GetProperty("password").GetProperty("writeOnly").GetBoolean().Should().BeTrue();
    }

    [UnitTest]
    public void PolicyAnnotationsAndDryRunBindingsAreExplicit()
    {
        var read = _catalog.GetRequired("admin.server.status").Descriptor;
        read.Policy.SideEffectClass.Should().Be(OperationSideEffectClass.ReadOnly);
        read.Policy.IsIdempotent.Should().BeTrue();

        var create = _catalog.GetRequired("admin.connection.create").Descriptor;
        create.Policy.SupportsDryRun.Should().BeTrue();
        create.Policy.DryRunOperationId.Should().Be("admin.connection.test-draft");
        _catalog.GetRequired(create.Policy.DryRunOperationId!).Descriptor.Policy.SideEffectClass
            .Should().NotBe(OperationSideEffectClass.DestroysState);

        var delete = _catalog.GetRequired("admin.tenant.delete").Descriptor;
        delete.Policy.SideEffectClass.Should().Be(OperationSideEffectClass.DestroysState);
        delete.Policy.IsIdempotent.Should().BeTrue();
    }

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
