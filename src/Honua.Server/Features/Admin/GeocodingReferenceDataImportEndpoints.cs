// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Geocoding.Features.Geocoding.Domain;
using Honua.Geocoding.Features.Geocoding.ReferenceDataImport;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Admin.Models;
using Microsoft.AspNetCore.Http.Features;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Admin endpoint for importing CSV reference data into the local PostGIS geocoder. The endpoint
/// is a thin adapter: column mapping, validation, and loading live in the shared
/// <see cref="IGeocoderReferenceDataImportService"/> so other surfaces can reuse them. Once
/// imported (and the <c>local</c> provider is enabled), the records are served through the
/// standard GeocodeServer operations (findAddressCandidates, reverseGeocode, suggest,
/// geocodeAddresses).
/// </summary>
internal static class GeocodingReferenceDataImportEndpoints
{
    internal sealed class GeocodingReferenceDataImportLog;

    /// <summary>Upper bound on the uploaded reference data CSV size (256 MiB).</summary>
    private const long MaxReferenceBytes = 256L * 1024 * 1024;

    /// <summary>
    /// Maps the geocoder reference data import endpoint under the admin geocoding surface.
    /// </summary>
    public static void MapGeocodingReferenceDataImportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/geocoding")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Geocoding")
            .RequireAdminAuthorization();

        // Cast to Delegate so the HttpContext-only handler binds as a route handler (its
        // IResult is written to the response) rather than a raw RequestDelegate.
        _ = group.MapPost("/reference-data/import", (Delegate)HandleImportReferenceData)
            .WithName("ImportGeocoderReferenceData")
            .WithSummary("Import CSV reference data into the local geocoder")
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<ApiResponse<GeocoderReferenceDataImportResponse>>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .DisableAntiforgery();
    }

    private static async Task<IResult> HandleImportReferenceData(HttpContext context)
    {
        // Timeout-aware token (includes Limits:Connections:RequestTimeout via
        // LimitsEnforcementMiddleware) so long imports cancel and produce the configured 408,
        // consistent with the other import endpoints.
        var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        if (!context.Request.HasFormContentType)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest,
                "Request must be multipart/form-data with a 'referenceData' field carrying the reference data CSV.");
        }

        // ASP.NET Core's default multipart body limit (128 MiB) is below the advertised
        // 256 MiB reference CSV cap; raise it for this request before parsing so the
        // documented range is actually importable. Kestrel's overall request-body limit is
        // handled by LimitsEnforcementMiddleware's import classification.
        context.Features.Set<IFormFeature>(new FormFeature(context.Request, new FormOptions
        {
            MultipartBodyLengthLimit = MaxReferenceBytes + (1024 * 1024),
        }));

        IFormCollection form;
        try
        {
            form = await context.Request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (BadHttpRequestException ex)
        {
            // Oversized chunked bodies exceed IHttpMaxRequestBodySizeFeature during form
            // parsing and surface here with the correct status (413); preserve it instead of
            // flattening to 400 via the IOException base class.
            return ProblemDetailsHelpers.CreateAdminProblem(context, ex.StatusCode,
                "The multipart form data could not be parsed. Check the multipart boundary and that the upload is within the documented size limits.");
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            // Missing/invalid multipart boundaries surface as InvalidDataException, while a body
            // that never contains the declared boundary ends in an IOException from the multipart
            // reader. All are client faults during form parsing; without this the global handler
            // would surface them as 500s. Over-limit Content-Length'd multipart sections get the
            // dedicated 413.
            var status = ex is InvalidDataException &&
                ex.Message.Contains("length limit", StringComparison.OrdinalIgnoreCase)
                ? StatusCodes.Status413PayloadTooLarge
                : StatusCodes.Status400BadRequest;
            return ProblemDetailsHelpers.CreateAdminProblem(context, status,
                "The multipart form data could not be parsed. Check the multipart boundary and that the upload is within the documented size limits.");
        }

        var referenceFile = form.Files["referenceData"];
        if (referenceFile is null || referenceFile.Length == 0)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest,
                "A non-empty 'referenceData' field carrying the reference data CSV is required.");
        }

        if (!HasExtension(referenceFile.FileName, ".csv"))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest,
                "The 'referenceData' file must be a CSV with a header row.");
        }

        if (referenceFile.Length > MaxReferenceBytes)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest,
                $"The reference data CSV exceeds the {MaxReferenceBytes}-byte upload limit.");
        }

        var mode = form["mode"].ToString();
        if (mode.Length > 0 && mode is not ("replace" or "append"))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest,
                "The 'mode' field must be 'replace' or 'append'.");
        }

        IReadOnlyDictionary<string, string>? fieldMap = null;
        var fieldMapJson = form["fieldMap"].ToString();
        if (fieldMapJson.Length > 0)
        {
            try
            {
                fieldMap = JsonSerializer.Deserialize(fieldMapJson, GeocodingAdminJsonContext.Default.DictionaryStringString);
            }
            catch (JsonException)
            {
                return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest,
                    "The 'fieldMap' field must be a JSON object mapping reference roles to CSV column names.");
            }
        }

        var importService = context.RequestServices.GetRequiredService<IGeocoderReferenceDataImportService>();
        var logger = context.RequestServices.GetRequiredService<ILogger<GeocodingReferenceDataImportLog>>();

        Stream? referenceStream = null;
        try
        {
            referenceStream = referenceFile.OpenReadStream();

            var result = await importService.ImportAsync(
                new GeocoderReferenceDataImportRequest
                {
                    ReferenceData = referenceStream,
                    FieldMap = fieldMap,
                    LocatorName = NormalizeOptional(form["locatorName"].ToString()),
                    ReplaceExisting = mode is not "append",
                },
                cancellationToken).ConfigureAwait(false);

            return Results.Json(
                ApiResponse<GeocoderReferenceDataImportResponse>.CreateSuccess(MapResponse(result)),
                GeocodingAdminJsonContext.Default.ApiResponseGeocoderReferenceDataImportResponse);
        }
        catch (GeocoderReferenceDataImportException ex)
        {
            // GeocoderReferenceDataImportException messages are operator-safe by contract (no SQL,
            // connection strings, or provider internals).
            AdminLog.GeocoderReferenceImportRejected(logger, ex.Message);
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, ex.Message);
        }
        catch (GeocoderReferenceDataImportStoreException ex)
        {
            // Reference-store failures (database down, permissions, incompatible schema) are
            // server faults: surface 503 so clients and monitoring classify and retry correctly.
            AdminLog.GeocoderReferenceImportRejected(logger, ex.Message);
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status503ServiceUnavailable, ex.Message);
        }
        finally
        {
            if (referenceStream is not null)
            {
                await referenceStream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static GeocoderReferenceDataImportResponse MapResponse(GeocoderReferenceDataImportResult result) => new()
    {
        LocatorName = result.LocatorName,
        Provider = GeocodeProviderNames.Local,
        Schema = result.Schema,
        Table = result.Table,
        RecordsImported = result.RecordsImported,
        RecordsSkipped = result.RecordsSkipped,
        SkippedRows = [.. result.SkippedRows.Select(static r => new GeocoderReferenceSkippedRowDto
        {
            RowNumber = r.RowNumber,
            Reason = r.Reason,
        })],
        Report = [.. result.Report.Select(static e => new GeocoderReferenceReportEntryDto
        {
            Column = e.Column,
            Status = e.Status == ReferenceColumnStatus.Supported ? "supported" : "ignored",
            Detail = e.Detail,
        })],
    };

    private static bool HasExtension(string fileName, string extension)
        => Path.GetExtension(fileName).Equals(extension, StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeOptional(string value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
