// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Server.Features.Admin.Models;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Durable spec for a large layer publish. The connection string is resolved at execution
/// from <see cref="ConnectionId"/> or <see cref="ConnectionKey"/>; it is not stored on the job.
/// </summary>
internal sealed class LayerPublishJobPayload
{
    internal const string ParameterName = "payload";

    public Guid ConnectionId { get; init; }

    public required string ConnectionKey { get; init; }

    public required long RowCount { get; init; }

    public required PublishLayerRequest Request { get; init; }

    public string? TenantId { get; init; }

    public TenantContextSource TenantSource { get; init; }

    public string? CurrentSchema { get; init; }

    internal string ToJson()
        => JsonSerializer.Serialize(this, LayerPublishingJsonContext.Default.LayerPublishJobPayload);

    internal static bool TryParse(
        IReadOnlyDictionary<string, string> parameters,
        out LayerPublishJobPayload? payload,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        payload = null;
        if (!parameters.TryGetValue(ParameterName, out var json) || string.IsNullOrWhiteSpace(json))
        {
            error = "missing payload";
            return false;
        }

        try
        {
            payload = JsonSerializer.Deserialize(json, LayerPublishingJsonContext.Default.LayerPublishJobPayload);
        }
        catch (JsonException)
        {
            error = "payload is not valid JSON";
            return false;
        }

        if (payload?.Request is null || string.IsNullOrWhiteSpace(payload.ConnectionKey))
        {
            payload = null;
            error = "payload is incomplete";
            return false;
        }

        error = null;
        return true;
    }
}
