// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;

namespace Honua.Core.Features.Studio.Services.Bridging;

public static class StudioBridgeIds
{
    public static Guid Derive(string ns, string nativeId)
    {
        ArgumentException.ThrowIfNullOrEmpty(ns);
        ArgumentNullException.ThrowIfNull(nativeId);

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"honua.studio.bridge:{ns}:{nativeId}"));
        var guidBytes = new byte[16];
        Array.Copy(bytes, guidBytes, 16);

        // Stamp RFC 4122 version 8 (custom) and variant bits so derived ids are well-formed
        // and cannot collide with the version-4 ids the Studio store generates.
        guidBytes[7] = (byte)((guidBytes[7] & 0x0F) | 0x80);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);
        return new Guid(guidBytes, bigEndian: true);
    }

    public static Guid ForNativeId(string ns, string nativeId)
        => Guid.TryParseExact(nativeId, "D", out var direct) ? direct : Derive(ns, nativeId);
}
