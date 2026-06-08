using GIsDataWorker.DTos;
using GIsDataWorker.Models;
using GIsDataWorker.Service;
using GIsDataWorker.Services;
using GIsDataWorker.Utailites;

namespace GIsDataWorker
{
    public class Worker : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<Worker> _logger;
        private readonly IMongoLocationService _mongoLocationService;
        private readonly OsmImportState _osmState;

        public Worker(
            IServiceScopeFactory scopeFactory,
            ILogger<Worker> logger,
            IMongoLocationService mongoLocationService,
            OsmImportState osmState)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
            _mongoLocationService = mongoLocationService;
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
                    var parallelOptions = new ParallelOptions
                    {
                        // Number of documents to process at the same time.
                        // Start with a number like 10 and tune it based on your CPU and DB performance.
                        MaxDegreeOfParallelism = 10,
                        CancellationToken = stoppingToken
                    };

                    var processedCount = 0;
                    //var locations = new List<MongoLocationDto>();
                    //await foreach (var location in _mongoLocationService.GetLocationsAsync(stoppingToken))
                    //{
                    //    if (locations.Count >= 10) break;
                    //    locations.Add(location);
                    //}
                    await Parallel.ForEachAsync(
                        _mongoLocationService.GetLocationsAsync(stoppingToken),
                        parallelOptions,
                        async (location, token) =>
                        {
                            try
                            {
                                using var scope = _scopeFactory.CreateScope();
                                var geoService = scope.ServiceProvider.GetRequiredService<IGeoService>();

                                // ─── Regions ───────────────────────────────────────────────
                                var regions = await geoService.GetRegionByCoordinatesAsync(
                                    location.Latitude, location.Longitude);

                                var areaRegions = regions
                                    .Where(r =>
                                        r.Place == "region"   ||
                                        r.Place == "state"    ||
                                        r.Place == "county"   ||
                                        r.Place == "district" ||
                                        r.Place == "suburb"   ||
                                        r.Place == "city"     ||
                                        (int.TryParse(r.AdminLevel, out var lvl) && lvl >= 4))
                                    .OrderBy(r => int.TryParse(r.AdminLevel, out var lvl) ? lvl : int.MaxValue)
                                    .ToList();

                                if (areaRegions.Count > 0)
                                {
                                    var specific = areaRegions
                                        .Where(r => int.TryParse(r.AdminLevel, out _))
                                        .OrderByDescending(r => int.Parse(r.AdminLevel!))
                                        .FirstOrDefault()
                                        ?? areaRegions.First();

                                    var suburb = areaRegions.FirstOrDefault(r => r.Place == "suburb");

                                    _logger.LogInformation(
                                        "Mongo {collection} location {sourceId} ({name}) [lat={lat}, lng={lng}] => " +
                                        "Area: {area} | AdminLevel: {level} | Place: {place} | Suburb: {suburb} | OsmId: {id}",
                                        location.CollectionName, location.Id, location.Name ?? "N/A",
                                        location.Latitude, location.Longitude,
                                        specific.Name, specific.AdminLevel, specific.Place,
                                        suburb?.Name ?? "N/A", specific.OsmId);

                                    foreach (var region in areaRegions)
                                    {
                                        _logger.LogDebug(
                                            "  └─ Mongo {collection} location {sourceId} [lat={lat}, lng={lng}] " +
                                            "Name: {name} | AdminLevel: {level} | Place: {place} | Suburb: {suburb}",
                                            location.CollectionName, location.Id,
                                            location.Latitude, location.Longitude,
                                            region.Name, region.AdminLevel, region.Place, region.Suburb ?? "-");
                                    }
                                }
                                else
                                {
                                    _logger.LogWarning(
                                        "No region/area found for Mongo {collection} location {sourceId} ({name}) at lat={lat}, lng={lng}.",
                                        location.CollectionName, location.Id, location.Name ?? "N/A",
                                        location.Latitude, location.Longitude);
                                }

                                // ─── Attractions ───────────────────────────────────────────
                                var attractions = await geoService.GetNearbyAttractionsAsync(
                                    location.Latitude, location.Longitude, radiusMeters: 1500, maxResults: 100);

                               if (attractions.Count > 0)
                                {
                                    foreach (var attraction in attractions)
                                    {
                                        _logger.LogInformation(
                                            "Attraction for Mongo {collection} location {sourceId} ({name}) [lat={lat},lng={lng}] => " +
                                            "Name: {attractionName} | Type: {type} | Distance: {dist:F0}m",
                                            location.CollectionName, location.Id, location.Name ?? "N/A",
                                            location.Latitude, location.Longitude,
                                            attraction.Name,
                                            attraction.Amenity ?? attraction.Tourism ?? attraction.Leisure
                                                ?? attraction.Shop ?? attraction.HistoricTag ?? "unknown",
                                            attraction.DistanceMeters);
                                    }
                                }
                                else
                                {
                                    _logger.LogWarning(
                                        "No attractions found for Mongo {collection} location {sourceId} ({name}) at lat={lat}, lng={lng}.",
                                        location.CollectionName, location.Id, location.Name ?? "N/A",
                                        location.Latitude, location.Longitude);
                                }
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                _logger.LogError(ex,
                                    "Error processing Mongo {collection} location {sourceId} ({name}).",
                                    location.CollectionName, location.Id, location.Name ?? "N/A");
                            }
                        });

                    _logger.LogInformation("Finished processing {Count} MongoDB location(s).", processedCount);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("Parallel processing was cancelled.");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "GIS Worker encountered an error during parallel processing.");
                }

                await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
            }

            _logger.LogInformation("GIS Worker stopped at: {time}", DateTimeOffset.UtcNow);
        }
    }
}