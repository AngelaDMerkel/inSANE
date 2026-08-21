using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using InSane;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<InSaneOptions>()
    .Bind(builder.Configuration.GetSection("InSane"))
    .Validate(x => Path.IsPathRooted(x.Storage.StatePath), "StatePath must be absolute.")
    .Validate(x => Path.IsPathRooted(x.Storage.OutputPath), "OutputPath must be absolute.")
    .Validate(x => !x.Scanner.PhysicalButtonEnabled || !string.IsNullOrWhiteSpace(x.Scanner.PhysicalButtonToken),
        "PhysicalButtonToken is required when physical button integration is enabled.")
    .ValidateOnStart();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});

builder.Services.AddSingleton<StoragePaths>();
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<ProfileStore>();
builder.Services.AddSingleton<Naps2Runtime>();
builder.Services.AddSingleton<Naps2ScannerBackend>();
builder.Services.AddSingleton<DemoScannerBackend>();
builder.Services.AddSingleton<ScannerRouter>();
builder.Services.AddSingleton<ScanCoordinator>();
builder.Services.AddSingleton<DocumentExporter>();

var app = builder.Build();

_ = app.Services.GetRequiredService<StoragePaths>();
_ = app.Services.GetRequiredService<SessionStore>();

app.Use(async (context, next) =>
{
    try { await next(); }
    catch (Exception ex)
    {
        if (context.Response.HasStarted) throw;
        var (status, title) = ex switch
        {
            KeyNotFoundException => (StatusCodes.Status404NotFound, "Not found"),
            ArgumentException => (StatusCodes.Status400BadRequest, "Invalid request"),
            InvalidOperationException => (StatusCodes.Status409Conflict, "Operation cannot be completed"),
            PlatformNotSupportedException => (StatusCodes.Status422UnprocessableEntity, "Driver is unavailable"),
            _ => (StatusCodes.Status500InternalServerError, "Unexpected server error")
        };
        app.Logger.Log(status >= 500 ? LogLevel.Error : LogLevel.Information, ex, "Request failed");
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new { title, status, detail = ex.Message });
    }
});

app.UseDefaultFiles();
app.UseStaticFiles();

var api = app.MapGroup("/api/v1");

api.MapGet("/health", (StoragePaths paths) => Results.Ok(new
{
    status = "ok",
    version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "development",
    runtime = new
    {
        user = Environment.UserName,
        uid = RuntimeIdentity.EffectiveUid,
        gid = RuntimeIdentity.EffectiveGid
    },
    storage = new
    {
        state = "writable",
        output = "writable",
        validatedAt = paths.ValidatedAt
    }
}));

api.MapGet("/system", (StoragePaths paths, IOptions<InSaneOptions> options) => Results.Ok(new
{
    product = "inSANE",
    naps2Sdk = "1.3.0",
    runtime = Environment.OSVersion.Platform.ToString(),
    statePath = paths.State,
    outputPath = paths.Output,
    demoEnabled = options.Value.Scanner.EnableDemo,
    physicalButtonEnabled = options.Value.Scanner.PhysicalButtonEnabled
}));

api.MapGet("/drivers", (IOptions<InSaneOptions> options) =>
    Results.Ok(DriverCatalog.Get(options.Value.Scanner.EnableDemo)));

api.MapGet("/devices", async (ScannerRouter scanner, CancellationToken cancellationToken) =>
    Results.Ok(await scanner.GetDevicesAsync(cancellationToken)));

api.MapGet("/devices/{deviceKey}/capabilities",
    async (string deviceKey, ScannerRouter scanner, CancellationToken cancellationToken) =>
        Results.Ok(await scanner.GetCapabilitiesAsync(deviceKey, cancellationToken)));

api.MapGet("/profiles", (string? deviceKey, ProfileStore profiles) => Results.Ok(profiles.List(deviceKey)));
api.MapPost("/profiles", async (ScanProfile profile, ProfileStore profiles) =>
    Results.Ok(await profiles.SaveAsync(profile)));
api.MapPut("/profiles/{profileId:guid}", async (Guid profileId, ScanProfile profile, ProfileStore profiles) =>
{
    if (profiles.Get(profileId) is null) return Results.NotFound();
    profile.Id = profileId;
    return Results.Ok(await profiles.SaveAsync(profile));
});
api.MapDelete("/profiles/{profileId:guid}", async (Guid profileId, ProfileStore profiles) =>
    await profiles.DeleteAsync(profileId) ? Results.NoContent() : Results.NotFound());

api.MapGet("/sessions", (SessionStore sessions) => Results.Ok(sessions.List()));
api.MapPost("/sessions", async (CreateSessionRequest request, SessionStore sessions) =>
{
    var session = await sessions.CreateAsync(request.Title);
    return Results.Created($"/api/v1/sessions/{session.Id}", session);
});
api.MapGet("/sessions/{sessionId:guid}", (Guid sessionId, SessionStore sessions) =>
    sessions.Get(sessionId) is { } session ? Results.Ok(session) : Results.NotFound());
