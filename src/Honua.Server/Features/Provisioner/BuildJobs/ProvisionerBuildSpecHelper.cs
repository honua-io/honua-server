// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Provisioner.BuildJobs;

/// <summary>
/// Shared decode helper for the geocoder/router build spec builders. Both build jobs
/// carry the same set of common parameters (feedstock source/product, area, table,
/// schema, artifact name/key); only the artifact-kind key differs, so the common read +
/// validation lives here to keep the two <c>TryParse</c> paths DRY and consistent.
/// </summary>
internal static class ProvisionerBuildSpecHelper
{
    /// <summary>
    /// Reads and validates the parameters common to both build-job kinds, parsing and
    /// re-validating the area selector. Returns <c>false</c> with a caller-facing
    /// <paramref name="error"/> on the first missing/invalid value.
    /// </summary>
    public static bool TryReadCommon(
        IReadOnlyDictionary<string, string> parameters,
        out string sourceId,
        out string productId,
        out ProvisionerArea area,
        out string feedstockTable,
        out string schemaName,
        out string artifactName,
        out string artifactKey,
        out string error)
    {
        sourceId = string.Empty;
        productId = string.Empty;
        area = null!;
        feedstockTable = string.Empty;
        schemaName = "public";
        artifactName = string.Empty;
        artifactKey = string.Empty;
        error = string.Empty;

        if (!TryRequire(parameters, ProvisionerBuildJobParameterKeys.SourceId, out sourceId, out error)
            || !TryRequire(parameters, ProvisionerBuildJobParameterKeys.ProductId, out productId, out error)
            || !TryRequire(parameters, ProvisionerBuildJobParameterKeys.FeedstockTable, out feedstockTable, out error)
            || !TryRequire(parameters, ProvisionerBuildJobParameterKeys.ArtifactName, out artifactName, out error)
            || !TryRequire(parameters, ProvisionerBuildJobParameterKeys.ArtifactKey, out artifactKey, out error))
        {
            return false;
        }

        if (!TryRequire(parameters, ProvisionerBuildJobParameterKeys.Area, out var rawArea, out error))
        {
            return false;
        }

        if (!ProvisionerArea.TryParse(rawArea, out area, out error))
        {
            return false;
        }

        if (parameters.TryGetValue(ProvisionerBuildJobParameterKeys.SchemaName, out var schema)
            && !string.IsNullOrWhiteSpace(schema))
        {
            schemaName = schema;
        }

        return true;
    }

    private static bool TryRequire(
        IReadOnlyDictionary<string, string> parameters,
        string key,
        out string value,
        out string error)
    {
        if (parameters.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v))
        {
            value = v;
            error = string.Empty;
            return true;
        }

        value = string.Empty;
        error = $"missing required build parameter '{key}'";
        return false;
    }
}
