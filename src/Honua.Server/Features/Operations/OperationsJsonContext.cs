// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Operations.Domain;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Operations;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(ApiResponse<OperationCatalogSnapshot>))]
[JsonSerializable(typeof(ApiResponse<OperationValidation>))]
[JsonSerializable(typeof(ApiResponse<OperationHandle>))]
[JsonSerializable(typeof(ApiResponse<OperationStatus>))]
[JsonSerializable(typeof(ApiResponse<OperationSecretValueResponse>))]
[JsonSerializable(typeof(ApiResponse<object>))]
[JsonSerializable(typeof(OperationCatalogSnapshot))]
[JsonSerializable(typeof(OperationValidation))]
[JsonSerializable(typeof(OperationHandle))]
[JsonSerializable(typeof(OperationStatus))]
[JsonSerializable(typeof(OperationSecretValueResponse))]
[JsonSerializable(typeof(OperationSecretReference))]
[JsonSerializable(typeof(OperationInvokeRequest))]
internal sealed partial class OperationsJsonContext : JsonSerializerContext
{
}

public sealed record OperationInvokeRequest
{
    public IReadOnlyDictionary<string, string?> Parameters { get; init; } =
        new Dictionary<string, string?>(StringComparer.Ordinal);

    public string? ConnectionId { get; init; }

    public string? ServiceName { get; init; }

    public IReadOnlyList<string> Fields { get; init; } = [];

    public bool DryRun { get; init; }
}

public sealed record OperationSecretValueResponse
{
    public required string Name { get; init; }

    // The plaintext secret is never cached and is not persisted.
    public required string Value { get; init; }
}
