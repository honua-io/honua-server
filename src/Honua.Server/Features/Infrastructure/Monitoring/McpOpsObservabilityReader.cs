// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.Observability.Abstractions;
using Honua.Core.Features.Observability.Domain;
using Honua.Geoprocessing;
using Honua.Infrastructure.Authentication;
using Honua.Server.Features.Admin;
using Honua.Server.Features.Admin.Models;
using Microsoft.AspNetCore.Authorization;

namespace Honua.Infrastructure.Monitoring;

/// <summary>
/// Server adapter that backs the read-only MCP operational-observability tools
/// with the same admin DTOs and ops-read authorization posture as the REST
/// observability endpoints.
/// </summary>
internal sealed class McpOpsObservabilityReader(
    IOpsHealthSnapshotService healthSnapshots,
    IOpsFindingsService findings,
    IOperateEventFeed operateEvents,
    IAuthorizationService authorization,
    IServiceProvider services) : IMcpOpsObservabilityReader
{
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 200;

    private readonly IOpsHealthSnapshotService _healthSnapshots = healthSnapshots;
    private readonly IOpsFindingsService _findings = findings;
    private readonly IOperateEventFeed _operateEvents = operateEvents;
    private readonly IAuthorizationService _authorization = authorization;
    private readonly IServiceProvider _services = services;

    public async Task<JsonElement> GetOpsHealthAsync(
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        await EnsureOpsReadAsync(principal, cancellationToken).ConfigureAwait(false);

        var snapshot = await _healthSnapshots.GetAsync(cancellationToken).ConfigureAwait(false);
        return Serialize(snapshot, OpsObservabilityJsonContext.Default.OpsHealthSnapshotResponse);
    }

    public async Task<JsonElement> GetOpsFindingsAsync(
        ClaimsPrincipal principal,
        McpOpsFindingsArgument argument,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(argument);
        await EnsureOpsReadAsync(principal, cancellationToken).ConfigureAwait(false);

        var severity = ParseOptionalEnum<OpsFindingSeverity>(argument.Severity, "severity");
        var findings = await _findings.EvaluateAsync(cancellationToken).ConfigureAwait(false);
        var filtered = findings.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(argument.FindingId))
        {
            filtered = filtered.Where(f => string.Equals(
                f.Id,
                argument.FindingId.Trim(),
                StringComparison.Ordinal));
        }

        if (severity is not null)
        {
            var severityValue = severity.Value;
            filtered = filtered.Where(f => f.Severity == severityValue);
        }

        if (!string.IsNullOrWhiteSpace(argument.Rule))
        {
            filtered = filtered.Where(f => string.Equals(
                f.Rule,
                argument.Rule.Trim(),
                StringComparison.OrdinalIgnoreCase));
        }

        var response = new OpsFindingsListResponse
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            Findings = filtered.Select(OpsFindingResponseMapper.Map).ToArray(),
        };

        return Serialize(response, OpsObservabilityJsonContext.Default.OpsFindingsListResponse);
    }

    public async Task<JsonElement> ListAlertEventsAsync(
        ClaimsPrincipal principal,
        McpAlertEventsArgument argument,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(argument);
        await EnsureOpsReadAsync(principal, cancellationToken).ConfigureAwait(false);

        var query = _services.GetService<IAlertEventQuery>()
            ?? throw new GeoprocessingPreconditionFailedException(
                "The alert-event query surface is unavailable in this composition.");

        var (ruleId, ruleName) = ParseAlertRule(argument.Rule);
        var filter = new AlertEventFilter
        {
            From = argument.From,
            To = argument.To,
            RuleId = ruleId,
            Severities = OptionalList(ParseOptionalEnum<AlertSeverity>(argument.Severity, "severity")),
            LifecycleStatuses = OptionalList(ParseOptionalEnum<AlertLifecycleStatus>(
                argument.LifecycleState,
                "lifecycleState")),
            PageSize = ClampPageSize(argument.PageSize),
            Cursor = Clean(argument.Cursor)
        };

        var page = await query.ListAsync(filter, cancellationToken).ConfigureAwait(false);
        var items = ApplyAlertPostFilters(page.Items, argument.Source, ruleName)
            .Select(ObservabilityAlertEventResponseMapper.Map)
            .ToArray();

        var response = new ObservabilityAlertEventPageResponse
        {
            Items = items,
            NextCursor = page.NextCursor
        };

        return Serialize(response, ObservabilityJsonContext.Default.ObservabilityAlertEventPageResponse);
    }

    public async Task<JsonElement> ListOperateEventsAsync(
        ClaimsPrincipal principal,
        McpOperateEventsArgument argument,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(argument);
        await EnsureOpsReadAsync(principal, cancellationToken).ConfigureAwait(false);

        var filter = new OperateEventFilter
        {
            From = argument.From,
            To = argument.To,
            Kinds = ParseOperateKinds(argument.Kind),
            CorrelationId = Clean(argument.CorrelationId),
            OperationId = Clean(argument.OperationId),
            ReleaseId = Clean(argument.ReleaseId),
            PageSize = ClampPageSize(argument.PageSize)
        };

        var page = await _operateEvents.ListAsync(filter, cancellationToken).ConfigureAwait(false);
        var response = new OperateEventPageResponse
        {
            Items = page.Items.Select(OperateEventResponseMapper.Map).ToArray(),
            PartialResult = page.PartialResult,
            SourceErrors = page.SourceErrors?.ToDictionary(
                pair => pair.Key.ToString().ToLowerInvariant(),
                pair => pair.Value)
        };

        return Serialize(response, ObservabilityJsonContext.Default.OperateEventPageResponse);
    }

    private async Task EnsureOpsReadAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        var resource = new DefaultHttpContext
        {
            User = principal,
            RequestAborted = cancellationToken
        };
        resource.Request.Method = HttpMethods.Get;

        var result = await _authorization
            .AuthorizeAsync(principal, resource, AuthenticationExtensions.OpsReadPolicy)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            throw new GeoprocessingAuthorizationException(
                requiresAuthentication: false,
                message: "Caller is not authorized to read operational observability.");
        }
    }

    private static IEnumerable<AlertEventSummary> ApplyAlertPostFilters(
        IReadOnlyList<AlertEventSummary> items,
        string? source,
        string? ruleName)
    {
        var filtered = items.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(source))
        {
            filtered = source.Trim().ToLowerInvariant() switch
            {
                "gis" => filtered.Where(item => item.RuleId is not null),
                "ops" => filtered.Where(item => item.RuleId is null),
                _ => throw new GeoprocessingValidationException(
                    $"Unsupported source '{source}'. Expected 'gis' or 'ops'.")
            };
        }

        if (!string.IsNullOrWhiteSpace(ruleName))
        {
            filtered = filtered.Where(item => string.Equals(
                item.RuleName,
                ruleName,
                StringComparison.OrdinalIgnoreCase));
        }

        return filtered;
    }

    private static List<OperateEventKind>? ParseOperateKinds(IReadOnlyList<string>? values)
    {
        if (values is null || values.Count == 0)
        {
            return null;
        }

        var parsed = new List<OperateEventKind>(values.Count);
        for (var i = 0; i < values.Count; i++)
        {
            var value = values[i];
            if (string.IsNullOrWhiteSpace(value) ||
                !Enum.TryParse<OperateEventKind>(value, ignoreCase: true, out var kind) ||
                !Enum.IsDefined(kind))
            {
                throw new GeoprocessingValidationException(
                    $"Unsupported kind '{value}' at index {i}.");
            }

            parsed.Add(kind);
        }

        return parsed;
    }

    private static (long? RuleId, string? RuleName) ParseAlertRule(string? rule)
    {
        var cleaned = Clean(rule);
        if (cleaned is null)
        {
            return (null, null);
        }

        return long.TryParse(cleaned, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ruleId)
            ? (ruleId, null)
            : (null, cleaned);
    }

    private static IReadOnlyList<TEnum>? OptionalList<TEnum>(TEnum? value)
        where TEnum : struct, Enum =>
        value is null ? null : [value.Value];

    private static TEnum? ParseOptionalEnum<TEnum>(string? value, string fieldName)
        where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed) &&
            Enum.IsDefined(parsed))
        {
            return parsed;
        }

        throw new GeoprocessingValidationException(
            $"'{fieldName}' contains unsupported value '{value}'.");
    }

    private static int ClampPageSize(int? pageSize) =>
        Math.Min(MaxPageSize, Math.Max(1, pageSize ?? DefaultPageSize));

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static JsonElement Serialize<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        var json = JsonSerializer.Serialize(value, typeInfo);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
