// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers;
using System.Security.Claims;
using System.Text.Json;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Ai.Protocols.Mcp.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Ai.Protocols.Mcp.Tools;

/// <summary>
/// A validated operations-toolset descriptor (RFC #17) projected into a first-class,
/// typed MCP tool (#2483, ADR-0056 Increment 4). One instance wraps one
/// <see cref="OperationDescriptor"/>: <see cref="Describe"/> projects the descriptor's
/// input schema, an output schema, and behavior annotations, and
/// <see cref="InvokeAsync"/> routes the call through the canonical
/// <see cref="IOperationInvoker"/> so the operation policy decision point governs every
/// invocation exactly as the hand-authored publish tools do.
/// </summary>
/// <remarks>
/// The tool does not execute the operation itself; it adapts arguments onto an
/// <see cref="OperationRequest"/> and hands it to the dispatcher, which consults the
/// policy decision point (allow / require-approval / dry-run-first / deny) before any
/// executor runs. For a deterministic, read-only descriptor the result is param-keyed
/// cached (<see cref="IPublishedOperationCache"/>): identical inputs return an
/// identical result without re-execution. Non-deterministic or side-effecting
/// descriptors are never cached.
/// </remarks>
internal sealed class PublishedOperationTool : IMcpTool
{
    /// <summary>Prefix for the MCP tool name projected from an operation id.</summary>
    public const string NamePrefix = "honua_op_";

    private readonly OperationDescriptor _descriptor;
    private readonly string _catalogVersion;
    private readonly ILogger _logger;
    private readonly JsonElement _inputSchema;

    public PublishedOperationTool(
        OperationDescriptor descriptor,
        string catalogVersion,
        ILogger logger)
    {
        _descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        _catalogVersion = catalogVersion ?? string.Empty;
        _logger = logger;
        Name = ProjectName(descriptor.OperationId);
        _inputSchema = BuildInputSchema(descriptor.InputSchema);
    }

    public string Name { get; }

    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Lifecycle;

    /// <summary>Whether the backing descriptor is deterministic (AI-free).</summary>
    public bool IsDeterministic => _descriptor.Policy.Determinism == OperationDeterminism.Deterministic;

    // Deterministic AND read-only invocations are the only ones safe to cache: a
    // cache must never skip a side effect or return a stale AI turn.
    private bool IsCacheable =>
        IsDeterministic && _descriptor.Policy.SideEffectClass == OperationSideEffectClass.ReadOnly;

    /// <summary>
    /// Projects an operation id (for example <c>service.publish</c>) into a valid MCP
    /// tool name (for example <c>honua_op_service_publish</c>).
    /// </summary>
    public static string ProjectName(string operationId)
    {
        var sanitized = new char[operationId.Length];
        for (var i = 0; i < operationId.Length; i++)
        {
            var c = operationId[i];
            sanitized[i] = char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_';
        }

        return NamePrefix + new string(sanitized);
    }

    public McpToolDescriptor Describe()
    {
        var readOnly = _descriptor.Policy.SideEffectClass == OperationSideEffectClass.ReadOnly;
        var destructive =
            _descriptor.Policy.SideEffectClass == OperationSideEffectClass.DestroysState
            || _descriptor.Policy.BlastRadiusClass == OperationBlastRadiusClass.DeploymentScope;

        var title = _descriptor.Title;
        var annotations = readOnly
            ? McpToolAnnotationSets.ReadOnly(title)
            // Non-read-only descriptors mutate state: idempotent only when they neither
            // destroy state nor reach deployment scope.
            : McpToolAnnotationSets.Write(title, destructive, idempotent: !destructive);

        var determinismNote = IsDeterministic
            ? " This operation is deterministic (AI-free); identical inputs return an identical, param-keyed-cached result."
            : " This operation is AI-assisted.";

        return new McpToolDescriptor
        {
            Name = Name,
            Title = title,
            Description = _descriptor.Description
                + " Published operations-toolset operation '" + _descriptor.OperationId
                + "', governed by the operation policy decision point on every invocation"
                + " (allow / require-approval / dry-run-first / deny)." + determinismNote,
            InputSchema = _inputSchema,
            OutputSchema = McpToolOutputSchemas.OperationToolOutputSchema,
            Annotations = annotations,
        };
    }

    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("PublishedOperation");
        McpLog.ToolInvoked(_logger, Name, WorkflowFamily);

