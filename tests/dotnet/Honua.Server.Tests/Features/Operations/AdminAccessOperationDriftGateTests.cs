// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Operations.Services;
using Honua.Server.Features.Operations;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.OperationsToolset;

/// <summary>
/// #3361 acceptance: the lane-C schema and exclusion-roster drift gates pass on the shipped
/// contract and fail on missing, hand-forked, unaudited or unexpectedly published operations.
/// </summary>
public sealed class AdminAccessOperationDriftGateTests
{
    private static readonly JsonElement Contract = AdminAccessOperationDriftGate.LoadAdminOpenApi();

    [UnitTest]
    public async Task Gate_PassesOnShippedContract_CatalogAndRuntimeProjection()
    {
        var findings = AdminAccessOperationDriftGate.Evaluate(
            Contract,
            AdminAccessOperationCatalog.Definitions,
            AdminAccessOperationCatalog.Descriptors,
            AdminMcpOperationExclusions.All,
            await PublishedAccessToolNamesAsync());

        findings.Should().BeEmpty();
    }

    [UnitTest]
    public void Gate_PassesOnCommittedMcpProjectionManifest()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(
            RepositoryPaths.Resolve("docs", "gis", "data", "admin-mcp-projection-manifest.json")));
        var accessIds = AdminAccessOperationCatalog.Definitions
            .Select(static definition => definition.OperationId)
            .ToHashSet(StringComparer.Ordinal);
        var committed = manifest.RootElement.GetProperty("operations").EnumerateArray()
            .Where(operation => accessIds.Contains(operation.GetProperty("operationId").GetString()!))
            .Select(static operation => operation.GetProperty("toolName").GetString()!)
            .ToArray();

        AdminAccessOperationDriftGate.Evaluate(
                Contract,
                AdminAccessOperationCatalog.Definitions,
                AdminAccessOperationCatalog.Descriptors,
                AdminMcpOperationExclusions.All,
                committed)
            .Should().BeEmpty();
    }

    [UnitTest]
    public void Gate_DetectsTheAuditedSecretsFromTheContract_NotFromARoster()
    {
        var access = AdminAccessOperationDriftGate.ReadOperations(Contract)
            .Where(static operation => operation.IsAccessFamily)
            .ToArray();

        access.Should().HaveCount(AdminAccessOperationCatalog.Definitions.Count,
            "every access-tagged Admin OpenAPI operation is projected exactly once");
        access.Where(static operation => operation.ReturnsPlaintextSecret).Select(static operation => operation.OperationId)
            .Should().Contain(["createAdminApiKey", "rotateAdminApiKey", "registerOAuthClient"]);
        access.Where(static operation => operation.AcceptsWriteOnlySecret).Select(static operation => operation.OperationId)
            .Should().Contain(["createOidcProvider", "updateOidcProvider"]);
        access.Where(operation => operation.OperationId is "listAdminApiKeys" or "getAdminApiKeyEffectivePermissions")
            .Should().HaveCount(2).And.OnlyContain(static operation =>
                !operation.ReturnsPlaintextSecret && !operation.AcceptsWriteOnlySecret,
                "the installer credential checks must stay MCP-eligible");
    }

    [UnitTest]
    public async Task Gate_EveryExecutorValidatesExactlyTheContractRequiredInputs()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddAdminAccessOperations();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var executors = scope.ServiceProvider.GetServices<IOperationExecutor>()
            .ToDictionary(static executor => executor.OperationId, StringComparer.Ordinal);
        var contract = AdminAccessOperationDriftGate.ReadOperations(Contract)
            .ToDictionary(static operation => operation.OperationId, StringComparer.Ordinal);

        foreach (var definition in AdminAccessOperationCatalog.Definitions)
        {
            executors.Should().ContainKey(definition.OperationId);
            var validation = await executors[definition.OperationId].ValidateAsync(
                new OperationRequest { OperationId = definition.OperationId });

            var reported = validation.Messages
                .Where(static message => message.StartsWith("Required ", StringComparison.Ordinal))
                .Select(static message => message.Split('\'')[1])
                .ToHashSet(StringComparer.Ordinal);
            reported.Should().BeEquivalentTo(contract[definition.OpenApiOperationId].RequiredInputs,
                "'{0}' must reject exactly the inputs its Admin OpenAPI operation requires", definition.OperationId);
            validation.IsValid.Should().Be(reported.Count == 0);
        }
    }

    [UnitTest]
    public async Task Gate_FailsWhenTheContractGainsAnUncataloguedAccessOperation()
    {
        var mutated = JsonNode.Parse(Contract.GetRawText())!;
        mutated["paths"]!["/roles/{id}/clone"] = JsonNode.Parse(
            """{"post":{"tags":["Roles"],"operationId":"cloneRole","responses":{"200":{"description":"Cloned."}}}}""");
        using var document = JsonDocument.Parse(mutated.ToJsonString());

        var findings = AdminAccessOperationDriftGate.Evaluate(
            document.RootElement,
            AdminAccessOperationCatalog.Definitions,
            AdminAccessOperationCatalog.Descriptors,
            AdminMcpOperationExclusions.All,
            await PublishedAccessToolNamesAsync());

        findings.Should().ContainSingle().Which.Should().StartWith("missing:").And.Contain("cloneRole");
    }

    [UnitTest]
    public async Task Gate_FailsWhenADescriptorIsDropped()
    {
        var findings = AdminAccessOperationDriftGate.Evaluate(
            Contract,
            AdminAccessOperationCatalog.Definitions.Where(static definition => definition.OperationId != "admin.role.delete").ToArray(),
            AdminAccessOperationCatalog.Descriptors,
            AdminMcpOperationExclusions.All,
            await PublishedAccessToolNamesAsync());

        findings.Should().Contain(finding => finding.StartsWith("missing:") && finding.Contains("deleteRole"));
    }

    [UnitTest]
    public async Task Gate_FailsWhenADescriptorSchemaIsHandForked()
    {
        var descriptors = AdminAccessOperationCatalog.Descriptors
            .Select(static descriptor => descriptor.OperationId == "admin.api-key.effective-permissions"
                ? descriptor with { InputSchema = [] }
                : descriptor)
            .ToArray();

        var findings = AdminAccessOperationDriftGate.Evaluate(
            Contract,
            AdminAccessOperationCatalog.Definitions,
            descriptors,
            AdminMcpOperationExclusions.All,
            await PublishedAccessToolNamesAsync());

        findings.Should().Contain(finding =>
            finding.StartsWith("hand-forked:") && finding.Contains("admin.api-key.effective-permissions") && finding.Contains("input schema"));
    }

    [UnitTest]
    public async Task Gate_FailsWhenADescriptorRouteIsHandForked()
    {
        var definitions = AdminAccessOperationCatalog.Definitions
            .Select(static definition => definition.OperationId == "admin.user.roles.update"
                ? definition with { Path = "/users/{id}/role-assignments" }
                : definition)
            .ToArray();

        var findings = AdminAccessOperationDriftGate.Evaluate(
            Contract,
            definitions,
            AdminAccessOperationCatalog.Descriptors,
            AdminMcpOperationExclusions.All,
            await PublishedAccessToolNamesAsync());

        findings.Should().ContainSingle().Which.Should().StartWith("hand-forked:").And.Contain("/users/{id}/role-assignments");
    }

    [UnitTest]
    public async Task Gate_FailsWhenAMutationBypassesTheApprovalGate()
    {
        var definitions = AdminAccessOperationCatalog.Definitions
            .Select(static definition => definition.OperationId == "admin.tenant.delete"
                ? definition with { ApprovalModel = OperationApprovalModel.None }
                : definition)
            .ToArray();
        var descriptors = definitions
            .Select(definition => AdminOperateOperationCatalog.BuildDescriptor(Contract, definition))
            .ToArray();

        var findings = AdminAccessOperationDriftGate.Evaluate(
            Contract, definitions, descriptors, AdminMcpOperationExclusions.All, await PublishedAccessToolNamesAsync());

        findings.Should().ContainSingle().Which.Should().StartWith("ungated:").And.Contain("admin.tenant.delete");
    }

    [UnitTest]
    public async Task Gate_FailsWhenAnAuditedExclusionIsPublished()
    {
        var published = (await PublishedAccessToolNamesAsync()).Append("honua_admin_api_key_create");

        var findings = AdminAccessOperationDriftGate.Evaluate(
            Contract,
            AdminAccessOperationCatalog.Definitions,
            AdminAccessOperationCatalog.Descriptors,
            AdminMcpOperationExclusions.All,
            published);

        findings.Should().ContainSingle().Which.Should().StartWith("unexpectedly-published:").And.Contain("honua_admin_api_key_create");
    }

    [UnitTest]
    public async Task Gate_FailsWhenAnEligibleOperationDisappearsFromTheProjection()
    {
        var published = (await PublishedAccessToolNamesAsync()).Where(static name => name != "honua_admin_api_key_list");

        var findings = AdminAccessOperationDriftGate.Evaluate(
            Contract,
            AdminAccessOperationCatalog.Definitions,
            AdminAccessOperationCatalog.Descriptors,
            AdminMcpOperationExclusions.All,
            published);

        findings.Should().ContainSingle().Which.Should().StartWith("unpublished:").And.Contain("honua_admin_api_key_list");
    }

    [UnitTest]
    public async Task Gate_FailsWhenASecretBearingOperationLosesItsExclusion()
    {
        var exclusions = AdminMcpOperationExclusions.All
            .Where(static entry => entry.OpenApiOperationId is not ("registerOAuthClient" or "updateOidcProvider"))
            .ToArray();

        var findings = AdminAccessOperationDriftGate.Evaluate(
            Contract,
            AdminAccessOperationCatalog.Definitions,
            AdminAccessOperationCatalog.Descriptors,
            exclusions,
            await PublishedAccessToolNamesAsync());

        findings.Should().Contain(finding => finding.StartsWith("unaudited-secret:") && finding.Contains("registerOAuthClient"));
        findings.Should().Contain(finding => finding.StartsWith("unaudited-secret:") && finding.Contains("updateOidcProvider"));
    }

    [UnitTest]
    public async Task Gate_FailsWhenAnInstallerReadIsExcluded()
    {
        var exclusions = AdminMcpOperationExclusions.All
            .Append(new AdminMcpOperationExclusions.Entry(
                "listAdminApiKeys",
                "admin.api-key.list",
                "honua_admin_api_key_list",
                AdminMcpOperationExclusions.OneTimeSecretReasonCode,
                "Deliberately wrong exclusion."))
            .ToArray();
        var published = (await PublishedAccessToolNamesAsync()).Where(static name => name != "honua_admin_api_key_list");

        var findings = AdminAccessOperationDriftGate.Evaluate(
            Contract,
            AdminAccessOperationCatalog.Definitions,
            AdminAccessOperationCatalog.Descriptors,
            exclusions,
            published);

        findings.Should().ContainSingle().Which.Should().StartWith("over-excluded:").And.Contain("admin.api-key.list");
    }

    private static async Task<IReadOnlyList<string>> PublishedAccessToolNamesAsync()
    {
        var source = new PublishedOperationToolSource(
            new OperationCatalog([new AdminAccessOperationDescriptorProvider()], TimeProvider.System),
            Options.Create(new McpPublishedOperationOptions { Enabled = true }),
            NullLogger<PublishedOperationToolSource>.Instance,
            requestMappers: AdminAccessOperationCatalog.Definitions
                .Where(static definition => definition.SideEffect != OperationSideEffectClass.ReadOnly)
                .Select(static definition => new AdminOperateOperationApprovalRequestMapper(definition))
                .ToArray());
        return (await source.GetToolsAsync(CancellationToken.None)).Select(static tool => tool.Name).ToArray();
    }
}
