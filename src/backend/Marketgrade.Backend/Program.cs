using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Marketgrade.Backend;
using Marketgrade.Engine;
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
    return new RecordingSettingsStore(
        new SettingsStore(Path.Combine(dataFolder, "marketgrade.db")),
        RecordingSettings.Default(Path.Combine(dataFolder, "recordings")));
});
builder.Services.AddSingleton(services => new MarketEngine(
    new EngineOptions(),
    ExchangeAdapters.CreateDefault(),
    services.GetRequiredService<ILoggerFactory>().CreateLogger("Marketgrade.Engine"),
    services.GetRequiredService<RecordingSettingsStore>().Load()));
builder.Services.AddHostedService<EngineHostedService>();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

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
    var authorization = Encoding.UTF8.GetBytes(context.Request.Headers.Authorization.ToString());
    if (!CryptographicOperations.FixedTimeEquals(authorization, expectedAuthorization))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    await next(context);
});

app.MapGet("/health", () => new { status = "ok", version = EngineInfo.Version, startedAtUtc });
app.MapGet("/engine/status", (MarketEngine engine) => new { status = engine.Status, events = engine.GetRecentEvents(50) });

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
