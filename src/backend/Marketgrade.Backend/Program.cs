using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Marketgrade.Backend;
using Marketgrade.Engine;
using Marketgrade.Engine.Books;
using Marketgrade.Engine.Exchanges;
using Marketgrade.Engine.Recording;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

var builder = WebApplication.CreateBuilder(args);

// Standard output is reserved for the ready line read by the desktop shell.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));

builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .WithOrigins("tauri://localhost", "http://tauri.localhost", "http://localhost:5173")
    .AllowAnyMethod()
    .AllowAnyHeader()));
builder.Services.AddHostedService<ParentProcessWatcher>();
// The desktop shell passes the operating system's app data folder; tools and tests may leave it out.
builder.Services.AddSingleton(services =>
{
    var configured = services.GetRequiredService<IConfiguration>()["MARKETGRADE_DATA_DIR"];
    var dataFolder = string.IsNullOrEmpty(configured)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Marketgrade")
        : configured;
    Directory.CreateDirectory(dataFolder);
    return new SettingsStore(Path.Combine(dataFolder, "marketgrade.db"));
});
builder.Services.AddSingleton(services =>
{
    var store = services.GetRequiredService<SettingsStore>();
    return new RecordingSettingsStore(store, RecordingSettings.Default(Path.Combine(store.Folder, "recordings")));
});
builder.Services.AddSingleton<PairRulesStore>();
builder.Services.AddSingleton(services => new MarketEngine(
    new EngineOptions(),
    ExchangeAdapters.CreateDefault(),
    services.GetRequiredService<ILoggerFactory>().CreateLogger("Marketgrade.Engine"),
    services.GetRequiredService<RecordingSettingsStore>().Load()));
builder.Services.AddHostedService<EngineHostedService>();
builder.Services.AddHostedService<ClockOffsetService>();
builder.Services.AddSingleton<BackendMetrics>();
builder.Services.AddHostedService<LivePushService>();
builder.Services.AddSingleton<FlowSubscriptions>();
builder.Services.AddHostedService<FlowPushService>();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSignalR().AddJsonProtocol(options =>
    options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();

var token = app.Configuration["MARKETGRADE_TOKEN"];
if (string.IsNullOrEmpty(token))
{
    Console.Error.WriteLine("MARKETGRADE_TOKEN is not set.");
    return 1;
}

var expectedAuthorization = Encoding.UTF8.GetBytes($"Bearer {token}");
var startedAtUtc = DateTime.UtcNow;

app.UseCors();

app.Use(async (context, next) =>
{
    var header = context.Request.Headers.Authorization.ToString();
    // Browsers cannot set headers on a WebSocket, so the live hub also takes the token from the query string.
    // Nothing logs request URLs (Microsoft.AspNetCore logs warnings only), so the token stays out of the logs.
    if (header.Length == 0
        && context.Request.Path.StartsWithSegments(LiveHub.Path)
        && context.Request.Query.TryGetValue("access_token", out var queryToken))
    {
        header = $"Bearer {queryToken}";
    }
    var authorization = Encoding.UTF8.GetBytes(header);
    if (!CryptographicOperations.FixedTimeEquals(authorization, expectedAuthorization))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    await next(context);
});

app.MapGet("/health", () => new { status = "ok", version = EngineInfo.Version, startedAtUtc });
app.MapGet("/engine/status", (MarketEngine engine) => new { status = engine.Status, events = engine.GetRecentEvents(50) });

// The best levels of one exchange's book, from a copy the engine publishes at most 10 times per second.
app.MapGet("/books/{exchange}", (string exchange, int? levels, MarketEngine engine) =>
{
    if (!engine.HasExchange(exchange))
    {
        return Results.NotFound();
    }
    if (levels is < 1 or > BookSnapshot.MaxLevels)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["levels"] = [$"Must be between 1 and {BookSnapshot.MaxLevels}."],
        });
    }
    return engine.GetBook(exchange) is { } book
        ? Results.Ok(BookResponse.From(book, levels ?? BookResponse.DefaultLevels))
        : Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "No book yet",
            detail: "The book is published once it is read; try again in a moment.");
});

app.MapHub<LiveHub>(LiveHub.Path);

app.MapGet("/settings/recording", (RecordingSettingsStore settings) => new
{
    values = RecordingSettingsDto.From(settings.Load()),
    defaults = RecordingSettingsDto.From(settings.Defaults),
});

app.MapPut("/settings/recording", (RecordingSettingsDto values, RecordingSettingsStore settings, MarketEngine engine) =>
{
    var errors = RecordingSettingsValidator.Validate(values, out var folder);
    if (errors.Count > 0)
    {
        return Results.ValidationProblem(errors);
    }
    var recording = values.ToSettings() with { Folder = folder! };
    settings.Save(recording);
    engine.ApplyRecordingSettings(recording);
    return Results.Ok(RecordingSettingsDto.From(recording));
});

app.Lifetime.ApplicationStarted.Register(() =>
{
    // Empty under the in-memory test server; only a real listener announces itself.
    var address = app.Services.GetRequiredService<IServer>()
        .Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault();
    if (address is not null)
    {
        Console.Out.WriteLine($"MARKETGRADE_READY port={new Uri(address).Port}");
        Console.Out.Flush();
    }
});

await app.RunAsync();
return 0;
