// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Stac;
using Honua.Protocols.Stac.Models;
using Honua.Protocols.Stac.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Protocols.Stac;

internal static partial class SearchEndpoints
{
    private readonly record struct SearchOffsetCheck
    {
        public required int Offset { get; init; }
        public required int TargetCount { get; init; }
        public bool OmitCount { get; init; }
    }

    private static IResult? ValidateSearchOffsetLimits(
        HttpContext context,
        SearchOffsetCheck paging,
        Activity? activity)
    {
        var maxOffset = context.RequestServices.GetService<IOptions<Honua.Core.Configuration.LimitsOptions>>()?.Value.Query.MaxOffset
            ?? 1_000_000;
        if (paging.Offset > maxOffset)
        {
            StacTelemetry.SetFailed(activity, "offset_limit_exceeded");
            return StandardErrorHelpers.CreateBadRequest(
                context,
                $"offset ({paging.Offset.ToString(CultureInfo.InvariantCulture)}) exceeds the maximum supported offset of {maxOffset.ToString(CultureInfo.InvariantCulture)}.");
        }

        if (paging.OmitCount && paging.TargetCount > 1)
        {
            var maxOmitCountOffset = Math.Min(maxOffset, StacConstants.MaxOmitCountSearchOffset);
            if (paging.Offset > maxOmitCountOffset)
            {
                StacTelemetry.SetFailed(activity, "omit_count_offset_limit_exceeded");
                return StandardErrorHelpers.CreateBadRequest(
                    context,
                    $"offset ({paging.Offset.ToString(CultureInfo.InvariantCulture)}) exceeds the maximum supported offset of {maxOmitCountOffset.ToString(CultureInfo.InvariantCulture)} for multi-collection search without exact counts. Narrow collections to a single collection to page further.");
            }
        }

        return null;
    }

    private static async Task<(int LocalOffset, int RemainingSkip, bool Exhausted)> AdvanceOmitCountSkipAsync(
        (IFeatureReader Reader, int StorageLayerId) target,
        (FeatureQuery Query, int TargetCount, int RemainingSkip) paging,
        CancellationToken cancellationToken)
    {
        if (paging.TargetCount == 1)
        {
            return (paging.RemainingSkip, 0, false);
        }

        var localOffset = 0;
        var remainingSkip = paging.RemainingSkip;
        while (remainingSkip > 0)
        {
            var skipPage = await StacPageReader.ReadAsync(
                target.Reader,
                target.StorageLayerId,
                paging.Query with { Offset = localOffset, Limit = Math.Min(remainingSkip, StacConstants.MaxSearchLimit) },
                true,
                cancellationToken);
            remainingSkip -= skipPage.Items.Length;
            localOffset += skipPage.Items.Length;
            if (!skipPage.HasMoreResults || skipPage.Items.IsEmpty)
            {
                return (localOffset, remainingSkip, true);
            }
        }

        return (localOffset, remainingSkip, false);
    }
}
