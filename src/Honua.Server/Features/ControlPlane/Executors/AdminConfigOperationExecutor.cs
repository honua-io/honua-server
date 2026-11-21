// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;

namespace Honua.ControlPlane.Executors;

internal sealed class AdminConfigOperationExecutor(IAdminConfigChangeApplier applier) : IOperationExecutor
{
    public OperationClass OperationClass => OperationClass.AdminConfigChange;

    public Task<OperationProposalPlan?> PlanAsync(
        OperationGatewayRequest request,
        CancellationToken cancellationToken = default)
    {
        var (diff, blockingReasons, parsedAction) = BuildServerOwnedDiff(request.ExecutionPayload);
        var plan = new OperationProposalPlan
        {
            Summary = request.Reason ?? (parsedAction is null ? "Admin configuration change" : $"Admin config action '{parsedAction}'"),
            Diff = diff,
            BlockingReasons = blockingReasons,
            RiskLevel = ProposalRiskLevel.Medium,
            ExecutionPayload = request.ExecutionPayload,
        };

        return Task.FromResult<OperationProposalPlan?>(plan);
    }

    public Task<string?> ExecuteAsync(
        OperationGatewayRequest request,
        string? executionPayload,
        CancellationToken cancellationToken = default)
        => applier.ApplyAsync(executionPayload, cancellationToken);

    internal static (List<string> Diff, List<string> BlockingReasons, string? Action) BuildServerOwnedDiff(string? executionPayload)
    {
        var diff = new List<string>();
        var blockingReasons = new List<string>();
        if (string.IsNullOrWhiteSpace(executionPayload))
        {
            return (diff, blockingReasons, null);
        }

        try
        {
            using var document = JsonDocument.Parse(executionPayload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                blockingReasons.Add("Admin configuration execution payload must be a JSON object.");
                return (diff, blockingReasons, null);
            }

            string? actionName = null;
            if (root.TryGetProperty("action", out var actionElement) &&
                actionElement.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(actionElement.GetString()))
            {
                actionName = actionElement.GetString()!.Trim();
                diff.Add($"action: {actionName}");
            }

            if (root.TryGetProperty("target", out var targetElement) &&
                targetElement.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(targetElement.GetString()))
            {
                diff.Add($"target: {targetElement.GetString()!.Trim()}");
            }

            if (root.TryGetProperty("params", out var paramsElement) && paramsElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in paramsElement.EnumerateObject().OrderBy(static p => p.Name, StringComparer.Ordinal))
                {
                    diff.Add($"param.{property.Name}: {FormatParameterValue(property.Name, property.Value)}");
                }
            }
            else
            {
                foreach (var property in root.EnumerateObject()
                    .Where(static p => !string.Equals(p.Name, "action", StringComparison.Ordinal) &&
                        !string.Equals(p.Name, "target", StringComparison.Ordinal))
                    .OrderBy(static p => p.Name, StringComparer.Ordinal))
                {
                    diff.Add($"param.{property.Name}: {FormatParameterValue(property.Name, property.Value)}");
                }
            }

            return (diff, blockingReasons, actionName);
        }
        catch (JsonException)
        {
            blockingReasons.Add("Admin configuration execution payload is not valid JSON.");
            return (diff, blockingReasons, null);
        }
    }

    private static string FormatParameterValue(string name, JsonElement value)
    {
        if (IsSensitiveParameterName(name))
        {
            return "[REDACTED]";
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "null",
            _ => value.GetRawText(),
        };
    }

    internal static bool IsSensitiveParameterName(string name)
        => name.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || name.Contains("password", StringComparison.OrdinalIgnoreCase)
            || name.Contains("token", StringComparison.OrdinalIgnoreCase)
            || name.Contains("apikey", StringComparison.OrdinalIgnoreCase)
            || name.Contains("api_key", StringComparison.OrdinalIgnoreCase)
            || name.Contains("authorization", StringComparison.OrdinalIgnoreCase)
            || name.Contains("credential", StringComparison.OrdinalIgnoreCase)
            || name.Contains("privatekey", StringComparison.OrdinalIgnoreCase)
            || name.Contains("private_key", StringComparison.OrdinalIgnoreCase);
}
