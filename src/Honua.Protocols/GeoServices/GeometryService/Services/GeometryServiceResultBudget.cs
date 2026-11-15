// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Protocols.GeoServices.GeometryService.Services;

// The geometry bytes (WKB) one request may build before its response is written. Buffer,
// overlay, cut and repair results can be larger than the geometries they were made from, so
// each result is counted when it is built and the request is refused once the total passes
// the budget. The budget is twice the geometry text the request sent, or an allowance if
// that is larger: the largest accepted request (Limits:Edits:MaxPayloadSize) for a signed-in
// caller, a quarter of it for a caller that is not signed in.
internal sealed class GeometryServiceResultBudget
{
    internal const int NotSignedInAllowanceDivisor = 4;

    internal const string RefusalMessage =
        "The result is larger than this service returns for one request. Send fewer geometries or distances.";

    private long _built;

    internal GeometryServiceResultBudget(long bytes)
    {
        Bytes = Math.Max(bytes, 1);
    }

    public long Bytes { get; }

    public static GeometryServiceResultBudget Create(HttpContext context, params IEnumerable<string>[] requestGeometries)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requestGeometries);

        var largestRequest = context.RequestServices
            .GetService<IOptions<LimitsOptions>>()?.Value.Edits.MaxPayloadSize
            ?? new EditLimits().MaxPayloadSize;
        var allowance = GeometryServiceCallers.Of(context) == GeometryServiceCaller.SignedIn
            ? largestRequest
            : largestRequest / NotSignedInAllowanceDivisor;

        var sent = 0L;
        foreach (var geometries in requestGeometries)
        {
            foreach (var geometry in geometries)
            {
                sent += geometry.Length;
            }
        }

        return new GeometryServiceResultBudget(Math.Max(allowance, sent * 2));
    }

    // True when results of about this many bytes still fit. Used before work whose result size
    // can be estimated from its input.
    public bool Fits(long estimatedBytes) => estimatedBytes >= 0 && estimatedBytes <= Bytes - _built;

    // Counts one built result and returns it; refuses the request once the budget is passed.
    public byte[] Add(byte[] wkb)
    {
        ArgumentNullException.ThrowIfNull(wkb);
        if (!Fits(wkb.Length))
        {
            throw new GeometryServiceLimitException(RefusalMessage);
        }

        _built += wkb.Length;
        return wkb;
    }
}
