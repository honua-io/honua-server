// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;

namespace Honua.Routing.Features.Routing.Domain;

/// <summary>Stable public identifiers for provider-advertised travel modes.</summary>
public static class RoutingTravelModeId
{
    /// <summary>Returns the identifier used by routing utility metadata.</summary>
    public static string Create(string name)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("honua-travel-mode:" + name.ToLowerInvariant()));
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
        var id = new char[16];
        for (var i = 0; i < id.Length; i++) id[i] = alphabet[hash[i] % alphabet.Length];
        return new string(id);
    }
}
