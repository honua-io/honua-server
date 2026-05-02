// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Scene;

/// <summary>
/// Builds copy-paste embed snippets for hosted 3D Tiles datasets.
/// </summary>
/// <remarks>
/// Output is intentionally minimal so callers can paste into a CesiumJS
/// initialization block or into HTML using the upcoming
/// <c>&lt;honua-scene&gt;</c> custom element. The tileset URL is rendered
/// inside JSON-quoted or HTML-attribute contexts; both must be safe to
/// concatenate, so the URL is escaped before substitution.
/// </remarks>
public static class SceneSnippetGenerator
{
    /// <summary>
    /// Builds a CesiumJS initialization snippet that creates a
    /// <c>Cesium3DTileset</c> for the given absolute tileset URL.
    /// </summary>
    public static string BuildCesiumJs(string tilesetUrl)
    {
        ValidateTilesetUrl(tilesetUrl);
        return $"new Cesium.Cesium3DTileset({{ url: \"{EscapeForJsString(tilesetUrl)}\" }})";
    }

    /// <summary>
    /// Builds a custom-element tag for the given absolute tileset URL.
    /// </summary>
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
