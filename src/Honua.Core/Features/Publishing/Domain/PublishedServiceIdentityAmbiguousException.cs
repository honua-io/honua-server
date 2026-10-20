// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Publishing.Domain;

/// <summary>Multiple promotion records share one case-insensitive public service identity.</summary>
public sealed class PublishedServiceIdentityAmbiguousException(string serviceId)
    : InvalidOperationException($"Published service identity '{serviceId}' is ambiguous.");
