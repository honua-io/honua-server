// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Geocoding.Providers;

internal static partial class NominatimGeocodeProviderLog
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "Nominatim suggest requested while EnableSuggestFromSearch is disabled.")]
    public static partial void SuggestDisabled(ILogger logger);
}
