using GIsDataWorker;
using GIsDataWorker.Models;
using GIsDataWorker.Service;
using GIsDataWorker.Services;
using GIsDataWorker.Utailites;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting.WindowsServices;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;

// ── Fix Arabic/Unicode characters showing as ???? in Windows console ──
Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;

var options = new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = WindowsServiceHelpers.IsWindowsService()
                        ? AppContext.BaseDirectory
                        : Directory.GetCurrentDirectory()
};

var builder = Host.CreateApplicationBuilder(options);
builder.Services.AddHttpClient("GIsWorkerClient")
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        SslOptions = new System.Net.Security.SslClientAuthenticationOptions
        {
            EnabledSslProtocols =
                System.Security.Authentication.SslProtocols.Tls12 |
                System.Security.Authentication.SslProtocols.Tls13
        },
        ConnectTimeout = TimeSpan.FromSeconds(30),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Brotli,
    })
    .ConfigureHttpClient(client =>
    {
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "GIsDataWorker/1.0 (osm2pgsql-auto-install)");
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
    });
builder.Services.Configure<MapSettings>(builder.Configuration.GetSection("MapSettings"));
builder.Services.Configure<MongoSettings>(builder.Configuration.GetSection("MongoSettings"));
builder.Services.Configure<PostgresSettings>(builder.Configuration.GetSection("Postgres"));
builder.Configuration
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true)
    .AddEnvironmentVariables();

 builder.Services.AddSingleton<IMongoLocationService, MongoLocationService>();

builder.Services.AddWindowsService(o =>
{
    o.ServiceName = builder.Configuration["WindowsService:ServiceName"]!;
});

builder.Services.AddDbContext<ApplicationDbContext>(dbOptions =>
    dbOptions.UseNpgsql(
        builder.Configuration.GetConnectionString("DefaultConnection"),
        x => x.UseNetTopologySuite()));

builder.Services.AddSingleton<OsmImportState>();   // ← أضف قبل AddHostedService
builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService<DataImportBackgroundService>();
builder.Services.AddScoped<IOsmReverseService, OsmReverseService>();
// في Program.cs
 
var host = builder.Build();

// ── Auto-migrate on startup ──
using (var scope = host.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    try
    {
        var pending = await db.Database.GetPendingMigrationsAsync();
        if (pending.Any())
        {
            logger.LogInformation("Applying {Count} pending migration(s)...", pending.Count());
            await db.Database.MigrateAsync();
            logger.LogInformation("Migrations applied successfully.");
        }
        else
        {
            logger.LogInformation("Database is up to date. No migrations needed.");
        }
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to apply migrations.");
        throw;
    }
}

host.Run(); 