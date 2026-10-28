using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Marketgrade.Backend.Tests;

public sealed class BackendFactory : WebApplicationFactory<Program>
{
    public const string Token = "test-token";

    private TempFolder? _ownFolder;
    private string? _dataFolder;

    /// <summary>
    /// Settings database and recordings of this backend, never the real app data folder. By default a
    /// temporary folder deleted on dispose; set it to a folder shared by two backends to simulate a restart.
    /// </summary>
    public string DataFolder
    {
        get => _dataFolder ??= (_ownFolder = new TempFolder()).Path;
        init => _dataFolder = value;
    }

    /// <summary>Every message the backend logged, with the backend's own log filters.</summary>
    public ConcurrentQueue<string> Logs { get; } = new();

    public HttpClient CreateAuthorizedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token);
        return client;
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        // Pooled connections keep the settings database open.
        SqliteConnection.ClearAllPools();
        _ownFolder?.Dispose();
    }

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.UseSetting("MARKETGRADE_TOKEN", Token);
        builder.UseSetting("MARKETGRADE_DATA_DIR", DataFolder);
        // Tests never connect to the real exchanges.
        builder.UseSetting("MARKETGRADE_ENGINE_DISABLED", "1");
        builder.ConfigureLogging(logging => logging.AddProvider(new CapturingLoggerProvider(Logs)));
    }

    private sealed class CapturingLoggerProvider(ConcurrentQueue<string> logs) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, logs);

        public void Dispose()
        {
        }
    }

    private sealed class CapturingLogger(string category, ConcurrentQueue<string> logs) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            logs.Enqueue($"{category}: {formatter(state, exception)} {exception}");
    }
}
