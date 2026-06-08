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

            await Task.WhenAny(_osmState.OsmDataReady, Task.Delay(Timeout.Infinite, stoppingToken));

            if (stoppingToken.IsCancellationRequested) return;

            _logger.LogInformation("OSM data is ready. GIS Worker started at: {time}", DateTimeOffset.UtcNow);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var processedCount = 0;
                    var errorCount = 0;

                    var parallelOptions = new ParallelOptions
                    {
                        MaxDegreeOfParallelism = 10,  // tune based on DB performance
                        CancellationToken = stoppingToken
                    };

                    await Parallel.ForEachAsync(
                        _mongoLocationService.GetLocationsAsync(stoppingToken),
                        parallelOptions,
                        async (location, token) =>
                        {
                            try
                            {
                                // Each task gets its own scope + DbContext
                                // REQUIRED — DbContext is NOT thread-safe
                                using var scope = _scopeFactory.CreateScope();
                                var geoService = scope.ServiceProvider
                                                           .GetRequiredService<IGeoService>();

                                // ─── 1. Region ──────────────────────────────────────────
                                var region = await geoService.GetRegionByCoordinatesAsync(
                                    location.Latitude, location.Longitude);

                                if (region is not null)
                                {
                                    _logger.LogInformation(
                                        "[Region] {collection}/{sourceId} ({name}) " +
                                        "[{lat},{lng}] => {regionName} | " +
                                        "AdminLevel: {level} | Place: {place} | OsmId: {osmId}",
                                        location.CollectionName, location.Id, location.Name ?? "N/A",
                                        location.Latitude, location.Longitude,
                                        region.Name, region.AdminLevel,
                                        region.Place, region.OsmId);
                                }
                                else
                                {
                                    _logger.LogWarning(
                                        "[Region] No region found for {collection}/{sourceId} " +
                                        "({name}) at [{lat},{lng}].",
                                        location.CollectionName, location.Id, location.Name ?? "N/A",
                                        location.Latitude, location.Longitude);
                                }

                                // ─── 2. Attractions ─────────────────────────────────────
                                var attractions = await geoService.GetNearbyAttractionsAsync(
                                    location.Latitude, location.Longitude,
                                    radiusMeters: 1500,
                                    maxResults: 100);

                                if (attractions.Count > 0)
                                {
                                    foreach (var attraction in attractions)
                                    {
                                        _logger.LogInformation(
                                            "[Attraction] {collection}/{sourceId} ({name}) " +
                                            "[{lat},{lng}] => {attractionName} | " +
                                            "Type: {type} | Distance: {dist:F0}m",
                                            location.CollectionName, location.Id, location.Name ?? "N/A",
                                            location.Latitude, location.Longitude,
                                            attraction.Name,
                                            attraction.Amenity ?? attraction.Tourism ??
                                            attraction.Leisure ?? attraction.Shop ??
                                            attraction.HistoricTag ?? "unknown",
                                            attraction.DistanceMeters);
                                    }
                                }
                                else
                                {
                                    _logger.LogWarning(
                                        "[Attraction] None found for {collection}/{sourceId} " +
                                        "({name}) at [{lat},{lng}].",
                                        location.CollectionName, location.Id, location.Name ?? "N/A",
                                        location.Latitude, location.Longitude);
                                }

                                // ─── Thread-safe counter ─────────────────────────────────
                                Interlocked.Increment(ref processedCount);
                            }
                            catch (OperationCanceledException)
                            {
                                throw; // let Parallel.ForEachAsync stop cleanly
                            }
                            catch (Exception ex)
                            {
                                Interlocked.Increment(ref errorCount);
                                _logger.LogError(ex,
                                    "[Error] Failed processing {collection}/{sourceId} ({name}).",
                                    location.CollectionName, location.Id, location.Name ?? "N/A");
                            }
                        });

                    _logger.LogInformation(
                        "Batch complete — processed: {processed}, errors: {errors}.",
                        processedCount, errorCount);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("GIS Worker cancelled — shutting down.");
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "GIS Worker batch failed.");
                }

                await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
            }

            _logger.LogInformation("GIS Worker stopped at: {time}", DateTimeOffset.UtcNow);
        }
    }
}