        var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);

        var parameters = ReadParameters(arguments);
        var dryRun = ReadBool(arguments, "dryRun");
        var cacheKey = IsCacheable && !dryRun
            ? IPublishedOperationCache.BuildKey(_descriptor.OperationId, _catalogVersion, parameters)
            : null;

        // Deterministic, read-only cache hit: identical inputs → identical result,
        // no re-execution and no policy round-trip needed (a read-only deterministic
        // op that was allowed once stays allowed for the same principal context).
        if (cacheKey is not null)
        {
            var cache = httpContext.RequestServices.GetService<IPublishedOperationCache>();
            var hit = cache?.TryGet(cacheKey);
            if (hit is not null)
            {
                return McpToolHelpers.SuccessResult(hit, McpJsonContext.Default.McpOperationToolOutput);
            }
        }

        var invoker = httpContext.RequestServices.GetService<IOperationInvoker>();
        if (invoker is null)
        {
            return McpToolHelpers.SuccessResult(
                new McpOperationToolOutput
                {
                    Status = OperationHandleStatus.Failed.ToString(),
                    OperationId = _descriptor.OperationId,
                    Deterministic = IsDeterministic,
                    Message = "The operations toolset is unavailable (no IOperationInvoker is registered in this composition).",
                },
                McpJsonContext.Default.McpOperationToolOutput);
        }

        var request = new OperationRequest
        {
            OperationId = _descriptor.OperationId,
            ConnectionId = ReadString(arguments, "connectionId"),
            ServiceName = ReadString(arguments, "serviceName"),
            Parameters = parameters,
            DryRun = dryRun,
        };

        var context = new OperationPolicyContext
        {
            PrincipalId = principal.Identity?.Name,
            Tier = ResolveTier(httpContext),
            Roles = principal.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray(),
        };

        var handle = await invoker.SubmitAsync(request, context, cancellationToken).ConfigureAwait(false);
        var output = Project(handle, cacheKey);

        // Cache only a completed, deterministic, read-only result — never a
        // requires-approval / denied / failed outcome, and never a side effect.
        if (cacheKey is not null && handle.Status == OperationHandleStatus.Completed)
        {
            httpContext.RequestServices.GetService<IPublishedOperationCache>()?.Set(cacheKey, output);
        }

        return McpToolHelpers.SuccessResult(output, McpJsonContext.Default.McpOperationToolOutput);
    }

    private McpOperationToolOutput Project(OperationHandle handle, string? cacheKey) => new()
    {
        Status = handle.Status.ToString(),
        RequiresApproval = handle.Status == OperationHandleStatus.RequiresApproval,
        Deterministic = IsDeterministic,
        CacheHit = false,
        CacheKey = cacheKey,
        OperationId = handle.OperationId,
        HandleId = handle.HandleId,
        JobId = handle.JobId,
        ApprovalLane = handle.ApprovalLane,
        MetadataRevision = handle.MetadataRevision,
        Summary = handle.Result?.Summary,
        Message = handle.Reason,
        Details = handle.Result?.Details ?? new Dictionary<string, string>(StringComparer.Ordinal),
    };

    private static string? ResolveTier(HttpContext httpContext)
    {
        // Tier is resolved from the running edition so the policy decision point can
        // apply tier-aware rules. Resolved leniently: a host without licensing wired
        // (a lightweight test host) leaves the tier null, i.e. Community pass-through.
        var licensing = httpContext.RequestServices.GetService<ILicenseEntitlementService>();
        return licensing?.GetSnapshot().Edition.ToString().ToLowerInvariant();
    }

    private Dictionary<string, string?> ReadParameters(JsonElement? arguments)
    {
        var parameters = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var parameter in _descriptor.InputSchema)
        {
            parameters[parameter.Name] = ReadString(arguments, parameter.Name);
        }

        return parameters;
    }

    private static string? ReadString(JsonElement? arguments, string name)
    {
        if (arguments is not { ValueKind: JsonValueKind.Object } args
            || !args.TryGetProperty(name, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => null,
            _ => property.GetRawText(),
        };
    }

    private static bool ReadBool(JsonElement? arguments, string name)
    {
        if (arguments is not { ValueKind: JsonValueKind.Object } args
            || !args.TryGetProperty(name, out var property))
        {
            return false;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(property.GetString(), out var parsed) && parsed,
            _ => false,
        };
    }

    // Builds a JSON Schema object for the operation's parameters using a
    // Utf8JsonWriter (reflection-free, AOT-safe). Operation parameters are
    // string-valued on the wire (OperationRequest keeps them as strings), so each
    // property is typed string with the descriptor's title as its description.
    private static JsonElement BuildInputSchema(IReadOnlyList<OperationParameterDescriptor> parameters)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "object");

            writer.WriteStartObject("properties");
            foreach (var parameter in parameters)
            {
                writer.WriteStartObject(parameter.Name);
                writer.WriteString("type", "string");
                if (!string.IsNullOrWhiteSpace(parameter.Title))
                {
                    writer.WriteString("description", parameter.Title);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();

            writer.WriteStartArray("required");
            foreach (var parameter in parameters)
            {
                if (parameter.Required)
                {
                    writer.WriteStringValue(parameter.Name);
                }
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }
}
