// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Metadata.Domain;

/// <summary>
/// Result of validating (and optionally upgrading) a metadata resource against the schema registry.
/// </summary>
public sealed record MetadataSchemaValidationResult(
    bool IsValid,
    MetadataResource? Resource,
    IReadOnlyList<string> Errors,
    bool WasUpConverted);
