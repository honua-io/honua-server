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

public readonly record struct AttributeValidationOptions(
    AttributeValidationProfile Profile = AttributeValidationProfile.Strict,
    AttributeMutationOperation Operation = AttributeMutationOperation.Insert,
    bool CoerceExactValues = false);
