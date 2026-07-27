// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;

namespace Honua.Core.Features.Studio.Services.Bridging;

/// <summary>
/// Deterministic GUID derivation for bridged Studio item and version identifiers
/// (ADR-0069). Native stores key content by strings (form ids, <c>itm_*</c> analysis ids,
/// <c>{itemId}:v{n}</c> version ids); the Studio lifecycle keys everything by GUID. Ids that
/// already round-trip as GUIDs map directly; anything else maps through a stable SHA-256
/// derivation so the same native record always projects the same Studio id.
/// </summary>
public static class StudioBridgeIds
{
    /// <summary>
    /// Derives a stable GUID from a namespaced native identifier. The derivation is one-way;
    /// reverse resolution enumerates native ids and re-derives.
    /// </summary>
    /// <param name="ns">Bridge-specific namespace (for example <c>form</c>).</param>
    /// <param name="nativeId">Native identifier.</param>
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

    /// <summary>
    /// Maps a native string id to a Studio GUID: a native id that parses as a canonical GUID
    /// maps directly (so lifecycle-created records round-trip losslessly); anything else uses
    /// the deterministic derivation.
    /// </summary>
    public static Guid ForNativeId(string ns, string nativeId)
        => Guid.TryParseExact(nativeId, "D", out var direct) ? direct : Derive(ns, nativeId);
}
