// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Abstractions;

/// <summary>
/// Stable identity of this process, the value the server stamps as the <c>Honua-Instance</c>
/// response header (honua-server#5790). Exposed to slices that record which instance ran work.
/// </summary>
public interface IInstanceIdentity
{
    /// <summary>Identifier of this instance; stable for the life of the process.</summary>
    string InstanceId { get; }
}
