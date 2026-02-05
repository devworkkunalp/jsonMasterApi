using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.AspNetCore.Http.Features;
using Newtonsoft.Json.Linq;
using JsonMaster.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Remove AddControllers() - Moving to Minimal APIs
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddScoped<StreamingDiffService>();
builder.Services.AddScoped<SmartCompareService>();
builder.Services.AddScoped<TextDiffService>();
builder.Services.AddMemoryCache();

// Global Newtonsoft settings for camelCase
var newtonsoftSettings = new Newtonsoft.Json.JsonSerializerSettings
{
    ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver(),
    Formatting = Newtonsoft.Json.Formatting.None
};

// Increase Form Limits for large uploads
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
    options.ValueLengthLimit = int.MaxValue;
    options.MultipartBodyLengthLimit = int.MaxValue;
    options.MemoryBufferThreshold = int.MaxValue;
});

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll",
        builder =>
        {
            builder.AllowAnyOrigin()
                   .AllowAnyMethod()
                   .AllowAnyHeader();
        });
});

var app = builder.Build();

app.UseCors("AllowAll");
app.UseStaticFiles();
app.UseHttpsRedirection();

// --- Minimal API Endpoints ---

// 1. Streaming Diff (Quick Compare)
app.MapPost("/api/compare", async (HttpContext context, StreamingDiffService diffService) =>
{
    context.Features.Get<IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize = null; // Disable limit
    context.Response.Headers.Append("Content-Type", "text/event-stream");

    var form = await context.Request.ReadFormAsync();
    var files = form.Files;
    var json1 = form["json1"].ToString();
    var json2 = form["json2"].ToString();

    Stream? stream1 = null;
    Stream? stream2 = null;

    try
    {
        if (files.Count == 2)
        {
            stream1 = files[0].OpenReadStream();
            stream2 = files[1].OpenReadStream();
        }
        else if (!string.IsNullOrEmpty(json1) && !string.IsNullOrEmpty(json2))
        {
            stream1 = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json1));
            stream2 = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json2));
        }
        else
        {
            await context.Response.WriteAsync("data: {\"error\": \"Please provide exactly 2 files or 2 JSON strings.\"}\n\n");
            return;
        }

        if (stream1 != null && stream2 != null)
        {
            await foreach (var batch in diffService.CompareStreamsAsync(stream1!, stream2!))
            {
                foreach (var diff in batch)
                {
                    await context.Response.WriteAsync($"data: {diff}\n\n");
                }
                await context.Response.Body.FlushAsync();
            }
        }
    }
    catch (Exception ex)
    {
        await context.Response.WriteAsync($"data: {{\"error\": \"Server error: {ex.Message}\"}}\n\n");
    }
}).DisableAntiforgery(); // Ensure large uploads work smoothly

// 2. Diff to File (Report)
app.MapPost("/api/compare/file", async (HttpContext context, StreamingDiffService diffService) =>
{
    context.Features.Get<IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize = null;
    context.Response.Headers.Append("Content-Type", "text/event-stream");

    var form = await context.Request.ReadFormAsync();
    Stream? stream1 = null;
    Stream? stream2 = null;
    
    // Quick extract logic
    if (form.Files.Count == 2) {
        stream1 = form.Files[0].OpenReadStream();
        stream2 = form.Files[1].OpenReadStream();
    } else {
        var j1 = form["json1"].ToString();
        var j2 = form["json2"].ToString();
        if (!string.IsNullOrEmpty(j1)) stream1 = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(j1));
        if (!string.IsNullOrEmpty(j2)) stream2 = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(j2));
    }

    if (stream1 == null || stream2 == null) {
        await context.Response.WriteAsync("data: {\"error\": \"Invalid input\"}\n\n");
        return;
    }

    try 
    {
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var outputFileName = $"diff_report_{timestamp}.json";
        var outputPath = Path.Combine("wwwroot", "reports", outputFileName);
        Directory.CreateDirectory(Path.Combine("wwwroot", "reports"));

        int totalDiffs = 0;
        int batchCount = 0;

        using (var fileWriter = new StreamWriter(outputPath))
        {
            await fileWriter.WriteLineAsync("[");
            bool firstItem = true;

            await foreach (var batch in diffService.CompareStreamsAsync(stream1, stream2))
            {
                foreach (var diff in batch)
                {
                    if (!firstItem) await fileWriter.WriteLineAsync(",");
                    await fileWriter.WriteAsync("  " + diff);
                    firstItem = false;
                    totalDiffs++;
                }
                batchCount++;
                var info = new { type = "progress", batches = batchCount, items = totalDiffs, message = $"Processed {totalDiffs:N0} differences..." };
                await context.Response.WriteAsync($"data: {System.Text.Json.JsonSerializer.Serialize(info)}\n\n");
                await context.Response.Body.FlushAsync();
            }
            await fileWriter.WriteLineAsync("\n]");
        }

        var result = new { type = "complete", totalDifferences = totalDiffs, downloadUrl = $"/reports/{outputFileName}", fileName = outputFileName, message = "Done!" };
        await context.Response.WriteAsync($"data: {System.Text.Json.JsonSerializer.Serialize(result)}\n\n");
    }
    catch (Exception ex)
    {
        await context.Response.WriteAsync($"data: {{\"type\": \"error\", \"message\": \"{ex.Message}\"}}\n\n");
    }
}).DisableAntiforgery();