api.MapPatch("/sessions/{sessionId:guid}", async (Guid sessionId, CreateSessionRequest request, SessionStore sessions) =>
{
    var updated = await sessions.UpdateAsync(sessionId, session =>
    {
        if (!string.IsNullOrWhiteSpace(request.Title)) session.Title = request.Title.Trim();
    });
    return updated is null ? Results.NotFound() : Results.Ok(updated);
});

api.MapPost("/sessions/{sessionId:guid}/scan",
    (Guid sessionId, ScanSessionRequest request, ScanCoordinator coordinator) =>
    {
        var job = coordinator.Start(sessionId, request.Settings);
        return Results.Accepted($"/api/v1/scan-jobs/{job.Id}", job);
    });

api.MapGet("/scan-jobs/{jobId:guid}", (Guid jobId, ScanCoordinator coordinator) =>
    coordinator.Get(jobId) is { } job ? Results.Ok(job) : Results.NotFound());
api.MapPost("/scan-jobs/{jobId:guid}/retry", (Guid jobId, ScanCoordinator coordinator) =>
{
    var job = coordinator.Retry(jobId);
    return Results.Accepted($"/api/v1/scan-jobs/{job.Id}", job);
});
api.MapDelete("/scan-jobs/{jobId:guid}", (Guid jobId, ScanCoordinator coordinator) =>
    coordinator.Cancel(jobId) ? Results.Accepted() : Results.NotFound());

api.MapPost("/actions/scanner-button", async (PhysicalButtonRequest request, HttpContext context,
    ProfileStore profiles, SessionStore sessions, ScanCoordinator coordinator,
    IOptions<InSaneOptions> options) =>
{
    var scannerOptions = options.Value.Scanner;
    if (!scannerOptions.PhysicalButtonEnabled)
        return Results.NotFound(new { detail = "Physical scanner button integration is not enabled." });

    var suppliedToken = context.Request.Headers["X-inSANE-Button-Token"].ToString();
    var suppliedBytes = Encoding.UTF8.GetBytes(suppliedToken);
    var expectedBytes = Encoding.UTF8.GetBytes(scannerOptions.PhysicalButtonToken);
    if (!CryptographicOperations.FixedTimeEquals(suppliedBytes, expectedBytes)) return Results.Unauthorized();

    var profile = profiles.GetDefault(request.DeviceKey);
    if (profile is null)
        throw new InvalidOperationException("Set a default scan profile for the scanner button before using it.");
    var session = sessions.List().FirstOrDefault(value => value.Status == "building")
                  ?? await sessions.CreateAsync(null);
    var job = coordinator.Start(session.Id, profile.Settings);
    return Results.Accepted($"/api/v1/scan-jobs/{job.Id}", new { job, sessionId = session.Id, profileId = profile.Id });
});

api.MapPost("/sessions/{sessionId:guid}/pages/{pageId:guid}/rotate",
    async (Guid sessionId, Guid pageId, RotatePageRequest request, SessionStore sessions) =>
    {
        if (request.Degrees % 90 != 0) return Results.BadRequest(new { detail = "Rotation must be a multiple of 90 degrees." });
        var found = false;
        var updated = await sessions.UpdateAsync(sessionId, session =>
        {
            var page = session.Pages.FirstOrDefault(x => x.Id == pageId);
            if (page is null) return;
            found = true;
            page.Rotation = ((page.Rotation + request.Degrees) % 360 + 360) % 360;
        });
        return updated is null || !found ? Results.NotFound() : Results.Ok(updated);
    });

api.MapPost("/sessions/{sessionId:guid}/pages/rotate",
    async (Guid sessionId, RotatePagesRequest request, SessionStore sessions) =>
    {
        if (request.Degrees % 90 != 0)
            return Results.BadRequest(new { detail = "Rotation must be a multiple of 90 degrees." });
        if (request.PageIds is null || request.PageIds.Count == 0)
            return Results.BadRequest(new { detail = "Select at least one page to rotate." });
        if (request.PageIds.Distinct().Count() != request.PageIds.Count)
            return Results.BadRequest(new { detail = "The rotation selection contains duplicate pages." });

        var pageIds = request.PageIds.ToHashSet();
        var updated = await sessions.UpdateAsync(sessionId, session =>
        {
            if (session.Status == "saved") throw new InvalidOperationException("A saved document cannot be rotated.");
            if (pageIds.Any(id => session.Pages.All(page => page.Id != id)))
                throw new ArgumentException("The rotation selection contains a page outside this document.");
            foreach (var page in session.Pages.Where(page => pageIds.Contains(page.Id)))
                page.Rotation = ((page.Rotation + request.Degrees) % 360 + 360) % 360;
        });
        return updated is null ? Results.NotFound() : Results.Ok(updated);
    });

