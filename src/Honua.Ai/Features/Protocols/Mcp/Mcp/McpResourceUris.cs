// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Ai.Protocols.Mcp;

/// <summary>
/// Canonical URI strings and prefixes for MCP resources advertised by the
/// operator surface. Centralized so the resource registry, taxonomy-alignment
/// tests, and tool outputs reference the same values.
/// </summary>
internal static class McpResourceUris
{
    public const string Scheme = "honua";

    public const string JobsPrefix = "honua://jobs/";
    public const string JobResultsSuffix = "/results";
    public const string JobReportSuffix = "/report";
    public const string ResultsPrefix = "honua://results/";
    public const string WorkspacesPrefix = "honua://workspaces/";
    public const string ProposalsPrefix = "honua://proposals/";
    public const string CatalogProcesses = "honua://catalog/processes";
    public const string CatalogFeatures = "honua://catalog/features";
    public const string OpsHealth = "honua://ops/health";
    public const string OpsFindings = "honua://ops/findings";

    public const string PublishedServicesRoot = "honua://published-services";
    public const string PublishedServicesPrefix = "honua://published-services/";
    public const string DeploymentsRoot = "honua://deployments";
    public const string DeploymentsPrefix = "honua://deployments/";
    public const string MapPackagesRoot = "honua://map-packages";
    public const string MapPackagesPrefix = "honua://map-packages/";
    public const string AppPackagesRoot = "honua://app-packages";
    public const string AppPackagesPrefix = "honua://app-packages/";

    /// <summary>Builds the <c>honua://jobs/{jobId}</c> URI.</summary>
    public static string JobUri(string jobId) => $"{JobsPrefix}{jobId}";

    /// <summary>Builds the <c>honua://jobs/{jobId}/results</c> URI.</summary>
    public static string JobResultsUri(string jobId) => $"{JobsPrefix}{jobId}{JobResultsSuffix}";

    /// <summary>Builds the <c>honua://results/{resultPackageId}</c> URI.</summary>
    public static string ResultPackageUri(string resultPackageId) => $"{ResultsPrefix}{resultPackageId}";

    /// <summary>Builds the <c>honua://jobs/{jobId}/report</c> URI.</summary>
    public static string JobReportUri(string jobId) => $"{JobsPrefix}{jobId}{JobReportSuffix}";

    /// <summary>Builds the <c>honua://proposals/{proposalId}</c> URI.</summary>
    public static string ProposalUri(string proposalId) => $"{ProposalsPrefix}{proposalId}";

    /// <summary>Builds the <c>honua://workspaces/{workspaceId}</c> URI.</summary>
    public static string WorkspaceUri(string workspaceId) => $"{WorkspacesPrefix}{workspaceId}";

    /// <summary>Builds the <c>honua://published-services/{serviceId}</c> URI.</summary>
    public static string PublishedServiceUri(string serviceId)
    {
        ArgumentException.ThrowIfNullOrEmpty(serviceId);
        return PublishedServicesPrefix + Uri.EscapeDataString(serviceId);
    }

    /// <summary>Parses one canonically encoded service identity without decoding it twice.</summary>
    public static bool TryParsePublishedServiceUri(string uri, out string serviceId)
    {
        serviceId = string.Empty;
        if (uri is null || !uri.StartsWith(PublishedServicesPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var encoded = uri[PublishedServicesPrefix.Length..];
        if (encoded.Length == 0 || encoded.Contains('/'))
        {
            return false;
        }

        var decoded = Uri.UnescapeDataString(encoded);
        if (decoded.Length == 0 || !string.Equals(PublishedServiceUri(decoded), uri, StringComparison.Ordinal))
        {
            return false;
        }

        serviceId = decoded;
        return true;
    }

    /// <summary>Builds the <c>honua://deployments/{deploymentId}</c> URI.</summary>
    public static string DeploymentUri(string deploymentId) => $"{DeploymentsPrefix}{deploymentId}";

    /// <summary>Builds the <c>honua://map-packages/{packageId}</c> URI.</summary>
    public static string MapPackageUri(string packageId) => $"{MapPackagesPrefix}{packageId}";

    /// <summary>Builds the <c>honua://app-packages/{packageId}</c> URI.</summary>
    public static string AppPackageUri(string packageId) => $"{AppPackagesPrefix}{packageId}";
}