// 3. Smart Compare
app.MapPost("/api/compare/smart", async (HttpContext context, SmartCompareService smartService) =>
{
    context.Features.Get<IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize = null;
    
    var form = await context.Request.ReadFormAsync();
    string keyField = form["keyField"].ToString();
    string ignoredFields = form["ignoredFields"].ToString();
    string sourceJson = "", targetJson = "";

    try {
        if (form.Files.Count == 2) {
            using var r1 = new StreamReader(form.Files[0].OpenReadStream());
            using var r2 = new StreamReader(form.Files[1].OpenReadStream());
            sourceJson = await r1.ReadToEndAsync();
            targetJson = await r2.ReadToEndAsync();
        } else {
             sourceJson = form["json1"].ToString();
             targetJson = form["json2"].ToString();
        }

        if (string.IsNullOrEmpty(sourceJson) || string.IsNullOrEmpty(targetJson)) {
            return Results.BadRequest(new { error = "Missing JSON content" });
        }

        var result = smartService.CompareJsons(sourceJson, targetJson, keyField, ignoredFields);
        if (!string.IsNullOrEmpty(result.ValidationError)) {
            return Results.BadRequest(new { error = result.ValidationError });
        }

        var response = new {
            summary = result.GetSummary(),
            modified = result.Modified,
            added = result.Added,
            removed = result.Removed,
            unchanged = result.Unchanged
        };
        
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(response, newtonsoftSettings);
        return Results.Text(json, "application/json");
    } catch (Exception ex) {
        return Results.Json(new { error = ex.Message }, statusCode: 500);
    }
}).DisableAntiforgery();

// 4. Text Diff
app.MapPost("/api/compare/text", async (HttpContext context, TextDiffService textService, IMemoryCache memoryCache) =>
{
     context.Features.Get<IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize = null;
     var form = await context.Request.ReadFormAsync();
     
     string sessionId = form["sessionId"].ToString();
     int page = int.TryParse(form["page"], out var p) ? p : 1;
     int pageSize = int.TryParse(form["pageSize"], out var ps) ? ps : 100;

     try {
        TextDiffService.DiffSession? session;
        if (form.Files.Count == 2) {
             var newSessionId = Guid.NewGuid().ToString();
             using var r1 = new StreamReader(form.Files[0].OpenReadStream());
             using var r2 = new StreamReader(form.Files[1].OpenReadStream());
             var s = await r1.ReadToEndAsync();
             var t = await r2.ReadToEndAsync();
             session = textService.InitializeSession(s, t);
             memoryCache.Set(newSessionId, session, TimeSpan.FromHours(1));
             sessionId = newSessionId;
        } else if (!string.IsNullOrEmpty(sessionId)) {
             memoryCache.TryGetValue(sessionId, out session);
             if (session == null) return Results.BadRequest(new { error = "Session expired" });
        } else {
            return Results.BadRequest(new { error = "Invalid input" });
        }

        var result = textService.GetPage(session!, (page - 1) * pageSize + 1, pageSize);
        var totalPages = (int)Math.Ceiling((double)result.TotalLines / pageSize);

        return Results.Ok(new {
             sessionId,
             totalDifferences = result.TotalDifferences,
             totalLines = result.TotalLines,
             sourceSize = result.SourceSize,
             targetSize = result.TargetSize,
             page,
             pageSize,
             totalPages,
             sourceLines = result.SourceLines,
             targetLines = result.TargetLines
        });

     } catch (Exception ex) {
         return Results.Json(new { error = ex.Message }, statusCode: 500);
     }
}).DisableAntiforgery();

// Configure the port for Render or other environments
var port = Environment.GetEnvironmentVariable("PORT")  ?? "10000";
app.Run($"http://0.0.0.0:{port}");
