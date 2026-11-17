// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Edit;

public enum AttributeValidationProfile
{
    Strict,
    GeoServices
}

public enum AttributeMutationOperation
{
    Insert,
    Update
}

// Clients without an exact wire type send numbers. CanonicalText stores those as exact text.
public enum ExactValueCoercion
{
    None,
    CanonicalText
}

public readonly record struct AttributeValidationOptions(
    AttributeValidationProfile Profile = AttributeValidationProfile.Strict,
    AttributeMutationOperation Operation = AttributeMutationOperation.Insert,
    ExactValueCoercion Coercion = ExactValueCoercion.None);
