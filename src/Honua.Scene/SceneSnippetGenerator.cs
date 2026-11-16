// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Scene;

public static class SceneSnippetGenerator
{
    public static string BuildCesiumJs(string tilesetUrl)
    {
        ValidateTilesetUrl(tilesetUrl);
        return $"new Cesium.Cesium3DTileset({{ url: \"{EscapeForJsString(tilesetUrl)}\" }})";
    }

    public static string BuildHonuaSceneTag(string tilesetUrl)
    {
        ValidateTilesetUrl(tilesetUrl);
        return $"<honua-scene src=\"{EscapeForHtmlAttribute(tilesetUrl)}\"></honua-scene>";
    }

    private static void ValidateTilesetUrl(string tilesetUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tilesetUrl);
    }

    private static string EscapeForJsString(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '<':
                    builder.Append("\\u003C");
                    break;
                default:
                    builder.Append(c);
                    break;
            }
        }
        return builder.ToString();
    }

    private static string EscapeForHtmlAttribute(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var c in value)
        {
            switch (c)
            {
                case '&':
                    builder.Append("&amp;");
                    break;
                case '"':
                    builder.Append("&quot;");
                    break;
                case '<':
                    builder.Append("&lt;");
                    break;
                case '>':
                    builder.Append("&gt;");
                    break;
                default:
                    builder.Append(c);
                    break;
            }
        }
        return builder.ToString();
    }
}
