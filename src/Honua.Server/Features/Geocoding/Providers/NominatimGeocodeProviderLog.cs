// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Geocoding.Providers;

internal static partial class NominatimGeocodeProviderLog
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "Nominatim suggest requested while EnableSuggestFromSearch is disabled.")]
    public static partial void SuggestDisabled(ILogger logger);
}
