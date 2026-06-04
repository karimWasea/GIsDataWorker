using GIsDataWorker;
using GIsDataWorker.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting.WindowsServices;
using System.Runtime.InteropServices;

var options = new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = WindowsServiceHelpers.IsWindowsService()
                        ? AppContext.BaseDirectory
                        : Directory.GetCurrentDirectory()
};

var builder = Host.CreateApplicationBuilder(options);
// بعد builder.Services.AddDbContext
builder.Services.Configure<MapSettings>(builder.Configuration.GetSection("MapSettings"));
builder.Services.Configure<PostgresSettings>(builder.Configuration.GetSection("Postgres"));
builder.Configuration
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true)
    .AddEnvironmentVariables();

 
 

builder.Services.AddWindowsService(o =>
{
    o.ServiceName = builder.Configuration["WindowsService:ServiceName"]!;
});

builder.Services.AddDbContext<ApplicationDbContext>(dbOptions =>
    dbOptions.UseNpgsql(
        builder.Configuration.GetConnectionString("DefaultConnection"),
        x => x.UseNetTopologySuite()));

builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService<MapUpdateWorker>();
 
 
var host = builder.Build();

// ── Auto-migrate on startup ✅
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