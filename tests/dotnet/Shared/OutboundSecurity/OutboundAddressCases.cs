// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Xunit;

namespace Honua.Tests.OutboundSecurity;

/// <summary>
/// The address table every outbound destination guard must agree on. Each row is an address and
/// the range it belongs to.
/// </summary>
public static class OutboundAddressCases
{
    public static TheoryData<string, string> Disallowed => new()
    {
        { "0.0.0.0", "IPv4 this-network" },
        { "10.1.2.3", "IPv4 private 10/8" },
        { "100.64.0.1", "IPv4 shared address space" },
        { "127.0.0.1", "IPv4 loopback" },
        { "169.254.169.254", "IPv4 link-local" },
        { "169.254.170.2", "IPv4 link-local" },
        { "172.16.0.1", "IPv4 private 172.16/12" },
        { "192.0.0.8", "IPv4 IETF protocol assignments" },
        { "192.0.2.1", "IPv4 documentation" },
        { "192.168.1.1", "IPv4 private 192.168/16" },
        { "198.18.0.1", "IPv4 benchmarking" },
        { "224.0.0.1", "IPv4 multicast" },
        { "255.255.255.255", "IPv4 broadcast" },
        { "::", "IPv6 unspecified" },
        { "::1", "IPv6 loopback" },
        { "::ffff:10.0.0.1", "IPv4-mapped private" },
        { "::ffff:169.254.169.254", "IPv4-mapped link-local" },
        { "fc00::1", "IPv6 unique local" },
        { "fd12:3456::1", "IPv6 unique local" },
        { "fe80::1", "IPv6 link-local" },
        { "fec0::1", "IPv6 site-local" },
        { "ff02::1", "IPv6 multicast" },
        { "ff05::2", "IPv6 multicast" },
        { "2001:db8::1", "IPv6 documentation" },
        { "2001:0:4136:e378:8000:63bf:3fff:fdd2", "Teredo" },
        { "2002:a9fe:a9fe::1", "6to4 embedding link-local IPv4" },
        { "2002:0a00:0001::1", "6to4 embedding private IPv4" },
        { "64:ff9b::a9fe:a9fe", "NAT64 embedding link-local IPv4" },
        { "64:ff9b::a00:1", "NAT64 embedding private IPv4" },
        { "64:ff9b:1::a9fe:a9fe", "local-use NAT64 embedding link-local IPv4" },
    };

    public static TheoryData<string, string> Allowed => new()
    {
        { "8.8.8.8", "public IPv4" },
        { "93.184.216.34", "public IPv4" },
        { "::ffff:8.8.8.8", "IPv4-mapped public" },
        { "2606:4700:4700::1111", "public IPv6" },
        { "2002:0808:0808::1", "6to4 embedding public IPv4" },
        { "64:ff9b::808:808", "NAT64 embedding public IPv4" },
    };
}
