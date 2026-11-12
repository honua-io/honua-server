// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.AspNetCore.Http.HttpResults;

namespace Honua.Protocols.GeoServices.ImageServer;

internal static partial class ImageServerSoapEndpoints
{
    // exportImage answers with the encoded bytes either as a byte result (small or empty images) or as a
    // stream result over a buffer (encoded exports); SOAP embeds the bytes in XML.
    internal static bool TryGetExportedImageBytes(IResult exportResult, out ReadOnlyMemory<byte> imageData)
    {
        switch (exportResult)
        {
            case FileContentHttpResult { FileContents: var contents }:
                imageData = contents;
                return true;
            case FileStreamHttpResult { FileStream: MemoryStream stream } when stream.TryGetBuffer(out var buffer):
                imageData = buffer;
                return true;
            default:
                imageData = default;
                return false;
        }
    }
}
