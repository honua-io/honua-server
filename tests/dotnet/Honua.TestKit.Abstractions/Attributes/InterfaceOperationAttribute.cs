// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.TestKit.Attributes;

/// <summary>
/// Maps a test to a public-interface operation tracked by <see cref="Server.OperationRegistry"/>.
/// Used alongside <see cref="EndpointAttribute"/> to enforce integration test coverage
/// for operations dispatched within a single HTTP route (WFS 2.0) or via non-HTTP protocols (gRPC).
/// </summary>
/// <param name="protocol">Protocol identifier (e.g. "WFS-2.0", "Grpc").</param>
/// <param name="operation">Operation name matching an entry in <see cref="Server.OperationRegistry.All"/>.</param>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class InterfaceOperationAttribute(string protocol, string operation) : Attribute
{
    public string Protocol { get; } = protocol;
    public string Operation { get; } = operation;
}
