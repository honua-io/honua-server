// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.PackageReview.Domain;

namespace Honua.Core.Features.PackageReview.Abstractions;

/// <summary>
/// Adapter for package-family-specific validation and preview planning.
/// </summary>
public interface IPackageFamilyReviewAdapter
{
    /// <summary>
    /// Determines whether the adapter should review the supplied request.
    /// </summary>
    /// <param name="request">Package-review request.</param>
    bool CanReview(PackageReviewRequest request);

    /// <summary>
    /// Reviews the package family without executing, publishing, or mutating catalog state.
    /// </summary>
    /// <param name="request">Package-review request.</param>
    /// <param name="context">Caller-visible review context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask<PackageFamilyReviewResult> ReviewAsync(
        PackageReviewRequest request,
        PackageReviewContext context,
        CancellationToken cancellationToken);
}
