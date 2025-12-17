// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.TestKit.Attributes;

/// <summary>
/// Indicates which API endpoint(s) this test covers.
/// Used for 100% API surface coverage enforcement.
/// </summary>
/// <param name="endpoint">HTTP method and route pattern (e.g., "GET /healthz/live")</param>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class EndpointAttribute(string endpoint) : Attribute
{
    public string Endpoint { get; } = endpoint;
}
