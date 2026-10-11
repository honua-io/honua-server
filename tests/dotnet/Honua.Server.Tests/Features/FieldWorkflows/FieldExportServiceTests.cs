// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Text;
using Honua.Core.Features.AuditLog;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.FieldWorkflows.Export;
using Honua.Core.Features.FieldWorkflows.Review;
using Honua.Server.Features.FieldWorkflows.Export;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.FieldWorkflows;

/// <summary>
/// Field export request parsing and truncation signaling for issue 5634.
/// </summary>
public sealed class FieldExportServiceTests
{
    [UnitTest]
    public async Task InvalidBody_IsRejectedAndDoesNotExport()
    {
        var store = new RecordingExportStore();
        var service = CreateService(store);

        await AssertBadRequestAsync(service, store, "not-json", contentLength: 8);
        await AssertBadRequestAsync(service, store, "null", contentLength: 4);
        await AssertBadRequestAsync(service, store, string.Empty, contentLength: null);

        Assert.Equal(0, store.CreateCalls);
    }

    [UnitTest]
    public async Task EmptyBody_ExportsWithoutFilters()
    {
        var store = new RecordingExportStore();
        var service = CreateService(store);
        var context = Context("{\"formId\":\"should-ignore\"}", contentLength: 0);

        await (await service.CreateAsync(context)).ExecuteAsync(context);

        Assert.Equal(1, store.CreateCalls);
        Assert.Null(store.LastRequest!.FormId);
        Assert.Equal("false", context.Response.Headers["X-Honua-Export-Truncated"]);
    }

    [UnitTest]
    public async Task ChunkedBody_IsReadWhenContentLengthIsAbsent()
    {
        var store = new RecordingExportStore();
        var service = CreateService(store);
        var context = Context("{\"formId\":\"form-1\"}", contentLength: null);

        await (await service.CreateAsync(context)).ExecuteAsync(context);

        Assert.Equal("form-1", store.LastRequest!.FormId);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [UnitTest]
    public async Task TruncatedPackage_SetsHeaderBodyAndAudit()
    {
        var audit = new RecordingAuditLog();
        var store = new RecordingExportStore
        {
            Package = Package(truncated: true)
        };
        var service = CreateService(store, audit);
        var context = Context("{}", contentLength: 2);

        await (await service.CreateAsync(context)).ExecuteAsync(context);

        Assert.Equal("true", context.Response.Headers["X-Honua-Export-Truncated"]);
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        Assert.Contains("\"truncated\":true", body, StringComparison.Ordinal);
        Assert.Contains("\"truncated\":true", audit.Details, StringComparison.Ordinal);
    }

    private static async Task AssertBadRequestAsync(
        FieldExportService service,
        RecordingExportStore store,
        string body,
        long? contentLength)
    {
        var calls = store.CreateCalls;
        var context = Context(body, contentLength);
        await (await service.CreateAsync(context)).ExecuteAsync(context);
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Equal(calls, store.CreateCalls);
    }

    private static DefaultHttpContext Context(string body, long? contentLength)
    {
        var context = new DefaultHttpContext();
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = contentLength;
        context.Request.ContentType = "application/json";
        context.Response.Body = new MemoryStream();
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "operator")], "test"));
        return context;
    }

    private static FieldExportService CreateService(RecordingExportStore store, IAuditLog? audit = null)
        => new(store, audit ?? NullAuditLog.Instance, NullLogger<FieldExportService>.Instance);

    private static FieldExportPackage Package(bool truncated)
        => new()
        {
            Truncated = truncated,
            Record = new FieldExportRecord
            {
                ExportId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                Format = FieldExportFormat.GeoJson,
                RecordCount = 1,
                RequestedBy = "operator"
            },
            Items =
            [
                new FieldSubmissionRecord
                {
                    SubmissionId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                    FormId = "form-1",
                    FormVersion = 1,
                    ServiceId = "svc",
                    LayerId = 1,
                    Operation = "create",
                    SyncStatus = FieldSyncStatus.Pending,
                    SubmittedAt = DateTimeOffset.UnixEpoch,
                    Review = new FieldReviewState
                    {
                        Status = FieldReviewStatus.Pending,
                        UpdatedAt = DateTimeOffset.UnixEpoch
                    }
                }
            ]
        };

    private sealed class RecordingExportStore : IFieldExportStore
    {
        public int CreateCalls { get; private set; }

        public FieldExportRequest? LastRequest { get; private set; }

        public FieldExportPackage Package { get; init; } = FieldExportServiceTests.Package(truncated: false);

        public Task<FieldExportPackage> CreateExportAsync(
            FieldExportRequest request,
            string requestedBy,
            CancellationToken cancellationToken = default)
        {
            CreateCalls++;
            LastRequest = request;
            return Task.FromResult(Package);
        }

        public Task<FieldExportRecordListResult> ListExportsAsync(
            int limit,
            int offset,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class RecordingAuditLog : IAuditLog
    {
        public string? Details { get; private set; }

        public bool IsPersisted => false;

        public Task<string?> RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            Details = auditEvent.Details;
            return Task.FromResult<string?>(null);
        }
    }
}
