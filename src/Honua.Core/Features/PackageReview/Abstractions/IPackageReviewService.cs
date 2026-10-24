// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.PackageReview.Domain;

namespace Honua.Core.Features.PackageReview.Abstractions;

/// <summary>
/// Canonical service for package validation and read-only preview planning.
/// </summary>
public interface IPackageReviewService
{
    /// <summary>
    /// Reviews a package request and returns the shared package-review response shape.
    /// </summary>
    /// <param name="request">Package-review request.</param>
    /// <param name="context">Caller-visible context used for deterministic correlation and auth-sensitive review.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<PackageReviewResponse> ReviewAsync(
        PackageReviewRequest request,
        PackageReviewContext? context = null,
        CancellationToken cancellationToken = default);
}
