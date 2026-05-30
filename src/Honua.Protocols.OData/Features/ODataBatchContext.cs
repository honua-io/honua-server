// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Protocols.OData;

internal static class ODataBatchContext
{
    private const string SuppressMutationSideEffectsItemKey = "Honua.OData.Batch.SuppressMutationSideEffects";

    public static void SuppressMutationSideEffects(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Items[SuppressMutationSideEffectsItemKey] = true;
    }

    public static bool ShouldSuppressMutationSideEffects(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items.TryGetValue(SuppressMutationSideEffectsItemKey, out var value) &&
               value is true;
    }
}
