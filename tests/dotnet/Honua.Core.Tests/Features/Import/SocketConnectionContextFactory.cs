// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Reflection;
using FluentAssertions;

namespace Honua.Core.Tests.Features.Import;

internal static class SocketConnectionContextFactory
{
    internal static SocketsHttpConnectionContext Create(DnsEndPoint dnsEndPoint, HttpRequestMessage request)
    {
        // Internal constructor: (DnsEndPoint, HttpRequestMessage) on .NET 10; .NET 11 appends a
        // connection id. Match the leading parameters and default any that follow.
        var constructor = typeof(SocketsHttpConnectionContext)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .SingleOrDefault(candidate => candidate.GetParameters() is [{ ParameterType: var first }, { ParameterType: var second }, ..]
                && first == typeof(DnsEndPoint) && second == typeof(HttpRequestMessage));

        constructor.Should().NotBeNull();
        var arguments = constructor!.GetParameters()
            .Select((parameter, index) => index switch
            {
                0 => (object?)dnsEndPoint,
                1 => request,
                _ => parameter.ParameterType.IsValueType ? Activator.CreateInstance(parameter.ParameterType) : null
            })
            .ToArray();
        return (SocketsHttpConnectionContext)constructor.Invoke(arguments);
    }
}
