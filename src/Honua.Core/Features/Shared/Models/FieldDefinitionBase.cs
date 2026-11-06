// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Shared.Models;

public abstract record FieldDefinitionBase
{
    public required string Name { get; init; }

    public required string Type { get; init; }

    public string? Alias { get; init; }

    public int? Length { get; init; }

    public bool Nullable { get; init; } = true;

    public object? DefaultValue { get; init; }

    public string DisplayName => Alias ?? Name;

    public virtual string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
            return "Field name cannot be empty";

        if (Name.Length > 64)
            return "Field name cannot exceed 64 characters";

        // Length validation for string-like types (protocol-specific validation in derived classes)
        if (IsStringType() && Length.HasValue && Length.Value <= 0)
            return "String field length must be positive";

        if (IsStringType() && Length.HasValue && Length.Value > 8000)
            return "String field length cannot exceed 8000 characters";

        return null; // Valid
    }

    protected virtual bool IsStringType()
    {
        var typeUpper = Type.ToUpperInvariant();
        return typeUpper.Contains("STRING") || typeUpper.Contains("TEXT") || typeUpper.Contains("VARCHAR");
    }
}
