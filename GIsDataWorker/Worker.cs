using GIsDataWorker.DTOs;
using GIsDataWorker.Services;

namespace GIsDataWorker;

public sealed class Worker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<Worker> _logger;

    /// <summary>
    /// Coordinates to query on each iteration.
    /// Consider moving these to appsettings.json / IOptions for production use.
    /// </summary>
    private static readonly IReadOnlyList<(double Latitude, double Longitude)> Coordinates =
    [
        (30.05041, 31.23214),
        (30.06762, 31.22259)
    ];

    private static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(60);

    public Worker(IServiceScopeFactory scopeFactory, ILogger<Worker> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("GIS Worker started at {Time}.", DateTimeOffset.UtcNow);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessRegionsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Graceful shutdown – don't log as error.
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GIS Worker encountered an error.");
            }

            await Task.Delay(PollingInterval, stoppingToken);
        }

        _logger.LogInformation("GIS Worker stopped at {Time}.", DateTimeOffset.UtcNow);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Region processing
    // ────────────────────────────────────────────────────────────────────────

    private async Task ProcessRegionsAsync(CancellationToken cancellationToken)
    {
        var tasks = Coordinates.Select(c => FetchRegionsForCoordinateAsync(c, cancellationToken));
        var results = await Task.WhenAll(tasks);

        foreach (var (regions, coord) in results)
        {
            if (regions.Count == 0)
            {
                _logger.LogWarning("No region/area found for lat={Latitude}, lng={Longitude}.",
                    coord.Latitude, coord.Longitude);
                continue;
            }

            LogRegionResults(regions, coord);
        }
    }

    private async Task<(List<RegionResultDto> Regions, (double Latitude, double Longitude) Coord)>
        FetchRegionsForCoordinateAsync(
            (double Latitude, double Longitude) coord,
            CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var geoService = scope.ServiceProvider.GetRequiredService<IGeoService>();

        var allRegions = await geoService.GetRegionByCoordinatesAsync(
            coord.Latitude, coord.Longitude, cancellationToken);

        var filtered = allRegions
            .Where(IsRelevantRegion)
            .OrderBy(r => ParseAdminLevel(r.AdminLevel))
            .ToList();

        return (filtered, coord);
    }

    private void LogRegionResults(
        List<RegionResultDto> regions,
        (double Latitude, double Longitude) coord)
    {
        // Most specific region = highest admin level
        var specific = regions
            .Where(r => int.TryParse(r.AdminLevel, out _))
            .OrderByDescending(r => int.Parse(r.AdminLevel!))
            .FirstOrDefault()
            ?? regions[0];

        var suburb = regions.FirstOrDefault(r => r.Place == "suburb");

        _logger.LogInformation(
            "Location [lat={Latitude}, lng={Longitude}] => " +
            "Area: {Name} | AdminLevel: {AdminLevel} | Place: {Place} | " +
            "Suburb: {Suburb} | OsmId: {OsmId}",
            coord.Latitude, coord.Longitude,
            specific.Name, specific.AdminLevel, specific.Place,
            suburb?.Name ?? "N/A", specific.OsmId);

        foreach (var region in regions)
        {
            _logger.LogDebug(
                "  └─ [lat={Latitude}, lng={Longitude}] Name: {Name} | AdminLevel: {AdminLevel} | Place: {Place} | Suburb: {Suburb}",
                coord.Latitude, coord.Longitude,
                region.Name, region.AdminLevel, region.Place,
                region.Suburb ?? "-");
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Helpers
    // ────────────────────────────────────────────────────────────────────────

    private static bool IsRelevantRegion(RegionResultDto r) =>
        r.Place is "region" or "state" or "county" or "district" or "suburb" or "city"
        || ParseAdminLevel(r.AdminLevel) >= 4;

    private static int ParseAdminLevel(string? adminLevel) =>
        int.TryParse(adminLevel, out var level) ? level : int.MaxValue;
}