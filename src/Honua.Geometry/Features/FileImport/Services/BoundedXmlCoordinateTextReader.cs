// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using System.Xml;

namespace Honua.Core.Features.FileImport.Services;

internal static class BoundedXmlCoordinateTextReader
{
    internal const int MaxXmlDepth = 64;
    internal const long MaxDocumentCharacters = 256L * 1024 * 1024;
    internal const int MaxCoordinateStringLength = 16 * 1024 * 1024;
    internal const int MaxCoordinateVertices = 100_000;
    internal const int MaxPolygonRings = 1_000;

    internal static XmlReaderSettings CreateReaderSettings()
        => new()
        {
            Async = true,
            IgnoreWhitespace = true,
            IgnoreComments = true,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxDocumentCharacters,
            MaxCharactersFromEntities = 0
        };

    internal static void EnsureXmlDepth(XmlReader reader, string formatLabel)
    {
        if (reader.Depth > MaxXmlDepth)
        {
            throw new InvalidDataException($"{formatLabel} XML nesting depth exceeds the maximum of {MaxXmlDepth}.");
        }
    }

    internal static void EnsureCoordinateLength(string text, string formatLabel)
    {
        if (text.Length > MaxCoordinateStringLength)
        {
            throw new InvalidDataException(
                $"{formatLabel} coordinate text exceeds the maximum length of {MaxCoordinateStringLength:N0} characters.");
        }
    }

    internal static void EnsureVertexCount(int vertexCount, string formatLabel)
    {
        if (vertexCount > MaxCoordinateVertices)
        {
            throw new InvalidDataException(
                $"{formatLabel} geometry vertex count ({vertexCount:N0}) exceeds the maximum of {MaxCoordinateVertices:N0}.");
        }
    }

    internal static void AppendBoundedText(StringBuilder builder, string chunk, string formatLabel)
    {
        if (chunk.Length > MaxCoordinateStringLength - builder.Length)
        {
            throw new InvalidDataException(
                $"{formatLabel} text content exceeds the maximum length of {MaxCoordinateStringLength:N0} characters.");
        }

        builder.Append(chunk);
    }

    internal static async Task<string> ReadElementTextAsync(
        XmlReader reader,
        string formatLabel,
        CancellationToken cancellationToken)
    {
        if (reader.IsEmptyElement)
        {
            return string.Empty;
        }

        var startDepth = reader.Depth;
        var builder = new StringBuilder();
        while (await reader.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureXmlDepth(reader, formatLabel);

            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == startDepth)
            {
                break;
            }

            if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace)
            {
                AppendBoundedText(builder, reader.Value, formatLabel);
            }
        }

        return builder.ToString();
    }
}
