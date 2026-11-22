// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Operations.Services;
using Honua.Server.Features.Operations;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.OperationsToolset;

public sealed class AdminOperationCatalogDriftGateTests : IDisposable
{
    private static readonly JsonElement Contract = AdminAccessOperationDriftGate.LoadAdminOpenApi();
    private readonly TestWebApplicationFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [UnitTest]
    public void AllAdminCatalogs_PassUnifiedDriftGate_WithLiveExecutorsAndMappers()
    {
        using var scope = _factory.Services.CreateScope();
        var executorCounts = scope.ServiceProvider.GetServices<IOperationExecutor>()
            .GroupBy(static executor => executor.OperationId, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.Ordinal);
        var mapperCounts = OperationDescriptorPublication.CountMappings(
            scope.ServiceProvider.GetServices<IOperationApprovalRequestMapper>());

        var findings = AdminOperationCatalogDriftGate.Evaluate(new(
            Contract,
            AdminOperationCatalogDriftGate.ShippedSlices(),
            executorCounts,
            mapperCounts));

        findings.Should().BeEmpty();
    }

    [UnitTest]
    public void DriftGate_FailsWhenCatalogPathDrifts()
    {
        var slices = ReplaceApiDefinition("admin.services.settings.get", d => d with { Path = "/services/{serviceName}/drifted" });

        var findings = AdminOperationCatalogDriftGate.Evaluate(new(Contract, slices));

        findings.Should().ContainSingle(f => f.StartsWith("path-drift:", StringComparison.Ordinal) && f.Contains("admin.services.settings.get", StringComparison.Ordinal));
    }

    [UnitTest]
    public void DriftGate_FailsWhenCatalogMethodDrifts()
    {
        var slices = ReplaceApiDefinition("admin.services.protocols.set", d => d with { Method = HttpMethod.Post });

        var findings = AdminOperationCatalogDriftGate.Evaluate(new(Contract, slices));

        findings.Should().ContainSingle(f => f.StartsWith("method-drift:", StringComparison.Ordinal) && f.Contains("admin.services.protocols.set", StringComparison.Ordinal));
    }

    [UnitTest]
    public void DriftGate_FailsWhenDuplicateOperationIdSpansCatalogs()
    {
        var slices = AdminOperationCatalogDriftGate.ShippedSlices().ToList();
        var duplicate = AdminApiOperationCatalog.Definitions[0];
        slices[1] = slices[1] with { Definitions = [.. slices[1].Definitions, duplicate] };

        var findings = AdminOperationCatalogDriftGate.Evaluate(new(Contract, slices));

        findings.Should().Contain(f => f.StartsWith("duplicate:", StringComparison.Ordinal) && f.Contains(duplicate.OperationId, StringComparison.Ordinal));
    }

    [UnitTest]
    public void DriftGate_FailsWhenMutationBypassesApprovalGate()
    {
        var slices = ReplaceOperateDescriptor(
            "admin.cache.invalidate",
            d => d with { ApprovalModel = OperationApprovalModel.None });

        var findings = AdminOperationCatalogDriftGate.Evaluate(new(Contract, slices));

        findings.Should().ContainSingle(f => f.StartsWith("ungated:", StringComparison.Ordinal) && f.Contains("admin.cache.invalidate", StringComparison.Ordinal));
    }

    [UnitTest]
    public void DriftGate_FailsWhenMutationCompensationIsNotApplicableOrBlank()
    {
        var slices = ReplaceOperateDescriptor(
            "admin.exports.create",
            d => d with { DeclaredCompensation = new OperationCompensationMetadata(OperationCompensationAvailability.NotApplicable, string.Empty) });

        var findings = AdminOperationCatalogDriftGate.Evaluate(new(Contract, slices));

        findings.Should().Contain(f => f.StartsWith("compensation-drift:", StringComparison.Ordinal) && f.Contains("admin.exports.create", StringComparison.Ordinal));
    }

