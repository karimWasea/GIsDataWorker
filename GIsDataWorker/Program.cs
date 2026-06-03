using GIsDataWorker;
using GIsDataWorker.Models;
 using Microsoft.EntityFrameworkCore;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"),
    x => x.UseNetTopologySuite()));
 

builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService<MapUpdateWorker>();

 
var host = builder.Build();
host.Run();
