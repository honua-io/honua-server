// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Styling;

public static class SharedStyleReferences
{
    public static bool CanMatchLiterally(string styleId) =>
        !string.IsNullOrEmpty(styleId) && styleId.IndexOfAny(['"', '\\']) < 0;

    public static string StyleIdNeedle(string styleId) => "\"styleId\":\"" + styleId + "\"";

    public static string StyleRefNeedle(string styleId) => "\"styleRef\":\"" + styleId + "\"";

    // jsonb::text prints a space after ':' . Both forms are the same token.
    public static string StyleIdNeedleSpaced(string styleId) => "\"styleId\": \"" + styleId + "\"";

    public static string StyleRefNeedleSpaced(string styleId) => "\"styleRef\": \"" + styleId + "\"";

    public static bool AppearsIn(string? json, string styleId) =>
        CanMatchLiterally(styleId)
        && json is not null
        && (json.Contains(StyleIdNeedle(styleId), StringComparison.Ordinal)
            || json.Contains(StyleRefNeedle(styleId), StringComparison.Ordinal));
}