    [UnitTest]
    public void DriftGate_FailsWhenExecutorOrApprovalMapperIsMissingOrDuplicated()
    {
        var slices = AdminOperationCatalogDriftGate.ShippedSlices();
        var allIds = slices.SelectMany(static s => s.Definitions).Select(static d => d.OperationId).ToArray();
        var executorCounts = allIds.ToDictionary(static id => id, static _ => 1, StringComparer.Ordinal);
        executorCounts["admin.services.list"] = 0;
        executorCounts["admin.cache.invalidate"] = 2;

        var mapperCounts = allIds.ToDictionary(static id => id, static _ => 1, StringComparer.Ordinal);
        mapperCounts["admin.exports.delete"] = 0;

        var findings = AdminOperationCatalogDriftGate.Evaluate(new(Contract, slices, executorCounts, mapperCounts));

        findings.Should().Contain(f => f.StartsWith("executor-drift:", StringComparison.Ordinal) && f.Contains("admin.services.list", StringComparison.Ordinal));
        findings.Should().Contain(f => f.StartsWith("executor-drift:", StringComparison.Ordinal) && f.Contains("admin.cache.invalidate", StringComparison.Ordinal));
        findings.Should().Contain(f => f.StartsWith("mapper-drift:", StringComparison.Ordinal) && f.Contains("admin.exports.delete", StringComparison.Ordinal));
    }

    [UnitTest]
    public void MutationInventory_CoversEveryAdminNonGetOperationBidirectionally()
    {
        var projectedOpenApiIds = AdminOperationCatalogDriftGate.ShippedSlices()
            .SelectMany(static s => s.Definitions)
            .Select(static d => d.OpenApiOperationId)
            .ToHashSet(StringComparer.Ordinal);

        var findings = AdminMutationInventory.Evaluate(Contract, projectedOpenApiIds, AdminMutationInventory.UnprojectedNonGet);

        findings.Should().BeEmpty();
    }

    [UnitTest]
    public void MutationInventory_FailsWhenContractAddsUnclassifiedMutation()
    {
        var mutated = JsonNode.Parse(Contract.GetRawText())!;
        mutated["paths"]!["/services/{serviceName}/untracked-action"] = JsonNode.Parse(
            """{"post":{"tags":["Services"],"operationId":"runUntrackedServiceAction","responses":{"200":{"description":"Done."}}}}""");
        using var document = JsonDocument.Parse(mutated.ToJsonString());
        var projected = AdminOperationCatalogDriftGate.ShippedSlices()
            .SelectMany(static s => s.Definitions)
            .Select(static d => d.OpenApiOperationId)
            .ToHashSet(StringComparer.Ordinal);

        var findings = AdminMutationInventory.Evaluate(document.RootElement, projected, AdminMutationInventory.UnprojectedNonGet);

        findings.Should().ContainSingle(f => f.StartsWith("unclassified:", StringComparison.Ordinal) && f.Contains("runUntrackedServiceAction", StringComparison.Ordinal));
    }

    [UnitTest]
    public void MutationInventory_FailsWhenStaleOrDuplicateEntryExists()
    {
        var projected = AdminOperationCatalogDriftGate.ShippedSlices()
            .SelectMany(static s => s.Definitions)
            .Select(static d => d.OpenApiOperationId)
            .ToHashSet(StringComparer.Ordinal);
        var tampered = AdminMutationInventory.UnprojectedNonGet
            .Append(new("nonExistentAdminOp", AdminMutationInventory.Classification.MigrationBacklog, "Stale entry."))
            .Append(new("updateServiceProtocols", AdminMutationInventory.Classification.MigrationBacklog, "Already projected."))
            .ToArray();

        var findings = AdminMutationInventory.Evaluate(Contract, projected, tampered);

        findings.Should().Contain(f => f.StartsWith("stale-inventory:", StringComparison.Ordinal) && f.Contains("nonExistentAdminOp", StringComparison.Ordinal));
        findings.Should().Contain(f => f.StartsWith("duplicate-classification:", StringComparison.Ordinal) && f.Contains("updateServiceProtocols", StringComparison.Ordinal));
    }

    private static List<AdminOperationCatalogDriftGate.CatalogSlice> ReplaceApiDefinition(
        string operationId,
        Func<AdminApiOperationCatalog.Definition, AdminApiOperationCatalog.Definition> mutate)
    {
        var slices = AdminOperationCatalogDriftGate.ShippedSlices().ToList();
        var updated = AdminApiOperationCatalog.Definitions
            .Select(d => d.OperationId == operationId ? mutate(d) : d)
            .Cast<IAdminHttpOperationDefinition>()
            .ToArray();
        slices[0] = slices[0] with { Definitions = updated };
        return slices;
    }

    private static List<AdminOperationCatalogDriftGate.CatalogSlice> ReplaceOperateDescriptor(
        string operationId,
        Func<OperationDescriptor, OperationDescriptor> mutate)
    {
        var slices = AdminOperationCatalogDriftGate.ShippedSlices().ToList();
        var updated = slices[1].Descriptors
            .Select(d => d.OperationId == operationId ? mutate(d) : d)
            .ToArray();
        slices[1] = slices[1] with { Descriptors = updated };
        return slices;
    }
}
