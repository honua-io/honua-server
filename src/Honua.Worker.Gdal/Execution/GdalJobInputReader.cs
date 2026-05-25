// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Worker.Gdal.Execution;

/// <summary>
/// Shared helpers for reading the canonical step inputs the geoprocessing submit
/// path projects onto an <see cref="ExecutionJobSpec"/> parameter bag.
/// </summary>
internal static class GdalJobInputReader
{
    /// <summary>
    /// Resolves the first canonical process id from the durable spec, mirroring the
    /// lean dispatch helper's resolution order.
    /// </summary>
    public static string? ResolveProcessId(IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        if (parameters.TryGetValue(GdalWorkerParameterKeys.ProcessDefinitions, out var raw)
            && !string.IsNullOrWhiteSpace(raw))
        {
            var first = raw
                .Split(
                    GdalWorkerParameterKeys.MetadataListSeparator,
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(first))
            {
                return first;
            }
        }

        if (parameters.TryGetValue("protocolProcessId", out var protocolProcessId)
            && !string.IsNullOrWhiteSpace(protocolProcessId))
        {
            return protocolProcessId;
        }

        return null;
    }

    /// <summary>
    /// Reads a required step-input string value.
    /// </summary>
    public static bool TryGetInput(
        IReadOnlyDictionary<string, string> parameters,
        string name,
        out string value)
    {
        if (parameters.TryGetValue(GdalWorkerParameterKeys.StepInputPrefix + name, out var raw)
            && !string.IsNullOrWhiteSpace(raw))
        {
            value = raw;
            return true;
        }

        value = "";
        return false;
    }

    /// <summary>
    /// Reads and decodes a required base64-encoded source payload step input.
    /// </summary>
    public static bool TryGetBase64Input(
        IReadOnlyDictionary<string, string> parameters,
        string name,
        out byte[] bytes,
        out string error)
    {
        bytes = [];
        error = "";

        if (!TryGetInput(parameters, name, out var raw))
        {
            error = $"missing required input '{name}'";
            return false;
        }

        try
        {
            bytes = Convert.FromBase64String(raw);
        }
        catch (FormatException)
        {
            error = $"input '{name}' is not valid base64";
            return false;
        }

        if (bytes.Length == 0)
        {
            error = $"input '{name}' decoded to zero bytes";
            return false;
        }

        return true;
    }
}
