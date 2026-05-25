// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;

namespace Honua.Core.Features.PackageReview.Services;

internal static class PackageReviewTelemetry
{
    public const string ActivitySourceName = "Honua.PackageReview";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);
}
