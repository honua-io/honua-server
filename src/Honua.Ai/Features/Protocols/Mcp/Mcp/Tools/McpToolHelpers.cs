// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;
using Honua.Ai.Protocols.Mcp.Models;

namespace Honua.Ai.Protocols.Mcp.Tools;

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
    /// Null entries inside <c>steps</c>, <c>outputs</c>, <c>warnings</c>, and
    /// each step's <c>dependsOn</c>/<c>inputs</c> collections are rejected
    /// with a validation error so malformed payloads like
    /// <c>{"steps":[null]}</c> surface as <c>invalid_argument</c> instead of
    /// turning into a server-side <c>NullReferenceException</c> that the
    /// <c>tools/call</c> envelope would otherwise render as an internal error.
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
    /// Serialized-text size (characters) at or above which the <c>text</c>
    /// content block stops duplicating the full structured payload and instead
    /// carries a one-line, information-bearing summary. The MCP spec says a
    /// result that carries <c>structuredContent</c> SHOULD also include
    /// functionally-equivalent <c>text</c> for backwards compatibility; small
    /// payloads honor that literally (the compact JSON is cheap), while larger
    /// payloads honor it with an informative summary so the same data is not
    /// billed twice per tool call (honua-server MCP A1 token efficiency).
    /// ~1&#160;KB keeps validate/dry-run/execute/cancel/count-only results inline
    /// verbatim and only summarizes the genuinely large ones (feature
    /// collections, catalogs, generated packages).
    /// </summary>
    public const int InlineTextThresholdChars = 1024;

    /// <summary>
    /// Builds the <see cref="McpToolsCallResult"/> for a successful tool call.
    /// The full payload always travels in <see cref="McpToolsCallResult.StructuredContent"/>
    /// (validated against the tool's <c>outputSchema</c>); the <c>text</c> block
    /// carries the compact JSON verbatim when it is small
    /// (&lt; <see cref="InlineTextThresholdChars"/>) and otherwise a one-line
    /// information-bearing summary from <paramref name="summarize"/> (counts,
    /// ids, next-step hints — never a bare "Success"), avoiding the ~2x token
    /// cost of double-encoding large results. Callers with large payloads SHOULD
    /// pass <paramref name="summarize"/>; when omitted, a generic size-bearing
    /// summary is emitted that still points the reader at
    /// <c>structuredContent</c>.
    /// </summary>
    public static McpToolsCallResult SuccessResult<T>(
        T value,
        JsonTypeInfo<T> typeInfo,
        Func<T, string>? summarize = null)
    {
        var (element, text) = SerializeStructured(value, typeInfo);
        var contentText = text.Length < InlineTextThresholdChars
            ? text
            : (summarize?.Invoke(value) ?? DefaultLargePayloadSummary(text));

        return new McpToolsCallResult
        {
            IsError = false,
            StructuredContent = element,
            Content =
            [
                new McpContentBlock { Type = "text", Text = contentText }
            ]
        };
    }

    /// <summary>
    /// Builds a successful tool result from an already serialized JSON payload.
    /// Used by adapter tools that project an existing server wire response
    /// without owning the response DTO type.
    /// </summary>
    public static McpToolsCallResult SuccessJsonElement(
        JsonElement value,
        Func<JsonElement, string>? summarize = null)
    {
        var element = value.Clone();
        var text = element.GetRawText();
        var contentText = text.Length < InlineTextThresholdChars
            ? text
            : (summarize?.Invoke(element) ?? DefaultLargePayloadSummary(text));

        return new McpToolsCallResult
        {
            IsError = false,
            StructuredContent = element,
            Content =
            [
                new McpContentBlock { Type = "text", Text = contentText }
            ]
        };
    }

    /// <summary>
    /// Fallback summary for a large structured payload whose caller supplied no
    /// bespoke summarizer. Information-bearing (serialized size + where the data
    /// lives) rather than an empty acknowledgement so a reader still learns the
    /// shape/cost of the elided text.
    /// </summary>
    private static string DefaultLargePayloadSummary(string serializedJson) =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"Structured result ({serializedJson.Length:N0} chars) returned in structuredContent; text summary elided to conserve context.");

    /// <summary>
    /// Builds the <see cref="McpToolsCallResult"/> for a tool execution failure
    /// with <c>isError: true</c> per MCP 2025-03-26. The canonical error envelope
    /// is mirrored in <c>structuredContent</c> so clients can drive retry,
    /// re-auth, and approval flows without parsing the human-readable text block.
    /// </summary>
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
            ConflictingJobId = jsonRpcError.Data?.ConflictingJobId,
            Retryable = jsonRpcError.Data?.Retryable,
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
        GeoprocessingStoreUnavailableException => "ExecutionFailed",
        GeoprocessingIdempotencyConflictException => "Conflict",
        _ when jsonRpcError.Data?.Code == McpErrorMapper.Codes.NotFound => "UnknownDataset",
        _ when jsonRpcError.Data?.Code == McpErrorMapper.Codes.InvalidArgument => "ValidationFailed",
        _ => "ExecutionFailed"
    };

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

    /// <summary>
    /// Parses optional object arguments. Missing, null, or undefined arguments
    /// produce a default instance so read tools with all-optional filters accept
    /// the MCP-standard empty call shape.
    /// </summary>
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

    /// <summary>
    /// Validates a no-argument tool invocation, accepting the MCP-standard
    /// missing/null argument bag and an empty JSON object.
    /// </summary>
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
