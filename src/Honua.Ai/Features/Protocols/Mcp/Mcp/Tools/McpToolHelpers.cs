// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;
using Honua.Ai.Protocols.Mcp.Models;

namespace Honua.Ai.Protocols.Mcp.Tools;

internal static class McpToolHelpers
{
    public static AnalysisPlan ToDomainPlan(McpPlanInput? input)
    {
        if (input is null)
        {
            throw new GeoprocessingValidationException("Plan is required.");
        }

        var steps = new List<AnalysisPlanStep>(input.Steps?.Count ?? 0);
        if (input.Steps != null)
        {
            for (var i = 0; i < input.Steps.Count; i++)
            {
                var step = input.Steps[i];
                if (step is null)
                {
                    throw new GeoprocessingValidationException(
                        $"Plan step at index {i} is null; each entry must be a step object.");
                }

                steps.Add(ToDomainStep(step, i));
            }
        }

        var outputs = new List<ArtifactKind>(input.Outputs?.Count ?? 0);
        if (input.Outputs != null)
        {
            for (var i = 0; i < input.Outputs.Count; i++)
            {
                var output = input.Outputs[i];
                if (output is null)
                {
                    throw new GeoprocessingValidationException(
                        $"Plan output at index {i} is null; each entry must be an artifact kind string.");
                }

                if (!Enum.TryParse<ArtifactKind>(output, ignoreCase: true, out var parsed))
                {
                    throw new GeoprocessingValidationException(
                        $"Unsupported artifact kind '{output}'.");
                }

                outputs.Add(parsed);
            }
        }

        IReadOnlyList<string> warnings;
        if (input.Warnings is null)
        {
            warnings = [];
        }
        else
        {
            for (var i = 0; i < input.Warnings.Count; i++)
            {
                if (input.Warnings[i] is null)
                {
                    throw new GeoprocessingValidationException(
                        $"Plan warning at index {i} is null; each entry must be a string.");
                }
            }

            warnings = input.Warnings;
        }

        return new AnalysisPlan
        {
            PlanId = input.PlanId ?? string.Empty,
            IntentId = input.IntentId ?? string.Empty,
            Steps = steps,
            Outputs = outputs,
            Warnings = warnings
        };
    }

    private static AnalysisPlanStep ToDomainStep(McpPlanStepInput step, int index)
    {
        if (!Enum.TryParse<AnalysisPlanStepKind>(step.Kind, ignoreCase: true, out var kind))
        {
            throw new GeoprocessingValidationException(
                $"Step '{step.StepId}' has unsupported step kind '{step.Kind}'.");
        }

        IReadOnlyDictionary<string, string> inputs;
        if (step.Inputs is null)
        {
            inputs = new Dictionary<string, string>();
        }
        else
        {
            foreach (var pair in step.Inputs.Where(pair => pair.Value is null))
            {
                throw new GeoprocessingValidationException(
                    $"Step '{step.StepId ?? $"at index {index}"}' has a null value for input '{pair.Key}'.");
            }

            inputs = step.Inputs;
        }

        IReadOnlyList<string> dependsOn;
        if (step.DependsOn is null)
        {
            dependsOn = [];
        }
        else
        {
            for (var i = 0; i < step.DependsOn.Count; i++)
            {
                if (step.DependsOn[i] is null)
                {
                    throw new GeoprocessingValidationException(
                        $"Step '{step.StepId ?? $"at index {index}"}' has a null dependsOn entry at index {i}.");
                }
            }

            dependsOn = step.DependsOn;
        }

        return new AnalysisPlanStep
        {
            StepId = step.StepId ?? string.Empty,
            Kind = kind,
            ProcessId = step.ProcessId,
            Inputs = inputs,
            DependsOn = dependsOn
        };
    }

    public static (JsonElement element, string text) SerializeStructured<T>(
        T value, JsonTypeInfo<T> typeInfo)
    {
        var json = JsonSerializer.Serialize(value, typeInfo);
        using var document = JsonDocument.Parse(json);
        return (document.RootElement.Clone(), json);
    }

    public const int InlineTextThresholdChars = 1024;

    // The serialized JSON is always returned as text because some clients show only content and would hide structuredContent.
    public static McpToolsCallResult SuccessResult<T>(
        T value,
        JsonTypeInfo<T> typeInfo,
        Func<T, string>? summarize = null)
    {
        var (element, text) = SerializeStructured(value, typeInfo);
        var summary = text.Length >= InlineTextThresholdChars ? summarize?.Invoke(value) : null;
        return BuildStructuredSuccess(element, text, summary);
    }

    public static McpToolsCallResult SuccessJsonElement(
        JsonElement value,
        Func<JsonElement, string>? summarize = null)
    {
        var element = value.Clone();
        var text = element.GetRawText();
        var summary = text.Length >= InlineTextThresholdChars ? summarize?.Invoke(element) : null;
        return BuildStructuredSuccess(element, text, summary);
    }

