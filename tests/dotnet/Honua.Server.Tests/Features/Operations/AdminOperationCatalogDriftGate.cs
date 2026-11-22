// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Operations.Services;
using Honua.Server.Features.Operations;

namespace Honua.Server.Tests.Features.OperationsToolset;

internal static class AdminOperationCatalogDriftGate
{
    public sealed record CatalogSlice(
        string Name,
        IReadOnlyList<IAdminHttpOperationDefinition> Definitions,
        IReadOnlyList<OperationDescriptor> Descriptors);

    public sealed record EvaluationContext(
        JsonElement Contract,
        IReadOnlyList<CatalogSlice> Slices,
        IReadOnlyDictionary<string, int>? ExecutorCounts = null,
        IReadOnlyDictionary<string, int>? MapperCounts = null);

    public static IReadOnlySet<string> DirectApprovalExceptions { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "admin.import.upload-url",
        "admin.metadata.prevalidate",
    };

    public static IReadOnlyList<CatalogSlice> ShippedSlices() =>
    [
        new("api", AdminApiOperationCatalog.Definitions.Cast<IAdminHttpOperationDefinition>().ToArray(), AdminApiOperationCatalog.Descriptors),
        new("operate", AdminOperateOperationCatalog.Definitions.Cast<IAdminHttpOperationDefinition>().ToArray(), AdminOperateOperationCatalog.Descriptors),
        new("access", AdminAccessOperationCatalog.Definitions.Cast<IAdminHttpOperationDefinition>().ToArray(), AdminAccessOperationCatalog.Descriptors),
        new("connect-import", AdminConnectImportOperationCatalog.Definitions.Cast<IAdminHttpOperationDefinition>().ToArray(), AdminConnectImportOperationCatalog.Descriptors),
    ];

    public static IReadOnlyList<string> Evaluate(EvaluationContext context)
    {
        var findings = new List<string>();
        var operations = AdminAccessOperationDriftGate.ReadOperations(context.Contract)
            .ToDictionary(static op => op.OperationId, StringComparer.Ordinal);
        var allDefinitions = context.Slices.SelectMany(static slice => slice.Definitions).ToArray();

        CheckGlobalUniqueness(allDefinitions, findings);

        foreach (var slice in context.Slices)
        {
            var descriptorMap = IndexDescriptors(slice, findings);
            foreach (var definition in slice.Definitions)
            {
                EvaluateDefinition(definition, descriptorMap, operations, context, findings);
            }
        }

        return findings;
    }

    private static void CheckGlobalUniqueness(
        IReadOnlyList<IAdminHttpOperationDefinition> definitions,
        List<string> findings)
    {
        foreach (var group in definitions.GroupBy(static d => d.OperationId, StringComparer.Ordinal).Where(static g => g.Count() > 1))
        {
            findings.Add($"duplicate: operationId '{group.Key}' is registered {group.Count()} times across admin catalogs.");
        }

        foreach (var group in definitions.GroupBy(static d => d.OpenApiOperationId, StringComparer.Ordinal).Where(static g => g.Count() > 1))
        {
            findings.Add($"duplicate: openApiOperationId '{group.Key}' is registered {group.Count()} times across admin catalogs.");
        }

        foreach (var group in definitions.GroupBy(static d => $"{d.Method.Method.ToUpperInvariant()} {d.Path}", StringComparer.Ordinal).Where(static g => g.Count() > 1))
        {
            findings.Add($"duplicate: route '{group.Key}' is registered {group.Count()} times across admin catalogs.");
        }
    }

    private static Dictionary<string, OperationDescriptor> IndexDescriptors(CatalogSlice slice, List<string> findings)
    {
        var map = new Dictionary<string, OperationDescriptor>(StringComparer.Ordinal);
        foreach (var group in slice.Descriptors.GroupBy(static d => d.OperationId, StringComparer.Ordinal))
        {
            if (group.Count() > 1)
            {
                findings.Add($"duplicate: descriptor '{group.Key}' appears {group.Count()} times in '{slice.Name}'.");
            }

            map[group.Key] = group.First();
        }

        return map;
    }

