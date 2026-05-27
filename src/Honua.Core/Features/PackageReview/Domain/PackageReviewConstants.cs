// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.PackageReview.Domain;

/// <summary>
/// Stable identifiers for the package-review contract.
/// </summary>
public static class PackageReviewContract
{
    /// <summary>
    /// Current package-review response contract version.
    /// </summary>
    public const string Version = "honua.package_review.v1";
}

/// <summary>
/// Package families accepted by the package-review contract.
/// </summary>
public static class PackageReviewFamilies
{
    /// <summary>Feature or catalog query package.</summary>
    public const string Query = "query";

    /// <summary>Analysis plan package.</summary>
    public const string AnalysisPlan = "analysis_plan";

    /// <summary>Map package.</summary>
    public const string MapPackage = "map_package";

    /// <summary>Dashboard or report data package.</summary>
    public const string DashboardReport = "dashboard_report";

    /// <summary>Field-collection form package.</summary>
    public const string Form = "form";

    /// <summary>Generated application package.</summary>
    public const string AppPackage = "app_package";

    /// <summary>Workflow package.</summary>
    public const string Workflow = "workflow";

    /// <summary>Geoprocessing package.</summary>
    public const string Geoprocessing = "gp";

    /// <summary>ETL package.</summary>
    public const string Etl = "etl";

    /// <summary>
    /// Determines whether a package family is part of the published review contract.
    /// </summary>
    public static bool IsKnown(string? family)
        => string.Equals(family, Query, StringComparison.Ordinal) ||
           string.Equals(family, AnalysisPlan, StringComparison.Ordinal) ||
           string.Equals(family, MapPackage, StringComparison.Ordinal) ||
           string.Equals(family, DashboardReport, StringComparison.Ordinal) ||
           string.Equals(family, Form, StringComparison.Ordinal) ||
           string.Equals(family, AppPackage, StringComparison.Ordinal) ||
           string.Equals(family, Workflow, StringComparison.Ordinal) ||
           string.Equals(family, Geoprocessing, StringComparison.Ordinal) ||
           string.Equals(family, Etl, StringComparison.Ordinal);
}

/// <summary>
/// Package-review status values.
/// </summary>
public static class PackageReviewStatus
{
    /// <summary>Review completed with no blocking or warning findings.</summary>
    public const string Ready = "ready";

    /// <summary>Review completed with warnings but no blockers.</summary>
    public const string Warning = "warning";

    /// <summary>Review found one or more unresolved blockers.</summary>
    public const string Blocked = "blocked";
}

/// <summary>
/// Package finding severity values.
/// </summary>
public static class PackageFindingSeverity
{
    /// <summary>Blocks execution or publication until resolved.</summary>
    public const string Blocker = "blocker";

    /// <summary>Requires review but does not block by itself.</summary>
    public const string Warning = "warning";

    /// <summary>Informational finding.</summary>
    public const string Info = "info";

    /// <summary>Optional positive finding emitted when pass findings are requested.</summary>
    public const string Pass = "pass";
}

/// <summary>
/// Common package finding categories.
/// </summary>
public static class PackageFindingCategory
{
    /// <summary>Package data binding category.</summary>
    public const string DataBinding = "data_binding";

    /// <summary>Permission or authorization category.</summary>
    public const string Permission = "permission";

    /// <summary>RBAC policy category.</summary>
    public const string Rbac = "rbac";

    /// <summary>Schema compatibility category.</summary>
    public const string Schema = "schema";

    /// <summary>Field compatibility category.</summary>
    public const string Field = "field";

    /// <summary>Domain or coded-value compatibility category.</summary>
    public const string Domain = "domain";

    /// <summary>Coordinate reference system category.</summary>
    public const string Crs = "crs";

    /// <summary>Geometry type category.</summary>
    public const string Geometry = "geometry";

    /// <summary>Temporal metadata category.</summary>
    public const string Temporal = "temporal";

    /// <summary>Service or server capability category.</summary>
    public const string Capability = "capability";

    /// <summary>Package dependency category.</summary>
    public const string Dependency = "dependency";

    /// <summary>Package format category.</summary>
    public const string Format = "format";

    /// <summary>Approval or admission-control category.</summary>
    public const string Approval = "approval";

    /// <summary>Preview planning category.</summary>
    public const string Preview = "preview";
}

/// <summary>
/// Package finding disposition values.
/// </summary>
public static class PackageFindingDisposition
{
    /// <summary>The finding remains unresolved.</summary>
    public const string Unresolved = "unresolved";

    /// <summary>The finding is resolved or passed.</summary>
    public const string Resolved = "resolved";

    /// <summary>Authorization denied the requested action.</summary>
    public const string AuthDenied = "auth_denied";

    /// <summary>The requested capability is not supported.</summary>
    public const string Unsupported = "unsupported";

    /// <summary>The package requires explicit approval before continuation.</summary>
    public const string RequiresApproval = "requires_approval";
}

/// <summary>
/// Package finding action scopes.
/// </summary>
public static class PackageReviewActionScope
{
    /// <summary>The finding applies to both execute and publish actions.</summary>
    public const string Both = "both";

    /// <summary>The finding applies to execute actions.</summary>
    public const string Execute = "execute";

    /// <summary>The finding applies to publish actions.</summary>
    public const string Publish = "publish";

    /// <summary>The finding applies to review only.</summary>
    public const string Review = "review";
}