    private static McpToolsCallResult BuildStructuredSuccess(JsonElement element, string serializedJson, string? summary)
    {
        var jsonBlock = new McpContentBlock { Type = "text", Text = serializedJson };
        IReadOnlyList<McpContentBlock> content = string.IsNullOrWhiteSpace(summary)
            ? [jsonBlock]
            : [new McpContentBlock { Type = "text", Text = summary }, jsonBlock];

        return new McpToolsCallResult
        {
            IsError = false,
            StructuredContent = element,
            Content = content
        };
    }

    public static McpToolsCallResult ErrorResult(Exception exception)
    {
        var jsonRpcError = McpErrorMapper.Map(exception);
        var violations = BuildViolations(exception, jsonRpcError);
        var output = new McpToolErrorOutput
        {
            Status = "error",
            Code = jsonRpcError.Data?.Code ?? McpErrorMapper.Codes.Internal,
            Message = jsonRpcError.Message,
            RequiresReauthentication = jsonRpcError.Data?.RequiresReauthentication,
            ApprovalRequired = jsonRpcError.Data?.ApprovalRequired,
            PolicyRef = jsonRpcError.Data?.PolicyRef,
            StudioAuthorizationCode = jsonRpcError.Data?.StudioAuthorizationCode,
            ProposalId = jsonRpcError.Data?.ProposalId,
            ResourceUri = jsonRpcError.Data?.ResourceUri,
            ConflictingJobId = jsonRpcError.Data?.ConflictingJobId,
            CurrentGeneration = jsonRpcError.Data?.CurrentGeneration,
            Retryable = jsonRpcError.Data?.Retryable,
            MissingDependency = jsonRpcError.Data?.MissingDependency,
            MissingEntitlement = jsonRpcError.Data?.MissingEntitlement,
            Capability = jsonRpcError.Data?.Capability,
            Remediation = jsonRpcError.Data?.Remediation,
            RemediationRef = jsonRpcError.Data?.RemediationRef,
            Violations = violations,
            Error = new McpGeoprocessingError
            {
                Kind = ToErrorKind(exception, jsonRpcError),
                Message = jsonRpcError.Message,
                Violations = violations
            }
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

    private static IReadOnlyList<McpValidationViolation>? BuildViolations(Exception exception, McpJsonRpcError jsonRpcError)
    {
        if (jsonRpcError.Data?.Violations is { Count: > 0 } violations)
        {
            return violations;
        }

        return exception is GeoprocessingValidationException
            ?
            [
                new McpValidationViolation
                {
                    Code = "INVALID_ARGUMENT",
                    Message = jsonRpcError.Message
                }
            ]
            : null;
    }

    private static string ToErrorKind(Exception exception, McpJsonRpcError jsonRpcError) => exception switch
    {
        GeoprocessingValidationException => "ValidationFailed",
        GeoprocessingAuthorizationException => "AuthorizationDenied",
        GeoprocessingApprovalRequiredException => "ApprovalRequired",
        GeoprocessingNotFoundException => "UnknownDataset",
        GeoprocessingPreconditionFailedException => "PreconditionFailed",
        Honua.Infrastructure.Rendering.RasterRenderingUnavailableException => "PreconditionFailed",
        // honua-release#202: a refusal that names a dependency the install never composed is a
        // precondition failure, not an execution failure — nothing ever ran. Matches the
        // RasterRenderingUnavailableException precedent above. Adapters that reuse this
        // exception for a transient upstream carry no receipt and keep the previous kind.
        GeoprocessingStoreUnavailableException { HasDependencyReceipt: true } => "PreconditionFailed",
        GeoprocessingStoreUnavailableException => "ExecutionFailed",
        GeoprocessingIdempotencyConflictException => "Conflict",
        _ when jsonRpcError.Data?.Code == McpErrorMapper.Codes.NotFound => "UnknownDataset",
        _ when jsonRpcError.Data?.Code == McpErrorMapper.Codes.InvalidArgument => "ValidationFailed",
        _ => "ExecutionFailed"
    };

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

    public static T ParseOptionalArguments<T>(JsonElement? arguments, JsonTypeInfo<T> typeInfo)
        where T : class, new()
    {
        if (arguments is null || arguments.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return new T();
        }

        if (arguments.Value.ValueKind != JsonValueKind.Object)
        {
            throw new GeoprocessingValidationException("Tool arguments must be a JSON object.");
        }

        try
        {
            return arguments.Value.Deserialize(typeInfo) ?? new T();
        }
        catch (JsonException ex)
        {
            throw new GeoprocessingValidationException(
                $"Tool arguments are not valid JSON: {ex.Message}");
        }
    }

    public static void EnsureNoArguments(JsonElement? arguments)
    {
        if (arguments is null || arguments.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return;
        }

        if (arguments.Value.ValueKind != JsonValueKind.Object)
        {
            throw new GeoprocessingValidationException("Tool arguments must be an empty JSON object.");
        }

        if (arguments.Value.EnumerateObject().Any())
        {
            throw new GeoprocessingValidationException("This tool does not accept arguments.");
        }
    }
}
