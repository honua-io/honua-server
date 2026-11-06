// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.PackageReview.Domain;

namespace Honua.Core.Features.PackageReview.Abstractions;

public interface IPackageFamilyReviewAdapter
{
    bool CanReview(PackageReviewRequest request);

    ValueTask<PackageFamilyReviewResult> ReviewAsync(
        PackageReviewRequest request,
        PackageReviewContext context,
        CancellationToken cancellationToken);
}
