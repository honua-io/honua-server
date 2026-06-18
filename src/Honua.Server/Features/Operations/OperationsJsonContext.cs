// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Operations.Domain;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Operations;

/// <summary>
/// Source-generated JSON context for the Operations Toolset endpoints. Keeps the surface
/// AOT-safe (the published image disables reflection-based serialization).
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(ApiResponse<OperationCatalogSnapshot>))]
[JsonSerializable(typeof(ApiResponse<OperationValidation>))]
[JsonSerializable(typeof(ApiResponse<OperationHandle>))]
[JsonSerializable(typeof(ApiResponse<OperationStatus>))]
[JsonSerializable(typeof(ApiResponse<object>))]
[JsonSerializable(typeof(OperationCatalogSnapshot))]
[JsonSerializable(typeof(OperationValidation))]
[JsonSerializable(typeof(OperationHandle))]
[JsonSerializable(typeof(OperationStatus))]
[JsonSerializable(typeof(OperationInvokeRequest))]
internal sealed partial class OperationsJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Request body for the validate and submit operation endpoints. Mirrors the
/// <c>OperationRequest</c> domain shape with a JSON-friendly surface.
/// </summary>
public sealed record OperationInvokeRequest
{
    /// <summary>
    /// Caller-supplied parameter values keyed by parameter name.
    /// </summary>
    public IReadOnlyDictionary<string, string?> Parameters { get; init; } =
        new Dictionary<string, string?>(StringComparer.Ordinal);

    /// <summary>
    /// Connection identifier the operation targets.
    /// </summary>
    public string? ConnectionId { get; init; }

    /// <summary>
    /// Service name the operation targets.
    /// </summary>
    public string? ServiceName { get; init; }

    /// <summary>
    /// Attribute fields to act on, when the operation targets a subset of columns.
    /// </summary>
    public IReadOnlyList<string> Fields { get; init; } = [];

    /// <summary>
    /// Whether the caller requested a dry-run/preview.
    /// </summary>
    public bool DryRun { get; init; }
}
