// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Admin.Domain;

/// <summary>
/// Persisted operator-managed display metadata for a layer field.
/// </summary>
/// <param name="Name">Field name.</param>
/// <param name="Alias">Optional field alias displayed by protocol metadata surfaces.</param>
/// <param name="Domain">Optional coded-value domain for the field.</param>
/// <param name="Hidden">Whether public protocol output should omit the field.</param>
public sealed record LayerFieldConfiguration(
    string Name,
    string? Alias,
    MetadataV2FieldDomain? Domain,
    bool Hidden);

/// <summary>
/// Operator-managed display metadata update for a layer field.
/// </summary>
/// <param name="Name">Field name.</param>
/// <param name="Alias">Optional field alias displayed by protocol metadata surfaces.</param>
/// <param name="Domain">Optional coded-value domain for the field.</param>
/// <param name="Hidden">Optional hidden-field update. Null preserves the current value.</param>
public sealed record LayerFieldConfigurationUpdate(
    string Name,
    string? Alias,
    MetadataV2FieldDomain? Domain,
    bool? Hidden);
