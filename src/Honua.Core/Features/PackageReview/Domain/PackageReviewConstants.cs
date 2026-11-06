// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.PackageReview.Domain;

public static class PackageReviewContract
{
    public const string Version = "honua.package_review.v1";
}

public static class PackageReviewFamilies
{
    public const string Query = "query";

    public const string AnalysisPlan = "analysis_plan";

    public const string MapPackage = "map_package";

    public const string DashboardReport = "dashboard_report";

    public const string Form = "form";

    public const string AppPackage = "app_package";

    public const string Workflow = "workflow";

    public const string Geoprocessing = "gp";

    public const string Etl = "etl";

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

public static class PackageReviewStatus
{
    public const string Ready = "ready";

    public const string Warning = "warning";

    public const string Blocked = "blocked";
}

public static class PackageFindingSeverity
{
    public const string Blocker = "blocker";

    public const string Warning = "warning";

    public const string Info = "info";

    public const string Pass = "pass";
}

public static class PackageFindingCategory
{
    public const string DataBinding = "data_binding";

    public const string Permission = "permission";

    public const string Rbac = "rbac";

    public const string Schema = "schema";

    public const string Field = "field";

    public const string Domain = "domain";

    public const string Crs = "crs";

    public const string Geometry = "geometry";

    public const string Temporal = "temporal";

    public const string Capability = "capability";

    public const string Dependency = "dependency";

    public const string Format = "format";

    public const string Approval = "approval";

    public const string Preview = "preview";
}

public static class PackageFindingDisposition
{
    public const string Unresolved = "unresolved";

    public const string Resolved = "resolved";

    public const string AuthDenied = "auth_denied";

    public const string Unsupported = "unsupported";

    public const string RequiresApproval = "requires_approval";
}

public static class PackageReviewActionScope
{
    public const string Both = "both";

    public const string Execute = "execute";

    public const string Publish = "publish";

    public const string Review = "review";
}
