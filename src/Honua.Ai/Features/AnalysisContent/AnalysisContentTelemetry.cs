// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Diagnostics.Metrics;
using Honua.Core.Features.AnalysisContent.Domain;
using Honua.ServiceDefaults;

namespace Honua.Ai.AnalysisContent;

internal static class AnalysisContentTelemetry
{
    private const string SuccessResult = "success";
    private const string ErrorResult = "error";

    private static readonly Counter<long> Operations = HonuaTelemetry.Meter.CreateCounter<long>(
        "honua.analysis_content.operations_total",
        "operations",
        "Number of analysis content operations by operation and result.");

    internal static class OperationsNames
    {
        public const string CreateItem = "create_item";
        public const string GetItem = "get_item";
        public const string ListItems = "list_items";
        public const string EstimateVersion = "estimate_version";
        public const string GetVersion = "get_version";
        public const string AddVersion = "add_version";
        public const string PreviewSavedQuery = "preview_saved_query";
        public const string SubmitAnalysisPackage = "submit_analysis_package";
        public const string RerunAnalysisPackage = "rerun_analysis_package";
        public const string GetArtifact = "get_artifact";
        public const string GetJobLogs = "get_job_logs";
        public const string GetJobFailure = "get_job_failure";
    }

    internal static class Tags
    {
        public const string ContentItemId = "honua.analysis_content.item_id";
        public const string ContentKind = "honua.analysis_content.kind";
        public const string ContentVersion = "honua.analysis_content.version";
        public const string ContentVersionId = "honua.analysis_content.version_id";
        public const string ArtifactId = "honua.analysis_content.artifact_id";
        public const string Result = "honua.analysis_content.result";
        public const string LogLimit = "honua.analysis_content.log_limit";
    }

    public static async Task<T> TrackAsync<T>(
        string operation,
        Func<Activity?, Task<T>> action)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity(
            $"honua.analysis_content.{operation}",
            ActivityKind.Internal);
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.Admin);
        activity?.SetTag(HonuaTelemetry.Tags.Operation, operation);

        try
        {
            var result = await action(activity).ConfigureAwait(false);
            activity?.SetTag(Tags.Result, SuccessResult);
            HonuaTelemetry.SetSuccess(activity);
            Operations.Add(
                1,
                new KeyValuePair<string, object?>(HonuaTelemetry.Tags.Operation, operation),
                new KeyValuePair<string, object?>(Tags.Result, SuccessResult));
            return result;
        }
        catch (Exception ex)
        {
            activity?.SetTag(Tags.Result, ErrorResult);
            HonuaTelemetry.RecordException(activity, ex);
            Operations.Add(
                1,
                new KeyValuePair<string, object?>(HonuaTelemetry.Tags.Operation, operation),
                new KeyValuePair<string, object?>(Tags.Result, ErrorResult));
            throw;
        }
    }

    public static void SetContentTags(
        Activity? activity,
        string itemId,
        AnalysisContentKind kind,
        int? version = null,
        string? versionId = null)
    {
        activity?.SetTag(Tags.ContentItemId, itemId);
        activity?.SetTag(Tags.ContentKind, kind.ToString());
        if (version.HasValue)
        {
            activity?.SetTag(Tags.ContentVersion, version.Value);
        }

        if (!string.IsNullOrWhiteSpace(versionId))
        {
            activity?.SetTag(Tags.ContentVersionId, versionId);
        }
    }
}
