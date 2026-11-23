// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Net.Http.Headers;

namespace Honua.Infrastructure.Security;

// Response headers must be ASCII. Every download names its file through this
// builder: an ASCII filename fallback plus the UTF-8 name as filename*
// (RFC 6266 / RFC 5987), with quoting and escaping done by the header type.
internal static class AttachmentContentDisposition
{
    public static string Create(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        var header = new ContentDispositionHeaderValue("attachment");
        header.SetHttpFileName(fileName);
        return header.ToString();
    }
}
