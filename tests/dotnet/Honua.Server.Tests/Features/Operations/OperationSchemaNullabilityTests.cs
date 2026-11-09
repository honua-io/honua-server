// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.Core.Features.WorkflowPackages.Domain;
using Honua.Server.Features.Operations;
using Honua.Server.Features.WorkflowPackages;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.OperationSchemas;

public sealed class OperationSchemaNullabilityTests
{
    private const string Document = """
        {
          "components": {
            "schemas": {
              "Selection": { "type": "string", "nullable": true },
              "Name": { "type": "string" },
              "Request": {
                "type": "object",
                "properties": {
                  "direct": { "type": "string", "nullable": true },
                  "referenced": { "$ref": "#/components/schemas/Selection" },
                  "wrapped": { "allOf": [ { "$ref": "#/components/schemas/Name" } ], "nullable": true },
                  "plain": { "$ref": "#/components/schemas/Name" },
                  "choice": { "type": "string", "nullable": true, "enum": ["survey", null] },
                  "list": { "type": "array", "items": { "type": "integer", "nullable": true } }
                }
              }
            }
          }
        }
        """;

    public static TheoryData<string> Converters => ["admin-api", "admin-operate", "admin-connect-import"];

    [UnitTheory]
    [MemberData(nameof(Converters))]
    public void ConvertSchema_CarriesNullableFromDeclarationReferenceAndWrapper(string converter)
    {
        using var document = JsonDocument.Parse(Document);
        var root = document.RootElement;
        var request = root.GetProperty("components").GetProperty("schemas").GetProperty("Request");

        var schema = Convert(converter, root, request);

        schema.AllowsNull.Should().BeFalse();
        schema.Properties["direct"].AllowsNull.Should().BeTrue();
        schema.Properties["referenced"].AllowsNull.Should().BeTrue();
        schema.Properties["wrapped"].AllowsNull.Should().BeTrue();
        schema.Properties["plain"].AllowsNull.Should().BeFalse();
        schema.Properties["choice"].AllowsNull.Should().BeTrue();
        schema.Properties["choice"].EnumValues.Should().Equal("survey", null);
        schema.Properties["list"].AllowsNull.Should().BeFalse();
        schema.Properties["list"].Items!.AllowsNull.Should().BeTrue();
    }

    [UnitTest]
    public void Serialize_OmitsAllowsNullUnlessSet()
    {
        var plain = JsonSerializer.Serialize(new WorkflowSchemaDefinition { Type = WorkflowSchemaValueType.Text },
            WorkflowPackagesJsonContext.Default.WorkflowSchemaDefinition);
        var nullable = JsonSerializer.Serialize(new WorkflowSchemaDefinition { Type = WorkflowSchemaValueType.Text, AllowsNull = true },
            WorkflowPackagesJsonContext.Default.WorkflowSchemaDefinition);

        plain.Should().NotContainEquivalentOf("allowsNull");
        nullable.Should().ContainEquivalentOf("\"allowsNull\":true");
    }

    [UnitTheory]
    [InlineData("null", """["string","null"]""", true)]
    [InlineData("\"folder\"", """["string","null"]""", true)]
    [InlineData("5", """["string","null"]""", false)]
    [InlineData("null", "\"string\"", false)]
    [InlineData("\"folder\"", "\"string\"", true)]
    [InlineData("null", "\"null\"", true)]
    public void MatchesDeclaredType_AcceptsSingleTypesAndUnions(string value, string declared, bool expected)
    {
        using var valueDocument = JsonDocument.Parse(value);
        using var declaredDocument = JsonDocument.Parse(declared);

        ProposalEvidenceVerifier.MatchesDeclaredType(valueDocument.RootElement, declaredDocument.RootElement)
            .Should().Be(expected);
    }

    private static WorkflowSchemaDefinition Convert(string converter, JsonElement root, JsonElement schema) => converter switch
    {
        "admin-api" => AdminApiOperationCatalog.ConvertSchema(root, schema),
        "admin-operate" => AdminOperateOperationCatalog.ConvertSchema(root, schema),
        _ => AdminConnectImportOperationCatalog.ConvertSchema(root, schema),
    };
}
