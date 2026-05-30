// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
    public const string WorkspacesPrefix = "honua://workspaces/";
    public const string CatalogProcesses = "honua://catalog/processes";

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

    /// <summary>Builds the <c>honua://jobs/{jobId}/report</c> URI.</summary>
    public static string JobReportUri(string jobId) => $"{JobsPrefix}{jobId}{JobReportSuffix}";

    /// <summary>Builds the <c>honua://workspaces/{workspaceId}</c> URI.</summary>
    public static string WorkspaceUri(string workspaceId) => $"{WorkspacesPrefix}{workspaceId}";

    /// <summary>Builds the <c>honua://published-services/{serviceId}</c> URI.</summary>
    public static string PublishedServiceUri(string serviceId) => $"{PublishedServicesPrefix}{serviceId}";

    /// <summary>Builds the <c>honua://deployments/{deploymentId}</c> URI.</summary>
    public static string DeploymentUri(string deploymentId) => $"{DeploymentsPrefix}{deploymentId}";

    /// <summary>Builds the <c>honua://map-packages/{packageId}</c> URI.</summary>
    public static string MapPackageUri(string packageId) => $"{MapPackagesPrefix}{packageId}";

    /// <summary>Builds the <c>honua://app-packages/{packageId}</c> URI.</summary>
    public static string AppPackageUri(string packageId) => $"{AppPackagesPrefix}{packageId}";
}
