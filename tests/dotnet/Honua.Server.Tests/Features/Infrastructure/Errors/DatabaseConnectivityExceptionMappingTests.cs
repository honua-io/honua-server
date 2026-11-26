// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net.Sockets;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.Core.Features.Infrastructure.Resilience;
using Honua.Infrastructure.Models;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Npgsql;

namespace Honua.Server.Tests.Features.Infrastructure.Errors;

public sealed class DatabaseConnectivityExceptionMappingTests
{
    [UnitTheory]
    [InlineData("08000")]
    [InlineData("08001")]
    [InlineData("08003")]
    [InlineData("08006")]
    [InlineData("53000")]
    [InlineData("53100")]
    [InlineData("53200")]
    [InlineData("53300")]
    [InlineData("53400")]
    [InlineData("57P01")]
    [InlineData("57P02")]
    [InlineData("57P03")]
    public void Map_TransientPostgresSqlState_ReturnsRetryable503WithoutLeakingDetails(string sqlState)
    {
        var exception = BuildPostgresException(sqlState, "Host=db.internal;Password=secret");

        var result = ExceptionMapper.Map(exception, includeDebugDetails: true);

        result.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        result.Title.Should().Be("Service Unavailable");
        ExceptionMapper.GetRetryAfterSeconds(exception).Should().Be(ExceptionMapper.DatabaseUnavailableRetryAfterSeconds);
        result.DebugInfo.Should().BeNull();
        result.Detail.Should().NotContain("db.internal").And.NotContain("secret");
    }

    [UnitTheory]
    [InlineData("42601")]
    [InlineData("42P01")]
    [InlineData("23505")]
    [InlineData("28P01")]
    [InlineData("57014")]
    public void Map_PermanentPostgresSqlState_Remains500(string sqlState)
    {
        var exception = BuildPostgresException(sqlState, "permanent sql error");

        var result = ExceptionMapper.Map(exception);

        result.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        ExceptionMapper.GetRetryAfterSeconds(exception).Should().BeNull();
    }

    [UnitTest]
    public void Map_TransientBareNpgsqlConnectivityAndPoolExhaustion_ReturnsRetryable503()
    {
        var refused = new NpgsqlException(
            "Failed to connect to 127.0.0.1:5432",
            new SocketException((int)SocketError.ConnectionRefused));
        var brokenStream = new NpgsqlException(
            "Exception while reading from stream",
            new IOException("Unable to read data from the transport connection"));
        var poolExhausted = new NpgsqlException(
            "The connection pool has been exhausted.",
            new TimeoutException("Timeout during connection attempt"));

        foreach (var exception in new[] { refused, brokenStream, poolExhausted })
        {
            var result = ExceptionMapper.Map(exception, includeDebugDetails: true);
            result.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
            ExceptionMapper.GetRetryAfterSeconds(exception).Should().Be(ExceptionMapper.DatabaseUnavailableRetryAfterSeconds);
            result.DebugInfo.Should().BeNull();
        }
    }

    [UnitTest]
    public void Map_PermanentBareNpgsqlTlsOrAuthFailure_Remains500()
    {
        var tlsFailure = new NpgsqlException(
            "The remote certificate was rejected by the provided RemoteCertificateValidationCallback.");
        var wrappedAuthFailure = new NpgsqlException(
            "Failed to connect",
            BuildPostgresException("28P01", "password authentication failed"));

        tlsFailure.IsTransient.Should().BeFalse();
        ExceptionMapper.Map(tlsFailure).StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        ExceptionMapper.GetRetryAfterSeconds(tlsFailure).Should().BeNull();
        ExceptionMapper.Map(wrappedAuthFailure).StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        ExceptionMapper.GetRetryAfterSeconds(wrappedAuthFailure).Should().BeNull();
    }

    [UnitTest]
    public void Map_WrappedTransientNpgsqlException_ReturnsRetryable503()
    {
        var wrapped = new InvalidOperationException(
            "Transient failure",
            BuildPostgresException("57P02", "crash shutdown"));

        var result = ExceptionMapper.Map(wrapped);

        result.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        ExceptionMapper.GetRetryAfterSeconds(wrapped).Should().Be(ExceptionMapper.DatabaseUnavailableRetryAfterSeconds);
    }

    [UnitTest]
    public void Map_AggregateDatabaseErrors_Maps503OnlyWhenEveryLeafIsTransientIncludingWhenWrapped()
    {
        var allTransient = new AggregateException(
            BuildPostgresException("57P01", "admin shutdown"),
            new NpgsqlException("connection refused", new SocketException((int)SocketError.ConnectionRefused)));
        var mixed = new AggregateException(
            BuildPostgresException("57P01", "admin shutdown"),
            BuildPostgresException("42601", "syntax error"));
        var wrappedMixed = new InvalidOperationException("wrapped aggregate", mixed);

        ExceptionMapper.Map(allTransient).StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        ExceptionMapper.Map(mixed).StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        ExceptionMapper.Map(wrappedMixed).StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        StartupDatabaseResilience.IsTransientConnectivityError(wrappedMixed).Should().BeFalse();
    }

    [UnitTest]
    public void Map_NonDatabaseTimeoutAndSocketErrors_KeepExistingMappings()
    {
        var timeout = new TimeoutException("upstream HTTP call timed out");
        var socket = new SocketException((int)SocketError.ConnectionReset);
        var cancelled = new OperationCanceledException(
            "cancelled",
            new NpgsqlException("aborted", new SocketException((int)SocketError.ConnectionAborted)));

        StartupDatabaseResilience.IsTransientDatabaseConnectivityError(timeout).Should().BeFalse();
        StartupDatabaseResilience.IsTransientDatabaseConnectivityError(socket).Should().BeFalse();
        ExceptionMapper.Map(timeout).StatusCode.Should().Be(StatusCodes.Status408RequestTimeout);
        ExceptionMapper.Map(socket).StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        ExceptionMapper.Map(cancelled).StatusCode.Should().Be(StatusCodes.Status408RequestTimeout);
    }

    [UnitTest]
    public void McpErrorMapper_TransientDatabaseConnectivityErrors_MapToRetryableUnavailableWithoutLeakingDetails()
    {
        var sqlTransient = BuildPostgresException("57P03", "Host=db.internal;Password=secret");
        var wrappedTransient = new InvalidOperationException(
            "Transient pool failure",
            new NpgsqlException("The connection pool has been exhausted.", new TimeoutException("Timeout")));
        var permanentSql = BuildPostgresException("42601", "syntax error");

        foreach (var transient in new Exception[] { sqlTransient, wrappedTransient })
        {
            var mapped = McpErrorMapper.Map(transient);
            mapped.Code.Should().Be(McpErrorMapper.JsonRpcServerError);
            mapped.Data!.Code.Should().Be(McpErrorMapper.Codes.Unavailable);
            mapped.Data.Retryable.Should().BeTrue();
            mapped.Data.RetryAfterSeconds.Should().Be(ExceptionMapper.DatabaseUnavailableRetryAfterSeconds);
            mapped.Message.Should().NotContain("db.internal").And.NotContain("secret");
        }

        var permanent = McpErrorMapper.Map(permanentSql);
        permanent.Data!.Code.Should().Be(McpErrorMapper.Codes.Internal);
        permanent.Data.Retryable.Should().BeNull();
    }

    private static PostgresException BuildPostgresException(string sqlState, string message)
        => new(messageText: message, severity: "ERROR", invariantSeverity: "ERROR", sqlState: sqlState);
}
