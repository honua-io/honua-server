// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Protocols.GeoServices;
using Microsoft.Extensions.Primitives;

namespace Honua.Protocols.GeoServices.FeatureServer;

internal static partial class FeatureServerEndpoints
{
    internal static Task<(IReadOnlyDictionary<string, StringValues>? Values, string? Error)> TryReadRequestValuesAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
        => GeoServicesRequestValueHelpers.TryReadRequestValuesAsync(request, cancellationToken);

    internal static bool TryValidateRequestContentType(HttpRequest request, out string? receivedContentType)
        => GeoServicesRequestValueHelpers.TryValidateRequestContentType(request, out receivedContentType);

    internal static bool TryGetUnsupportedMediaType(string? error, out string? receivedContentType)
        => GeoServicesRequestValueHelpers.TryGetUnsupportedMediaType(error, out receivedContentType);

    internal static IResult CreateUnsupportedRequestContentTypeResult(HttpContext context, string? receivedContentType)
        => GeoServicesRequestValueHelpers.CreateUnsupportedRequestContentTypeResult(context, receivedContentType);

    internal static Dictionary<string, StringValues> ToCaseInsensitiveDictionary(IQueryCollection values)
        => GeoServicesRequestValueHelpers.ToCaseInsensitiveDictionary(values);

    private static string? GetValueString(IReadOnlyDictionary<string, StringValues> values, string key)
    {
        return TryGetValue(values, key, out var raw) ? raw.ToString() : null;
    }

    private static bool TryGetValue(IReadOnlyDictionary<string, StringValues> values, string key, out StringValues value)
        => values.TryGetValue(key, out value);

}
