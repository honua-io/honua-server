// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Caching;

// The output-cache store refuses a zero or negative lifetime when it stores an entry, which is after the response is sent;
// a bad OutputCache value has to stop the host at startup instead.
internal static class OutputCacheTtlValidation
{
    internal static void EnsureValid(OutputCacheTtlOptions ttl)
    {
        var invalid = typeof(OutputCacheTtlOptions).GetProperties()
            .Where(static property => property.PropertyType == typeof(TimeSpan))
            .Where(property => (TimeSpan)property.GetValue(ttl)! <= TimeSpan.Zero)
            .Select(static property => $"{OutputCacheTtlOptions.SectionName}:{property.Name} must be a positive duration.")
            .ToList();

        if (invalid.Count > 0)
        {
            throw new OptionsValidationException(OutputCacheTtlOptions.SectionName, typeof(OutputCacheTtlOptions), invalid);
        }
    }
}
