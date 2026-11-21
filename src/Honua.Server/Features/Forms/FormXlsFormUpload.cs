// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Models;
using Microsoft.AspNetCore.Http.Features;

namespace Honua.Server.Features.Forms;

// The workbook bytes of a multipart XLSForm upload, or the problem that rejected it.
internal sealed class FormXlsFormUpload
{
    internal const long MaxWorkbookBytes = 8 * 1024 * 1024;
    internal const string FilePartName = "file";

    private const long MultipartOverheadBytes = 64 * 1024;
    private const long MaxMultipartBodyBytes = MaxWorkbookBytes + MultipartOverheadBytes;
    private const string Unreadable = "The multipart upload could not be read; check the boundary and that the workbook is at most 8 MiB.";

    public IResult? Error { get; private init; }

    public byte[] Content { get; private init; } = [];

    public static async Task<FormXlsFormUpload> ReadAsync(HttpContext context)
    {
        if (!context.Request.HasFormContentType)
        {
            return Failed(context, StatusCodes.Status400BadRequest, "Request must be multipart/form-data with a 'file' part carrying the XLSForm workbook.");
        }

        if (context.Request.ContentLength is long contentLength && contentLength > MaxMultipartBodyBytes)
        {
            return PayloadTooLarge(context);
        }

        ConfigureRequestLimits(context);
        IFormCollection form;
        try
        {
            form = await context.Request.ReadFormAsync(context.RequestAborted).ConfigureAwait(false);
        }
        catch (BadHttpRequestException ex)
        {
            return ex.StatusCode == StatusCodes.Status413PayloadTooLarge
                ? PayloadTooLarge(context)
                : Failed(context, ex.StatusCode, Unreadable);
        }
        catch (InvalidDataException ex) when (ex.Message.Contains("length limit", StringComparison.OrdinalIgnoreCase))
        {
            return PayloadTooLarge(context);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            return Failed(context, StatusCodes.Status400BadRequest, Unreadable);
        }

        return await ReadFileAsync(context, form.Files[FilePartName]).ConfigureAwait(false);
    }

    private static async Task<FormXlsFormUpload> ReadFileAsync(HttpContext context, IFormFile? file)
    {
        if (file is null || file.Length == 0)
        {
            return Failed(context, StatusCodes.Status400BadRequest, "A non-empty 'file' part carrying the XLSForm workbook is required.");
        }

        if (file.Length > MaxWorkbookBytes)
        {
            return PayloadTooLarge(context);
        }

        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, context.RequestAborted).ConfigureAwait(false);
        return new FormXlsFormUpload { Content = buffer.ToArray() };
    }

    private static void ConfigureRequestLimits(HttpContext context)
    {
        var maxBodyFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (maxBodyFeature is { IsReadOnly: false })
        {
            maxBodyFeature.MaxRequestBodySize = MaxMultipartBodyBytes;
        }

        context.Features.Set<IFormFeature>(new FormFeature(context.Request, new FormOptions { MultipartBodyLengthLimit = MaxMultipartBodyBytes }));
    }

    private static FormXlsFormUpload PayloadTooLarge(HttpContext context)
        => Failed(context, StatusCodes.Status413PayloadTooLarge, $"The workbook exceeds the {MaxWorkbookBytes}-byte limit.");

    private static FormXlsFormUpload Failed(HttpContext context, int statusCode, string detail)
        => new() { Error = ProblemDetailsHelpers.CreateAdminProblem(context, statusCode, detail) };
}
