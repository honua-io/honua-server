// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Security.Claims;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Ai.Protocols.Mcp.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Ai.Protocols.Mcp.Tools;

/// <summary>
/// MCP tool that proposes an in-scope mutating control-plane operation through the
/// shared <see cref="IOperationGateway"/>. The model-facing contract always
/// creates an approval proposal and never selects a direct-execution route. The tool
/// returns a structured result carrying a
/// <c>proposalId</c> and the <c>honua://proposals/{id}</c> resource URI so the
/// agent can poll until a human resolves it, rather than failing (#1696).
/// </summary>
internal sealed class ProposeOperationTool : IMcpTool
{
    public const string ToolName = "honua_propose_operation";

    private const string AgentActorPrefix = "agent:";
    private static readonly HashSet<string> DeployPayloadProperties =
    [
        "targetId",
        "desiredRevision",
        "currentRevision",
        "priority",
        "parameterOverrides",
    ];
    private static readonly HashSet<string> MetadataReleasePayloadProperties =
    [
        "action",
        "packageId",
        "targetEnvironment",
        "resourceSemanticId",
        "newFieldName",
        "newFieldType",
        "dataPopulateWorkloadId",
        "scriptId",
    ];
    private readonly ILogger<ProposeOperationTool> _logger;

    public ProposeOperationTool(ILogger<ProposeOperationTool> logger)
    {
        _logger = logger;
    }

