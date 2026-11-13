// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Sockets;

namespace Honua.Infrastructure.RateLimiting;

// IPv6 aggregates to its /64 so one host cannot rotate interface identifiers to fill a
// process-local table; IPv4, including an address that arrived IPv4-mapped, stays a single address.
internal static class ClientAddressPartition
{
    internal static string? From(IPAddress? address)
    {
        if (address is null)
        {
            return null;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            bytes.AsSpan(8).Clear();
            address = new IPAddress(bytes);
        }

        return address.ToString();
    }
}
