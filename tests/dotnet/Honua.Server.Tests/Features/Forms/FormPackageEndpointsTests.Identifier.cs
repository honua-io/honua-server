// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Honua.Core.Features.Forms.Packages;
using Honua.TestKit;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Forms;

public sealed partial class FormPackageEndpointsTests
{
    [IntegrationTheory]
    [InlineData("../evil")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a' OR '1'='1")]
    [InlineData("bad\r\nX-Injected: 1")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("import")]
    [Operation(Operations.Configuration)]
    [Endpoint("POST /api/v1/admin/forms")]
    public async Task CreateDraft_UnsafeFormId_ReturnsProblemAndPersistsNothing(string formId)
    {
        using var content = JsonContent(PackageJsonWithFormId("Unsafe form id", formId));
        using var response = await _fixture.Client.PostAsync("/api/v1/admin/forms", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        var packages = await GetJsonAsync("/api/v1/admin/forms", FormPackageJsonContext.Default.FormPackageSummaryArray);
        packages.Should().NotContain(summary => summary.FormId == formId || summary.FormId == formId.Trim());
    }

    [IntegrationTest]
    [Operation(Operations.Configuration)]
    [Endpoint("POST /api/v1/admin/forms")]
    public async Task CreateDraft_SafeOrAbsentFormId_IsAccepted()
    {
        var safeId = $"Site_Inspection-{Guid.NewGuid():N}";
        using var content = JsonContent(PackageJsonWithFormId("Safe form id", safeId));
        using var response = await _fixture.Client.PostAsync("/api/v1/admin/forms", content);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var explicitDraft = await ReadJsonAsync(response, FormPackageJsonContext.Default.FormPackageVersion);
        explicitDraft.FormId.Should().Be(safeId);

        var generated = await CreateDraftAsync(CreatePackage("Generated form id"));
        generated.FormId.Should().StartWith("form-");
    }

    private static string PackageJsonWithFormId(string title, string formId)
    {
        var node = JsonNode.Parse(JsonSerializer.Serialize(CreatePackage(title), FormPackageJsonContext.Default.FormPackageDocument))!;
        node["formId"] = formId;
        return node.ToJsonString();
    }
}