api.MapPost("/sessions/{sessionId:guid}/pages/{pageId:guid}/crop",
    async (Guid sessionId, Guid pageId, CropPageRequest request, SessionStore sessions) =>
    {
        if (request is { X: < 0 } or { Y: < 0 } || request.Width <= 0 || request.Height <= 0 ||
            request.X + request.Width > 1.00001 || request.Y + request.Height > 1.00001)
            return Results.BadRequest(new { detail = "Crop values must describe a positive normalized rectangle inside the page." });
        var found = false;
        var updated = await sessions.UpdateAsync(sessionId, session =>
        {
            var page = session.Pages.FirstOrDefault(x => x.Id == pageId);
            if (page is null) return;
            found = true;
            page.Crop = new CropRegion { X = request.X, Y = request.Y, Width = request.Width, Height = request.Height };
        });
        return updated is null || !found ? Results.NotFound() : Results.Ok(updated);
    });

api.MapPost("/sessions/{sessionId:guid}/pages/crop",
    async (Guid sessionId, CropPagesRequest request, SessionStore sessions) =>
    {
        if (request.PageIds is null || request.PageIds.Count == 0)
            return Results.BadRequest(new { detail = "Select at least one page to crop." });
        if (request.PageIds.Distinct().Count() != request.PageIds.Count)
            return Results.BadRequest(new { detail = "The crop selection contains duplicate pages." });
        if (request is { X: < 0 } or { Y: < 0 } || request.Width <= 0 || request.Height <= 0 ||
            request.X + request.Width > 1.00001 || request.Y + request.Height > 1.00001)
            return Results.BadRequest(new { detail = "Crop values must describe a positive normalized rectangle inside the page." });

        var pageIds = request.PageIds.ToHashSet();
        var updated = await sessions.UpdateAsync(sessionId, session =>
        {
            if (session.Status == "saved") throw new InvalidOperationException("A saved document cannot be cropped.");
            if (pageIds.Any(id => session.Pages.All(page => page.Id != id)))
                throw new ArgumentException("The crop selection contains a page outside this document.");
            foreach (var page in session.Pages.Where(page => pageIds.Contains(page.Id)))
            {
                page.Crop = new CropRegion
                {
                    X = request.X,
                    Y = request.Y,
                    Width = request.Width,
                    Height = request.Height
                };
            }
        });
        return updated is null ? Results.NotFound() : Results.Ok(updated);
    });

api.MapDelete("/sessions/{sessionId:guid}/pages/{pageId:guid}",
    async (Guid sessionId, Guid pageId, SessionStore sessions) =>
        await sessions.DeletePageAsync(sessionId, pageId) ? Results.NoContent() : Results.NotFound());

api.MapPost("/sessions/{sessionId:guid}/pages/reorder",
    async (Guid sessionId, ReorderPagesRequest request, SessionStore sessions) =>
    {
        if (request.PageIds is null)
            return Results.BadRequest(new { detail = "A complete page order is required." });
        var updated = await sessions.ReorderPagesAsync(sessionId, request.PageIds);
        return updated is null ? Results.NotFound() : Results.Ok(updated);
    });

api.MapGet("/sessions/{sessionId:guid}/pages/{pageId:guid}/image",
    (Guid sessionId, Guid pageId, SessionStore sessions) =>
    {
        var page = sessions.Get(sessionId)?.Pages.FirstOrDefault(x => x.Id == pageId);
        return page is not null && File.Exists(page.FilePath)
            ? Results.File(page.FilePath, "image/jpeg", enableRangeProcessing: true)
            : Results.NotFound();
    });

api.MapPost("/sessions/{sessionId:guid}/save",
    async (Guid sessionId, SaveDocumentRequest request, DocumentExporter exporter, CancellationToken cancellationToken) =>
        Results.Ok(await exporter.SaveDocumentAsync(sessionId, request, cancellationToken)));

api.MapPost("/sessions/{sessionId:guid}/download",
    async (Guid sessionId, SaveDocumentRequest request, DocumentExporter exporter,
        HttpContext context, CancellationToken cancellationToken) =>
    {
        var download = await exporter.PrepareDownloadAsync(sessionId, request, cancellationToken);
        context.Response.OnCompleted(() =>
        {
            try { File.Delete(download.FilePath); }
            catch (IOException) { /* A later cleanup pass may remove an abandoned temporary download. */ }
            return Task.CompletedTask;
        });
        return Results.File(download.FilePath, download.ContentType, download.FileName);
    });

api.MapGet("/history", (SessionStore sessions) => Results.Ok(sessions.List(savedOnly: true)));
api.MapGet("/documents/{fileName}", (string fileName, DocumentExporter exporter) =>
    exporter.ResolveOutput(fileName) is { } path
        ? Results.File(path,
            Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".tif" or ".tiff" => "image/tiff",
                ".zip" => "application/zip",
                _ => "application/pdf"
            },
            fileName, enableRangeProcessing: true)
        : Results.NotFound());

app.MapFallbackToFile("index.html");
app.Run();
