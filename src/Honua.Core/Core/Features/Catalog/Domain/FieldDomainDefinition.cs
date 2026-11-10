// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Catalog.Domain;

public sealed record FieldDomainDefinition(
    string Name,
    string Type,
    DomainCodedValueDefinition[]? CodedValues = null,
    RangeDomainDefinition? Range = null,
    string? MergePolicy = null,
    string? SplitPolicy = null);

public sealed record DomainCodedValueDefinition(
    object Code,
    string Name,
    string? Description = null);

public sealed record RangeDomainDefinition(
    object MinValue,
    object MaxValue);