    private static void EvaluateDefinition(
        IAdminHttpOperationDefinition definition,
        Dictionary<string, OperationDescriptor> descriptors,
        Dictionary<string, AdminAccessOperationDriftGate.OpenApiOperation> operations,
        EvaluationContext context,
        List<string> findings)
    {
        if (!operations.TryGetValue(definition.OpenApiOperationId, out var contractOp))
        {
            findings.Add($"unknown: '{definition.OperationId}' references missing OpenAPI operation '{definition.OpenApiOperationId}'.");
            return;
        }

        if (!string.Equals(definition.Method.Method, contractOp.Method, StringComparison.OrdinalIgnoreCase))
        {
            findings.Add($"method-drift: '{definition.OperationId}' declares {definition.Method.Method}, contract declares {contractOp.Method}.");
        }

        if (!string.Equals(NormalizePath(definition.Path), NormalizePath(contractOp.Path), StringComparison.Ordinal))
        {
            findings.Add($"path-drift: '{definition.OperationId}' declares '{definition.Path}', contract declares '{contractOp.Path}'.");
        }

        if (!descriptors.TryGetValue(definition.OperationId, out var descriptor))
        {
            findings.Add($"missing-descriptor: '{definition.OperationId}' has no published descriptor.");
            return;
        }

        EvaluatePolicyAndScope(definition, descriptor, contractOp, findings);
        EvaluateCompensation(descriptor, findings);
        EvaluateRuntimeRegistrations(descriptor, context, findings);
    }

    private static void EvaluatePolicyAndScope(
        IAdminHttpOperationDefinition definition,
        OperationDescriptor descriptor,
        AdminAccessOperationDriftGate.OpenApiOperation contractOp,
        List<string> findings)
    {
        var sideEffect = descriptor.Policy.SideEffectClass;
        if (contractOp.Method == "GET" && sideEffect != OperationSideEffectClass.ReadOnly)
        {
            findings.Add($"policy-drift: GET operation '{definition.OperationId}' is classified as {sideEffect}.");
        }

        if (sideEffect != OperationSideEffectClass.ReadOnly &&
            descriptor.ApprovalModel != OperationApprovalModel.OperatorGate &&
            !DirectApprovalExceptions.Contains(definition.OperationId))
        {
            findings.Add($"ungated: mutation '{definition.OperationId}' bypasses OperatorGate.");
        }

        if (!OperationScopeMapping.TryResolve(new OperationRequest { OperationId = definition.OperationId }, out var scope))
        {
            findings.Add($"scope-drift: '{definition.OperationId}' has no canonical OAuth scope mapping.");
        }
        else if ((sideEffect == OperationSideEffectClass.ReadOnly) != (scope == OperatorOperation.Read))
        {
            findings.Add($"scope-drift: '{definition.OperationId}' ({sideEffect}) maps to OAuth operation {scope}.");
        }
    }

    private static void EvaluateCompensation(OperationDescriptor descriptor, List<string> findings)
    {
        var compensation = descriptor.Compensation;
        if (descriptor.Policy.SideEffectClass == OperationSideEffectClass.ReadOnly)
        {
            if (compensation.Availability != OperationCompensationAvailability.NotApplicable)
            {
                findings.Add($"compensation-drift: read-only '{descriptor.OperationId}' declares {compensation.Availability}.");
            }

            return;
        }

        var expected = descriptor.OperationId == "service.move"
            ? OperationCompensationAvailability.Supported
            : OperationCompensationAvailability.Unavailable;
        if (compensation.Availability != expected)
        {
            findings.Add($"compensation-drift: mutation '{descriptor.OperationId}' declares {compensation.Availability}, expected {expected}.");
        }

        if (string.IsNullOrWhiteSpace(compensation.Reason))
        {
            findings.Add($"compensation-drift: mutation '{descriptor.OperationId}' has an empty compensation reason.");
        }
    }

    private static void EvaluateRuntimeRegistrations(
        OperationDescriptor descriptor,
        EvaluationContext context,
        List<string> findings)
    {
        if (context.ExecutorCounts is not null &&
            (!context.ExecutorCounts.TryGetValue(descriptor.OperationId, out var executorCount) || executorCount != 1))
        {
            findings.Add($"executor-drift: '{descriptor.OperationId}' has {executorCount} registered executor(s); expected 1.");
        }

        if (context.MapperCounts is not null &&
            descriptor.ApprovalModel == OperationApprovalModel.OperatorGate &&
            (!context.MapperCounts.TryGetValue(descriptor.OperationId, out var mapperCount) || mapperCount != 1))
        {
            findings.Add($"mapper-drift: gated '{descriptor.OperationId}' has {mapperCount} approval mapper(s); expected 1.");
        }
    }

    private static string NormalizePath(string path)
    {
        var segments = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length; i++)
        {
            if (segments[i].StartsWith('{') && segments[i].EndsWith('}'))
            {
                segments[i] = "{}";
            }
        }

        return "/" + string.Join('/', segments);
    }
}
