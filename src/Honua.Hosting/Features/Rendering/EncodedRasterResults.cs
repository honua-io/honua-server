// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.AspNetCore.Http;

namespace Honua.Infrastructure.Rendering;

// Encoded raster exports reach tens of MB. Results.File(byte[]) hands the whole array to the response pipe
// in one write, which copies all of it into pipe memory before the first flush; a stream result copies it
// in flushed chunks. Headers (Content-Type, Content-Length) are the same.
internal static class EncodedRasterResults
{
    public static IResult File(byte[] data, string contentType)
        => Results.File(new MemoryStream(data, writable: false), contentType);
}
