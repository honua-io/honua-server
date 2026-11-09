// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;

namespace Honua.Core.Features.PackageReview.Services;

internal static class PackageReviewTelemetry
{
    public const string ActivitySourceName = "Honua.PackageReview";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);
}
