// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Geocoding.Features.Geocoding.Domain;
using Microsoft.AspNetCore.Http.Features;
using Honua.Geocoding.Features.Geocoding.LocatorImport;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Admin.Models;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Admin endpoint for importing Esri <c>.loc</c>/<c>.lox</c> locators into the local PostGIS
/// geocoder (#2152). The endpoint is a thin adapter: parsing, classification, and reference
/// loading live in the shared <see cref="IEsriLocatorImportService"/> so other surfaces can reuse
/// them. Once imported (and the <c>local</c> provider is enabled), the locator is served through
/// the standard GeocodeServer operations (findAddressCandidates, reverseGeocode, suggest,
/// geocodeAddresses).
/// </summary>
internal static class GeocodingLocatorImportEndpoints
{
    internal sealed class GeocodingLocatorImportLog;

    /// <summary>Upper bound on the uploaded .loc definition size (4 MiB): classic definitions are small text files.</summary>
    private const long MaxLocBytes = 4L * 1024 * 1024;

    /// <summary>Upper bound on the uploaded reference data CSV size (256 MiB).</summary>
    private const long MaxReferenceBytes = 256L * 1024 * 1024;

    /// <summary>
    /// Maps the Esri locator import endpoint under the admin geocoding surface.
    /// </summary>
    public static void MapGeocodingLocatorImportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/geocoding")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Geocoding")
            .RequireAdminAuthorization();

        _ = group.MapPost("/locators/import", HandleImportLocator)
            .WithName("ImportEsriLocator")
            .WithSummary("Import an Esri .loc/.lox locator and its reference data into the local geocoder")
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<ApiResponse<EsriLocatorImportResponse>>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .DisableAntiforgery();
    }

    private static async Task<IResult> HandleImportLocator(HttpContext context, CancellationToken cancellationToken)
    {
        if (!context.Request.HasFormContentType)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest,
                "Request must be multipart/form-data with a 'locator' field carrying the .loc definition file.");
        }

        // ASP.NET Core's default multipart body limit (128 MiB) is below the advertised
        // 256 MiB reference CSV cap; raise it for this request before parsing so the
        // documented range is actually importable. Kestrel's overall request-body limit is
        // handled by LimitsEnforcementMiddleware's import classification.
        context.Features.Set<IFormFeature>(new FormFeature(context.Request, new FormOptions
        {
            MultipartBodyLengthLimit = MaxReferenceBytes + MaxLocBytes + (1024 * 1024),
        }));

        var form = await context.Request.ReadFormAsync(cancellationToken).ConfigureAwait(false);

        var locFile = form.Files["locator"];
        if (locFile is null || locFile.Length == 0)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest,
                "A non-empty 'locator' field carrying the .loc definition file is required.");
        }

        if (!HasExtension(locFile.FileName, ".loc"))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest,
                "The 'locator' file must have a .loc extension.");
        }

        if (locFile.Length > MaxLocBytes)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest,
                $"The .loc definition exceeds the {MaxLocBytes}-byte upload limit.");
        }

        var indexFile = form.Files["index"];
        if (indexFile is not null && !HasExtension(indexFile.FileName, ".lox"))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest,
                "The 'index' file must have a .lox extension.");
        }

        var referenceFile = form.Files["referenceData"];
        if (referenceFile is not null)
        {
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

        byte[] locContent;
        await using (var locStream = locFile.OpenReadStream())
        {
            using var buffer = new MemoryStream((int)locFile.Length);
            await locStream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            locContent = buffer.ToArray();
        }

        var importService = context.RequestServices.GetRequiredService<IEsriLocatorImportService>();
        var logger = context.RequestServices.GetRequiredService<ILogger<GeocodingLocatorImportLog>>();

        Stream? referenceStream = null;
        try
        {
            if (referenceFile is not null)
            {
                referenceStream = referenceFile.OpenReadStream();
            }

            var result = await importService.ImportAsync(
                new EsriLocatorImportRequest
                {
                    LocFileName = Path.GetFileName(locFile.FileName),
                    LocContent = locContent,
                    IndexFileName = indexFile is null ? null : Path.GetFileName(indexFile.FileName),
                    ReferenceData = referenceStream,
                    FieldMap = fieldMap,
                    LocatorName = NormalizeOptional(form["locatorName"].ToString()),
                    ReplaceExisting = mode is not "append",
                },
                cancellationToken).ConfigureAwait(false);

            return Results.Json(
                ApiResponse<EsriLocatorImportResponse>.CreateSuccess(MapResponse(result)),
                GeocodingAdminJsonContext.Default.ApiResponseEsriLocatorImportResponse);
        }
        catch (EsriLocatorImportException ex)
        {
            // EsriLocatorImportException messages are operator-safe by contract (no SQL,
            // connection strings, or provider internals).
            AdminLog.EsriLocatorImportRejected(logger, ex.Message);
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, ex.Message);
        }
        finally
        {
            if (referenceStream is not null)
            {
                await referenceStream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static EsriLocatorImportResponse MapResponse(EsriLocatorImportResult result) => new()
    {
        LocatorName = result.Definition.Name,
        Provider = GeocodeProviderNames.Local,
        Schema = result.Schema,
        Table = result.Table,
        ReferenceDataImported = result.ReferenceDataImported,
        RecordsImported = result.RecordsImported,
        RecordsSkipped = result.RecordsSkipped,
        SkippedRows = [.. result.SkippedRows.Select(static r => new EsriLocatorSkippedRowDto
        {
            RowNumber = r.RowNumber,
            Reason = r.Reason,
        })],
        Version = result.Definition.Version,
        StyleId = result.Definition.StyleId,
        Category = result.Definition.Category,
        MatchSettings = new EsriLocatorMatchSettingsDto
        {
            MinimumMatchScore = result.Definition.MatchSettings.MinimumMatchScore,
            MinimumCandidateScore = result.Definition.MatchSettings.MinimumCandidateScore,
            SpellingSensitivity = result.Definition.MatchSettings.SpellingSensitivity,
            SideOffset = result.Definition.MatchSettings.SideOffset,
            SideOffsetUnits = result.Definition.MatchSettings.SideOffsetUnits,
            EndOffset = result.Definition.MatchSettings.EndOffset,
            MatchIfScoresTie = result.Definition.MatchSettings.MatchIfScoresTie,
            Interpolate = result.Definition.MatchSettings.Interpolate,
        },
        Report = [.. result.Report.Select(static e => new EsriLocatorReportEntryDto
        {
            Item = e.Item,
            Status = e.Status switch
            {
                LocatorTranslationStatus.Supported => "supported",
                LocatorTranslationStatus.Unsupported => "unsupported",
                LocatorTranslationStatus.Regenerated => "regenerated",
                _ => "ignored",
            },
            Detail = e.Detail,
        })],
    };

    private static bool HasExtension(string fileName, string extension)
        => Path.GetExtension(fileName).Equals(extension, StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeOptional(string value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
