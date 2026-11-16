// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Ai.Protocols.Mcp.Tools;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Schema;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

public sealed partial class McpTaxonomyAlignmentTests
{
    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [InlineData("deploy_operations", "deploy_operations_result_posture.json")]
    [InlineData("platform_release_status", "platform_release_status_result_null_serving.json")]
    [InlineData("platform_release_status", "platform_release_status_result_null_execution.json")]
    [InlineData("supported_operation_kinds", "supported_operation_kinds_result.json")]
    public void PlatformOperationResults_CanonicalFixtures_ConformToLiveAndReferenceSchemas(
        string tool, string fixture)
    {
        var payload = ReadFixtureInputs(Path.Join(FixtureRoot, "tools", tool, fixture))!;
        var reference = LoadSchema(Path.Join(SchemaRoot, "tools", $"{tool}.result.schema.json"));
        var liveSchema = tool switch
        {
            "deploy_operations" => McpPlatformOpsSchemas.DeployOperationsOutputSchema,
            "platform_release_status" => McpPlatformOpsSchemas.PlatformReleaseStatusOutputSchema,
            _ => McpPlatformOpsSchemas.SupportedOperationKindsOutputSchema
        };
        var live = LoadSchemaFromJson(liveSchema.GetRawText());

        payload.IsValid(live, out IList<string> liveErrors).Should().BeTrue(string.Join("; ", liveErrors));
        payload.IsValid(reference, out IList<string> referenceErrors).Should().BeTrue(string.Join("; ", referenceErrors));
    }

    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [InlineData("deploy_operations", "deploy_operations_result_posture.json")]
    public void PlatformOperationResults_ReferenceSchemas_RejectUnknownEnvelopeFields(string tool, string fixture)
    {
        var payload = (JObject)ReadFixtureInputs(Path.Join(FixtureRoot, "tools", tool, fixture))!;
        payload["unknownEnvelopeField"] = true;
        var reference = LoadSchema(Path.Join(SchemaRoot, "tools", $"{tool}.result.schema.json"));

        payload.IsValid(reference).Should().BeFalse();
    }
}