    public string Name => ToolName;

    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Lifecycle;

    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Title = "Propose operation",
        Description = "Propose a deploy or metadata-release operation using a validated, resource-bound execution specification. "
            + "Always returns an approval proposal; this model-facing tool never executes an operation directly.",
        InputSchema = McpToolSchemas.ProposeOperationArgumentSchema,
        OutputSchema = McpToolOutputSchemas.ProposeOperationOutputSchema,
        // Write tool: it routes a mutating control-plane operation through the
        // approval gateway. Idempotent because it honors the optional
        // idempotencyKey; not flagged destructive at the propose layer (the
        // underlying operation class governs its own destructiveness).
        Annotations = McpToolAnnotationSets.Write("Propose operation", destructive: false, idempotent: true)
    };

    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("ProposeOperation");
        McpLog.ToolInvoked(_logger, ToolName, WorkflowFamily);

        var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);
        var argument = McpToolHelpers.ParseArguments(arguments, McpJsonContext.Default.McpProposeOperationArgument);

        // Executor-discovery surface (#2563): intersect the live gateway executors with the kinds
        // this generic MCP adapter can represent as validated, resource-bound specifications.
        // Dedicated surfaces own AdminConfigChange, Geoprocess, and Seed.
        var catalog = httpContext.RequestServices.GetService<IOperationExecutorCatalog>();
        var supportedKinds = catalog?.SupportedKinds
            .Where(McpProposableOperationKinds.Contains)
            .Select(supportedKind => supportedKind.ToString())
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        if (string.IsNullOrWhiteSpace(argument.Kind) ||
            !Enum.TryParse<OperationClass>(argument.Kind, ignoreCase: true, out var kind) ||
            !Enum.IsDefined(kind))
        {
            return McpToolHelpers.SuccessResult(
                new McpProposeOperationOutput
                {
                    Outcome = "rejected",
                    RequiresApproval = false,
                    SupportedKinds = supportedKinds,
                    Message = "Unknown or missing operation 'kind'. Expected one of: Deploy, MetadataRelease."
                },
                McpJsonContext.Default.McpProposeOperationOutput);
        }

        if (!McpProposableOperationKinds.Contains(kind))
        {
            return McpToolHelpers.SuccessResult(
                new McpProposeOperationOutput
                {
                    Outcome = "rejected",
                    RequiresApproval = false,
                    SupportedKinds = supportedKinds,
                    Message = $"Operation kind '{kind}' is not safely representable by this generic proposal surface. Use its dedicated operation tool or endpoint."
                },
                McpJsonContext.Default.McpProposeOperationOutput);
        }

        if (supportedKinds is not null && !supportedKinds.Contains(kind.ToString(), StringComparer.Ordinal))
        {
            return McpToolHelpers.SuccessResult(
                new McpProposeOperationOutput
                {
                    Outcome = "rejected",
                    RequiresApproval = false,
                    SupportedKinds = supportedKinds,
                    Message = $"Operation kind '{kind}' has no registered executor in this runtime."
                },
                McpJsonContext.Default.McpProposeOperationOutput);
        }

        if (string.IsNullOrWhiteSpace(argument.ResourceId))
        {
            return McpToolHelpers.SuccessResult(
                new McpProposeOperationOutput
                {
                    Outcome = "rejected",
                    RequiresApproval = false,
                    SupportedKinds = supportedKinds,
                    Message = "A non-empty 'resourceId' is required so authority is bound to an exact target."
                },
                McpJsonContext.Default.McpProposeOperationOutput);
        }

        if (argument.ResourceId.Length > OperationAuthorityContext.MaxResourceIdLength)
        {
            return McpToolHelpers.SuccessResult(
                new McpProposeOperationOutput
                {
                    Outcome = "rejected",
                    RequiresApproval = false,
                    SupportedKinds = supportedKinds,
                    Message = $"The authority-bound 'resourceId' must not exceed {OperationAuthorityContext.MaxResourceIdLength} characters."
                },
                McpJsonContext.Default.McpProposeOperationOutput);
        }

        if (!TryValidateExecutionSpecification(
                kind,
                argument.ResourceId,
                argument.ExecutionPayload,
                out var executionPayload,
                out var validationError))
        {
            return McpToolHelpers.SuccessResult(
                new McpProposeOperationOutput
                {
                    Outcome = "rejected",
                    RequiresApproval = false,
                    SupportedKinds = supportedKinds,
                    Message = validationError,
                },
                McpJsonContext.Default.McpProposeOperationOutput);
        }

        var gateway = httpContext.RequestServices.GetService<IOperationGateway>();
        if (gateway is null)
        {
            return McpToolHelpers.SuccessResult(
                new McpProposeOperationOutput
                {
                    Outcome = "unavailable",
                    RequiresApproval = false,
                    SupportedKinds = supportedKinds,
                    Message = "The operation gateway is unavailable (durable storage is not configured)."
                },
                McpJsonContext.Default.McpProposeOperationOutput);
        }

        var authority = BuildAuthority(
            principal,
            httpContext.RequestServices.GetRequiredService<ITenantContext>(),
            httpContext.RequestServices.GetRequiredService<IConfiguration>()
                .GetValue("MultiTenancy:Enabled", true),
            kind,
            argument.ResourceId);
        var actor = authority.Actor;
        var request = new OperationGatewayRequest
        {
            Kind = kind,
            RequestedByAgent = string.IsNullOrWhiteSpace(actor) ? $"{AgentActorPrefix}mcp" : $"{AgentActorPrefix}{actor}",
            RequestedBy = actor,
            Reason = argument.Reason,
            IdempotencyKey = argument.IdempotencyKey,
            ExecutionPayload = executionPayload,
            Authority = authority,
        };

        var result = await gateway.CreateApprovalProposalAsync(request, cancellationToken).ConfigureAwait(false);

        var output = new McpProposeOperationOutput
        {
            Outcome = result.Outcome.ToString(),
            RequiresApproval = result.Outcome == OperationGatewayOutcome.ProposalCreated,
            ProposalId = result.ProposalId,
            ResourceUri = result.ProposalId == null ? null : McpResourceUris.ProposalUri(result.ProposalId),
            ExecutionOperationId = result.ExecutionOperationId,
            SupportedKinds = supportedKinds,
            Message = result.Message,
        };

        return McpToolHelpers.SuccessResult(output, McpJsonContext.Default.McpProposeOperationOutput);
    }

    private static bool TryValidateExecutionSpecification(
        OperationClass kind,
        string resourceId,
        string? executionPayload,
        out string? normalizedPayload,
        out string? error)
    {
        normalizedPayload = null;
        error = null;

        if (string.IsNullOrWhiteSpace(executionPayload))
        {
            error = $"An executable JSON 'executionPayload' is required for {kind}.";
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(executionPayload);
        }
        catch (JsonException)
        {
            error = "The 'executionPayload' must be valid JSON.";
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "The 'executionPayload' must be a JSON object.";
                return false;
            }

            if (kind == OperationClass.Deploy)
            {
                if (!HasOnlyUniqueProperties(root, DeployPayloadProperties, out error))
                {
                    return false;
                }

                if (!TryReadRequiredString(root, "targetId", out var targetId) ||
                    !TryReadRequiredString(root, "desiredRevision", out _))
                {
                    error = "Deploy executionPayload requires non-empty 'targetId' and 'desiredRevision' values.";
                    return false;
                }

                if (!string.Equals(targetId, resourceId, StringComparison.Ordinal))
                {
                    error = "Deploy executionPayload.targetId must exactly match the authority-bound resourceId.";
                    return false;
                }

                if (!HasOptionalString(root, "currentRevision") ||
                    !HasOptionalEnumNumber<OperationPriority>(root, "priority") ||
                    !HasOptionalStringMap(root, "parameterOverrides"))
                {
                    error = "Deploy executionPayload optional values must use the executable contract: currentRevision is a string, priority is a valid numeric OperationPriority, and parameterOverrides is a string-valued object.";
                    return false;
                }
            }
            else
            {
                if (!HasOnlyUniqueProperties(root, MetadataReleasePayloadProperties, out error))
                {
                    return false;
                }

                if (!root.TryGetProperty("action", out var action) ||
                    action.ValueKind != JsonValueKind.String ||
                    !string.Equals(action.GetString(), "create", StringComparison.OrdinalIgnoreCase))
                {
                    error = "MetadataRelease executionPayload requires action 'create'.";
                    return false;
                }

                if (!TryReadRequiredString(root, "packageId", out _) ||
                    !TryReadRequiredString(root, "targetEnvironment", out _) ||
                    !TryReadRequiredString(root, "resourceSemanticId", out var semanticId) ||
                    !TryReadRequiredString(root, "newFieldName", out _))
                {
                    error = "MetadataRelease executionPayload requires non-empty 'packageId', 'targetEnvironment', 'resourceSemanticId', and 'newFieldName' values.";
                    return false;
                }

                if (!string.Equals(semanticId, resourceId, StringComparison.Ordinal))
                {
                    error = "MetadataRelease executionPayload.resourceSemanticId must exactly match the authority-bound resourceId.";
                    return false;
                }

                if (!HasOptionalString(root, "newFieldType") ||
                    !HasOptionalString(root, "dataPopulateWorkloadId") ||
                    !HasOptionalString(root, "scriptId"))
                {
                    error = "MetadataRelease executionPayload optional values newFieldType, dataPopulateWorkloadId, and scriptId must be strings.";
                    return false;
                }
            }

            normalizedPayload = root.GetRawText();
            return true;
        }
    }

    private static bool TryReadRequiredString(JsonElement parent, string propertyName, out string? value)
    {
        value = parent.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool HasOnlyUniqueProperties(
        JsonElement root,
        HashSet<string> allowedProperties,
        out string? error)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!allowedProperties.Contains(property.Name))
            {
                error = $"executionPayload contains unsupported property '{property.Name}'.";
                return false;
            }

            if (!seen.Add(property.Name))
            {
                error = $"executionPayload contains duplicate property '{property.Name}'.";
                return false;
            }
        }

        error = null;
        return true;
    }

    private static bool HasOptionalString(JsonElement root, string propertyName)
        => !root.TryGetProperty(propertyName, out var property)
            || property.ValueKind is JsonValueKind.String or JsonValueKind.Null;

    private static bool HasOptionalEnumNumber<TEnum>(JsonElement root, string propertyName)
        where TEnum : struct, Enum
        => !root.TryGetProperty(propertyName, out var property)
            || (property.ValueKind == JsonValueKind.Number
                && property.TryGetInt32(out var value)
                && Enum.IsDefined(typeof(TEnum), value));

    private static bool HasOptionalStringMap(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        return property.EnumerateObject().All(entry =>
            entry.Value.ValueKind == JsonValueKind.String && keys.Add(entry.Name));
    }

    private static OperationAuthorityContext BuildAuthority(
        ClaimsPrincipal principal,
        ITenantContext tenantContext,
        bool multiTenancyEnabled,
        OperationClass kind,
        string resourceId)
    {
        var governed = OperatorScopeCatalog.IsScopeGoverned(principal);
        var scopes = OperatorScopeCatalog.CollectRecognizedScopes(principal).ToArray();
        var operation = kind switch
        {
            OperationClass.Deploy or OperationClass.MetadataRelease => OperatorOperation.Publish,
            OperationClass.Seed => OperatorOperation.Create,
            _ => OperatorOperation.Update,
        };
        var resourceType = kind switch
        {
            OperationClass.Deploy or OperationClass.MetadataRelease => OperatorResourceType.Deployment,
            _ => OperatorResourceType.Catalog,
        };

        return OperationAuthorityContext.Capture(principal, tenantContext, multiTenancyEnabled) with
        {
            OAuthScopes = scopes,
            ScopeCeiling = scopes,
            ScopeGoverned = governed,
            ResourceType = resourceType,
            Operation = operation,
            ResourceId = resourceId,
        };
    }
}
