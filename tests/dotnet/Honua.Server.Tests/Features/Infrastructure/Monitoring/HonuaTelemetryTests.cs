// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using Honua.ServiceDefaults;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Infrastructure.Monitoring;

[Protocol(TestProtocols.TestQuality)]
[Collection("HonuaTelemetry")]
public sealed class HonuaTelemetryTests
{
    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void TracingOptions_Defaults_AreSafe()
    {
        var options = new TracingOptions();

        Assert.False(options.ExportExceptionDetails);
        Assert.False(options.RecordExceptionStackTraces);
        Assert.Equal(256, options.MaxExceptionDetailLength);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void RecordException_WhenDetailsDisabled_OmitsMessageAndStackTrace()
    {
        var activity = new Activity("telemetry-test").Start();

        try
        {
            HonuaTelemetry.ConfigureExceptionRecording(exportDetails: false, includeStackTraces: false, maxDetailLength: 64);
            HonuaTelemetry.RecordException(activity, CaptureException("token=abc123 contact=test@example.com"));

            Assert.Equal(ActivityStatusCode.Error, activity.Status);
            Assert.Null(activity.GetTagItem(HonuaTelemetry.Tags.ErrorMessage));

            var exceptionEvent = Assert.Single(activity.Events);
            Assert.Equal("exception", exceptionEvent.Name);
            Assert.NotNull(GetEventTag(exceptionEvent, "exception.type"));
            Assert.Null(GetEventTag(exceptionEvent, "exception.message"));
            Assert.Null(GetEventTag(exceptionEvent, "exception.stacktrace"));
        }
        finally
        {
            activity.Dispose();
            HonuaTelemetry.ConfigureExceptionRecording(exportDetails: false, includeStackTraces: false);
        }
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void RecordException_WhenDetailsEnabled_SanitizesExportedMessage()
    {
        var activity = new Activity("telemetry-test").Start();

        try
        {
            HonuaTelemetry.ConfigureExceptionRecording(exportDetails: true, includeStackTraces: false, maxDetailLength: 64);
            HonuaTelemetry.RecordException(activity, CaptureException("Bearer abc123 password=hunter2 email=test@example.com"));

            var errorMessage = activity.GetTagItem(HonuaTelemetry.Tags.ErrorMessage) as string;
            Assert.NotNull(errorMessage);
            Assert.Contains("Bearer ***", errorMessage!, StringComparison.Ordinal);
            Assert.Contains("password=***", errorMessage, StringComparison.Ordinal);
            Assert.DoesNotContain("abc123", errorMessage, StringComparison.Ordinal);
            Assert.DoesNotContain("test@example.com", errorMessage, StringComparison.Ordinal);

            var exceptionEvent = Assert.Single(activity.Events);
            var eventMessage = GetEventTag(exceptionEvent, "exception.message");
            Assert.Equal(errorMessage, eventMessage);
            Assert.Null(GetEventTag(exceptionEvent, "exception.stacktrace"));
        }
        finally
        {
            activity.Dispose();
            HonuaTelemetry.ConfigureExceptionRecording(exportDetails: false, includeStackTraces: false);
        }
    }

    private static Exception CaptureException(string message)
    {
        try
        {
            throw new InvalidOperationException(message);
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static string? GetEventTag(ActivityEvent activityEvent, string tagName)
    {
        foreach (var tag in activityEvent.Tags)
        {
            if (string.Equals(tag.Key, tagName, StringComparison.Ordinal))
            {
                return tag.Value?.ToString();
            }
        }

        return null;
    }
}
