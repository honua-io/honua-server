// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using Honua.Core.Features.Infrastructure.Monitoring;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Core.Tests.Features.Infrastructure.Monitoring;

[Trait("Tier", "Fast")]
public sealed class DatabaseQueryPerformanceMonitorTests
{
    // Each test listens to its own source name: ActivitySource listeners are process-wide,
    // so a shared name would let parallel tests count each other's commands.
    private readonly string _sourceName = "Honua.Tests.DbClient." + Guid.NewGuid().ToString("N");

    [Fact]
    public void Default_sources_are_the_database_clients_that_emit_command_activities()
    {
        var options = new QueryPerformanceMonitoringOptions();

        Assert.Equal(new[] { "Npgsql", "MySqlConnector" }, options.ActivitySourceNames);
    }

    [Fact]
    public void Completed_commands_on_a_database_client_source_feed_the_statistics()
    {
        using var monitor = CreateMonitor();
        using var source = new ActivitySource(_sourceName);

        RunCommand(source, ("db.operation.name", "select"), ("db.query.text", "SELECT 1"));
        RunCommand(source, ("db.query.text", "  update features set name = $1"));
        RunCommand(source, ("db.stored_procedure.name", "refresh_tiles"));

        var statistics = monitor.GetStatistics();
        Assert.Equal(3, statistics.TotalQueries);
        Assert.Equal(0, statistics.FailedQueries);
        Assert.Equal(new[] { "CALL", "SELECT", "UPDATE" }, statistics.ByQueryType.Keys.Order());
    }

    [Fact]
    public void Failed_commands_are_counted_as_failed()
    {
        using var monitor = CreateMonitor();
        using var source = new ActivitySource(_sourceName);

        RunCommand(source, ActivityStatusCode.Error, ("db.query.text", "DELETE FROM t"));
        RunCommand(source, ActivityStatusCode.Unset, ("db.query.text", "INSERT INTO t VALUES (1)"), ("error.type", "57014"));

        var statistics = monitor.GetStatistics();
        Assert.Equal(2, statistics.TotalQueries);
        Assert.Equal(2, statistics.FailedQueries);
    }

    [Fact]
    public void Activities_without_a_command_and_other_sources_are_ignored()
    {
        using var monitor = CreateMonitor();
        using var source = new ActivitySource(_sourceName);
        using var unrelated = new ActivitySource(_sourceName + ".Other");

        RunCommand(source, ("db.system.name", "postgresql"));
        RunCommand(unrelated, ("db.query.text", "SELECT 1"));

        Assert.Equal(0, monitor.GetStatistics().TotalQueries);
    }

    [Fact]
    public void Slow_commands_are_listed_with_row_count_and_without_text_by_default()
    {
        using var monitor = CreateMonitor(options => options.SlowQueryThresholdMs = 0);
        using var source = new ActivitySource(_sourceName);

        RunCommand(source, ("db.query.text", "SELECT secret FROM accounts"), ("db.npgsql.rows", 7L));

        var slow = Assert.Single(monitor.GetRecentSlowQueries());
        Assert.Equal("SELECT", slow.QueryType);
        Assert.Equal(7, slow.RowCount);
        Assert.Null(slow.QueryText);
        Assert.Equal(32, slow.CorrelationId.Length);
    }

    [Fact]
    public void Slow_command_text_is_kept_only_when_capture_is_enabled()
    {
        using var monitor = CreateMonitor(options => { options.SlowQueryThresholdMs = 0; options.CaptureQueryText = true; });
        using var source = new ActivitySource(_sourceName);

        RunCommand(source, ("db.query.text", "SELECT name FROM layers"));

        Assert.Equal("SELECT name FROM layers", Assert.Single(monitor.GetRecentSlowQueries()).QueryText);
    }

    [Fact]
    public void A_disposed_monitor_stops_listening()
    {
        var monitor = CreateMonitor();
        using var source = new ActivitySource(_sourceName);
        monitor.Dispose();

        RunCommand(source, ("db.query.text", "SELECT 1"));

        Assert.False(source.HasListeners());
        Assert.Equal(0, monitor.GetStatistics().TotalQueries);
    }

    private DatabaseQueryPerformanceMonitor CreateMonitor(Action<QueryPerformanceMonitoringOptions>? configure = null)
    {
        var options = new QueryPerformanceMonitoringOptions
        {
            SlowQueryThresholdMs = 60_000,
            CriticalSlowQueryThresholdMs = 120_000,
        };
        options.ActivitySourceNames.Clear();
        options.ActivitySourceNames.Add(_sourceName);
        configure?.Invoke(options);
        return new DatabaseQueryPerformanceMonitor(
            NullLogger<DatabaseQueryPerformanceMonitor>.Instance,
            Options.Create(options));
    }

    private static void RunCommand(ActivitySource source, params (string Key, object Value)[] tags) =>
        RunCommand(source, ActivityStatusCode.Unset, tags);

    private static void RunCommand(ActivitySource source, ActivityStatusCode status, params (string Key, object Value)[] tags)
    {
        using var activity = source.StartActivity("command", ActivityKind.Client);
        if (activity is null)
            return;
        foreach (var (key, value) in tags)
            activity.SetTag(key, value);
        activity.SetStatus(status);
    }
}
