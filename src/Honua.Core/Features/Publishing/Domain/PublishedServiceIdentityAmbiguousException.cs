// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Publishing.Domain;

/// <summary>Multiple promotion records share one case-insensitive public service identity.</summary>
public sealed class PublishedServiceIdentityAmbiguousException(string serviceId)
    : InvalidOperationException($"Published service identity '{serviceId}' is ambiguous.");
