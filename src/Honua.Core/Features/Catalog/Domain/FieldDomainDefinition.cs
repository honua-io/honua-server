// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Catalog.Domain;

/// <summary>
/// Declares a schema-defined domain for a field.
/// </summary>
/// <param name="Name">Stable domain name.</param>
/// <param name="Type">Domain type such as <c>codedValue</c> or <c>range</c>.</param>
/// <param name="CodedValues">Allowed coded values for coded-value domains.</param>
/// <param name="Range">Minimum and maximum values for range domains.</param>
/// <param name="MergePolicy">Optional merge policy metadata.</param>
/// <param name="SplitPolicy">Optional split policy metadata.</param>
public sealed record FieldDomainDefinition(
    string Name,
    string Type,
    DomainCodedValueDefinition[]? CodedValues = null,
    RangeDomainDefinition? Range = null,
    string? MergePolicy = null,
    string? SplitPolicy = null);

/// <summary>
/// Declares an individual coded value within a schema-defined domain.
/// </summary>
/// <param name="Code">Stored code value.</param>
/// <param name="Name">Display label for the code.</param>
/// <param name="Description">Optional description.</param>
public sealed record DomainCodedValueDefinition(
    object Code,
    string Name,
    string? Description = null);

/// <summary>
/// Declares the inclusive minimum and maximum values for a range domain.
/// </summary>
/// <param name="MinValue">Minimum allowed value.</param>
/// <param name="MaxValue">Maximum allowed value.</param>
public sealed record RangeDomainDefinition(
    object MinValue,
    object MaxValue);
