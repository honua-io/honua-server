// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Server.Features.Geoprocessing;
using Honua.Server.Features.Mcp.Models;

namespace Honua.Server.Features.Mcp.Tools;

/// <summary>
/// Shared conversion and serialization helpers for MCP tools.
/// </summary>
internal static class McpToolHelpers
{
    /// <summary>
    /// Converts an MCP plan input into the canonical domain
    /// <see cref="AnalysisPlan"/>. Enum parsing is strict: unsupported step
    /// kinds or artifact kinds produce a
    /// <see cref="GeoprocessingValidationException"/> so tools surface an
    /// <c>invalid_argument</c> error rather than silently dropping fields.
    /// </summary>
    public static AnalysisPlan ToDomainPlan(McpPlanInput? input)
    {
        if (input is null)
        {
            throw new GeoprocessingValidationException("Plan is required.");
        }

        var steps = new List<AnalysisPlanStep>(input.Steps?.Count ?? 0);
        if (input.Steps != null)
        {
            foreach (var step in input.Steps)
            {
                steps.Add(ToDomainStep(step));
            }
        }

        var outputs = new List<ArtifactKind>(input.Outputs?.Count ?? 0);
        if (input.Outputs != null)
        {
            foreach (var output in input.Outputs)
            {
                if (!Enum.TryParse<ArtifactKind>(output, ignoreCase: true, out var parsed))
                {
                    throw new GeoprocessingValidationException(
                        $"Unsupported artifact kind '{output}'.");
                }

                outputs.Add(parsed);
            }
        }

        return new AnalysisPlan
        {
            PlanId = input.PlanId ?? string.Empty,
            IntentId = input.IntentId ?? string.Empty,
            Steps = steps,
            Outputs = outputs,
            Warnings = input.Warnings ?? []
        };
    }

    private static AnalysisPlanStep ToDomainStep(McpPlanStepInput step)
    {
        if (!Enum.TryParse<AnalysisPlanStepKind>(step.Kind, ignoreCase: true, out var kind))
        {
            throw new GeoprocessingValidationException(
                $"Step '{step.StepId}' has unsupported step kind '{step.Kind}'.");
        }

        return new AnalysisPlanStep
        {
            StepId = step.StepId ?? string.Empty,
            Kind = kind,
            ProcessId = step.ProcessId,
            Inputs = step.Inputs ?? new Dictionary<string, string>(),
            DependsOn = step.DependsOn ?? []
        };
    }

    /// <summary>
    /// Serializes <paramref name="value"/> to a JSON string, then into a
    /// <see cref="JsonElement"/> so it can be embedded as
    /// <see cref="McpToolsCallResult.StructuredContent"/> and printed as the
    /// <c>text</c> content block alongside.
    /// </summary>
    public static (JsonElement element, string text) SerializeStructured<T>(
        T value, JsonTypeInfo<T> typeInfo)
    {
        var json = JsonSerializer.Serialize(value, typeInfo);
        using var document = JsonDocument.Parse(json);
        return (document.RootElement.Clone(), json);
    }

    /// <summary>
    /// Builds the <see cref="McpToolsCallResult"/> for a successful tool call
    /// with structured JSON output plus a text block containing the same data.
    /// </summary>
    public static McpToolsCallResult SuccessResult<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        var (element, text) = SerializeStructured(value, typeInfo);
        return new McpToolsCallResult
        {
            IsError = false,
            StructuredContent = element,
            Content =
            [
                new McpContentBlock { Type = "text", Text = text }
            ]
        };
    }

    /// <summary>
    /// Builds the <see cref="McpToolsCallResult"/> for a tool execution failure
    /// with <c>isError: true</c> per MCP 2025-03-26. The canonical error envelope
    /// is mirrored in <c>structuredContent</c> so clients can drive retry,
    /// re-auth, and approval flows without parsing the human-readable text block.
    /// </summary>
    public static McpToolsCallResult ErrorResult(Exception exception)
    {
        var jsonRpcError = McpErrorMapper.Map(exception);
        var output = new McpToolErrorOutput
        {
            Status = "error",
            Code = jsonRpcError.Data?.Code ?? McpErrorMapper.Codes.Internal,
            Message = jsonRpcError.Message,
            RequiresReauthentication = jsonRpcError.Data?.RequiresReauthentication,
            ApprovalRequired = jsonRpcError.Data?.ApprovalRequired,
            PolicyRef = jsonRpcError.Data?.PolicyRef,
            ConflictingJobId = jsonRpcError.Data?.ConflictingJobId,
            Retryable = jsonRpcError.Data?.Retryable,
            Violations = jsonRpcError.Data?.Violations
        };

        var (element, text) = SerializeStructured(output, McpJsonContext.Default.McpToolErrorOutput);
        return new McpToolsCallResult
        {
            IsError = true,
            StructuredContent = element,
            Content =
            [
                new McpContentBlock { Type = "text", Text = text }
            ]
        };
    }

    /// <summary>
    /// Parses the raw <c>arguments</c> payload into a typed model using the
    /// supplied type info. Throws <see cref="GeoprocessingValidationException"/>
    /// so validation failures flow through
    /// <see cref="McpErrorMapper"/> consistently with domain-level errors.
    /// </summary>
    public static T ParseArguments<T>(JsonElement? arguments, JsonTypeInfo<T> typeInfo) where T : class
    {
        if (arguments is null || arguments.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            throw new GeoprocessingValidationException("Tool arguments are required.");
        }

        try
        {
            var result = arguments.Value.Deserialize(typeInfo);
            if (result is null)
            {
                throw new GeoprocessingValidationException("Tool arguments could not be deserialized.");
            }

            return result;
        }
        catch (JsonException ex)
        {
            throw new GeoprocessingValidationException(
                $"Tool arguments are not valid JSON: {ex.Message}");
        }
    }
}
