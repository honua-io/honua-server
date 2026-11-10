// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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
        => metadata.ContainsKey(ResponseMode)
           && (!metadata.TryGetValue("protocolProcessId", out var processId)
               || !string.Equals(processId, "honua-geoprocessing", StringComparison.Ordinal));
}
