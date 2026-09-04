// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Protocols.Ogc.Api.Processes;

/// <summary>
/// Durable protocol choices needed when an asynchronous execution is later polled.
/// </summary>
internal static class OgcProcessesExecutionMetadata
{
    internal const string ResponseMode = "ogc.processes.response";

    internal static bool IsRaw(IReadOnlyDictionary<string, string> metadata)
        => metadata.TryGetValue(ResponseMode, out var mode)
           && string.Equals(mode, "raw", StringComparison.OrdinalIgnoreCase);

    internal static bool UsesValueTransmission(IReadOnlyDictionary<string, string> metadata)
        => metadata.ContainsKey(ResponseMode);
}
