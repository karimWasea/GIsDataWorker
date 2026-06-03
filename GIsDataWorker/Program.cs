using GIsDataWorker;
using GIsDataWorker.Models;
 using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting.WindowsServices;
using System.Runtime.InteropServices;

// ── Fix content root so appsettings.json is found from the .exe folder,
//    not System32 (which is the default working directory for Windows Services)
var options = new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = WindowsServiceHelpers.IsWindowsService()
                        ? AppContext.BaseDirectory
                        : Directory.GetCurrentDirectory()
};

var builder = Host.CreateApplicationBuilder(options);

// ── Load config from the exe's folder (critical for services)
builder.Configuration
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddJsonFile(
        $"appsettings.{builder.Environment.EnvironmentName}.json",
        optional: true,
        reloadOnChange: true)
    .AddEnvironmentVariables();

// ── Windows Service lifetime + Windows Event Log
builder.Services.AddWindowsService(o =>
{
    o.ServiceName = builder.Configuration["WindowsService:ServiceName"] !;
});


// ── Database
builder.Services.AddDbContext<ApplicationDbContext>(dbOptions =>
    dbOptions.UseNpgsql(
        builder.Configuration.GetConnectionString("DefaultConnection"),
    x => x.UseNetTopologySuite()));
 
// ── Hosted services
builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService<MapUpdateWorker>();

 
var host = builder.Build();
host.Run();
