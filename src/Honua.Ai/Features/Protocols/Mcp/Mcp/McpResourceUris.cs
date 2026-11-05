// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Ai.Protocols.Mcp;

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

    public static string JobUri(string jobId) => $"{JobsPrefix}{jobId}";

    public static string JobResultsUri(string jobId) => $"{JobsPrefix}{jobId}{JobResultsSuffix}";

    public static string ResultPackageUri(string resultPackageId) => $"{ResultsPrefix}{resultPackageId}";

    public static string JobReportUri(string jobId) => $"{JobsPrefix}{jobId}{JobReportSuffix}";

    public static string ProposalUri(string proposalId) => $"{ProposalsPrefix}{proposalId}";

    public static string WorkspaceUri(string workspaceId) => $"{WorkspacesPrefix}{workspaceId}";

    public static string PublishedServiceUri(string serviceId)
    {
        ArgumentException.ThrowIfNullOrEmpty(serviceId);
        return PublishedServicesPrefix + Uri.EscapeDataString(serviceId);
    }

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

    public static string DeploymentUri(string deploymentId) => $"{DeploymentsPrefix}{deploymentId}";

    public static string MapPackageUri(string packageId) => $"{MapPackagesPrefix}{packageId}";

    public static string AppPackageUri(string packageId) => $"{AppPackagesPrefix}{packageId}";
}
