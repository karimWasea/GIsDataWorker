using GIsDataWorker.Models;
using GIsDataWorker.Services;

namespace GIsDataWorker
{
    public class Worker : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<Worker> _logger;
        private readonly OsmImportState _osmState;

        private readonly List<(double Latitude, double Longitude)> _coordinates = new()
        {
            (30.05041, 31.23214),
            (30.06762, 31.22259)
        };

        public Worker(IServiceScopeFactory scopeFactory, ILogger<Worker> logger, OsmImportState osmState)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
            _osmState = osmState;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("GIS Worker waiting for OSM data to be ready...");

            // ✅ Wait until MapUpdateWorker confirms OSM import is done
            await Task.WhenAny(_osmState.OsmDataReady, Task.Delay(Timeout.Infinite, stoppingToken));

            if (stoppingToken.IsCancellationRequested) return;

            _logger.LogInformation("OSM data is ready. GIS Worker started at: {time}", DateTimeOffset.UtcNow);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // ── Regions ──────────────────────────────────────────────────
                    var regionTasks = _coordinates
                        .Select(async c =>
                        {
                            using var scope = _scopeFactory.CreateScope();
                            var geoService = scope.ServiceProvider.GetRequiredService<GeoService>();

                            var regions = await geoService.GetRegionByCoordinatesAsync(c.Latitude, c.Longitude);

                            var areaRegions = regions
      .Where(r =>
          r.Place == "region" ||
          r.Place == "state" ||
          r.Place == "county" ||
          r.Place == "district" ||
          r.Place == "suburb" ||
          r.Place == "city" ||
          (int.TryParse(r.AdminLevel, out var lvl) && lvl >= 4))  // ✅ parse string → int
      .OrderBy(r => int.TryParse(r.AdminLevel, out var lvl) ? lvl : int.MaxValue)  // ✅ sort correctly
      .ToList();

                            return (regions: areaRegions, coord: c);
                        });

                    var allRegions = await Task.WhenAll(regionTasks);

                    foreach (var result in allRegions)
                    {
                        var regions = result.regions;
                        var coord = result.coord;

                        if (regions.Any())
                        {
                            // ✅ Most specific area by highest AdminLevel
                            var specific = regions
                                .Where(r => int.TryParse(r.AdminLevel, out _))
                                .OrderByDescending(r => int.Parse(r.AdminLevel!))
                                .FirstOrDefault()
                                ?? regions.First();

                            // ✅ Suburb specifically
                            var suburb = regions.FirstOrDefault(r => r.Place == "suburb");

                            _logger.LogInformation(
                                "Location [lat={lat}, lng={lng}] => " +
                                "Area: {name} | AdminLevel: {level} | Place: {place} | " +
                                "Suburb: {suburb} | OsmId: {id}",
                                coord.Latitude, coord.Longitude,
                                specific.Name,
                                specific.AdminLevel,
                                specific.Place,
                                suburb?.Name ?? "N/A",    // ✅ suburb name or N/A
                                specific.OsmId);

                            // ✅ Also log all matched regions for full visibility
                            foreach (var region in regions)
                            {
                                _logger.LogDebug(
                                    "  └─ [lat={lat}, lng={lng}] Name: {name} | AdminLevel: {level} | Place: {place} | Suburb: {suburb}",
                                    coord.Latitude, coord.Longitude,
                                    region.Name, region.AdminLevel, region.Place,
                                    region.Suburb ?? "-");
                            }
                        }
                        else
                        {
                            _logger.LogWarning(
                                "No region/area found for lat={lat}, lng={lng}.",
                                coord.Latitude, coord.Longitude);
                        }
                    }

                     //  ── Nearby Attractions(uncomment to enable) ─────────────────
                        var attractionTasks = _coordinates
                            .Select(async c =>
                            {
                                using var scope = _scopeFactory.CreateScope();
                                var geoService = scope.ServiceProvider.GetRequiredService<GeoService>();
                                var attractions = await geoService.GetNearbyAttractionsAsync(c.Latitude, c.Longitude, radiusMeters: 1500, maxResults: 100);
                                return (attractions, coord: c);
                            });

                    var allAttractions = await Task.WhenAll(attractionTasks);

                    foreach (var (attractions, coord) in allAttractions)
                    {
                        if (attractions.Any())
                            foreach (var attraction in attractions)
                                _logger.LogInformation(
                                    "Attraction [lat={lat},lng={lng}] => Name: {name} | Type: {type} | Distance: {dist:F0}m",
                                    coord.Latitude, coord.Longitude,
                                    attraction.Name,
                                    attraction.Amenity ?? attraction.Tourism ?? attraction.Leisure ?? attraction.Shop ?? attraction.HistoricTag ?? "unknown",
                                    attraction.DistanceMeters);
                        else
                            _logger.LogWarning("No attractions found for lat={lat}, lng={lng}.",
                                coord.Latitude, coord.Longitude);
                    }
                }
                    catch (Exception ex)
                {
                    _logger.LogError(ex, "GIS Worker encountered an error.");
                    }

                await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
            }

            _logger.LogInformation("GIS Worker stopped at: {time}", DateTimeOffset.UtcNow);
        }
    }
}