using GIsDataWorker.DTos;
using GIsDataWorker.Models;
using GIsDataWorker.Service;
using GIsDataWorker.Services;
using GIsDataWorker.Utailites;
using System.Collections.Concurrent;
using System.Text.Json;

namespace GIsDataWorker
{
    public class Worker : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<Worker> _logger;
        private readonly IMongoLocationService _mongoLocationService;
        private readonly OsmImportState _osmState;

        private static readonly string OutputFolder = @"C:\REstmangoDB";
        private static readonly string OutputFile = Path.Combine(OutputFolder, "regions.json");

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

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

            Directory.CreateDirectory(OutputFolder);
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

                    // ── collect all regions from this batch ──────────────────
                    var regionsBag = new ConcurrentBag<object>();

                    var parallelOptions = new ParallelOptions
                    {
                        MaxDegreeOfParallelism = 10,
                        CancellationToken = stoppingToken
                    };

                    await Parallel.ForEachAsync(
                        _mongoLocationService.GetLocationsAsync(stoppingToken),
                        parallelOptions,
                        async (location, token) =>
                        {
                            try
                            {
                                using var scope = _scopeFactory.CreateScope();
                                var geoService = scope.ServiceProvider
                                                      .GetRequiredService<IOsmReverseService>();

                                // ─── 1. Region ──────────────────────────────
                                var region = await geoService.GetRegionByCoordinatesAsync(
                                    location.Latitude, location.Longitude, language: "en");

                                if (region is not null)
                                {
                                    _logger.LogInformation(
                                        "[Region] {collection}/{sourceId} ({name}) " +
                                        "input:[{inLat},{inLng}] matched:[{matchLat},{matchLng}] " +
                                        "drift:{drift:F0}m => {displayName} | " +
                                        "type:{addressType} rank:{placeRank} osmType:{osmType} osmId:{osmId}",
                                        location.CollectionName, location.Id, location.Name ?? "N/A",
                                        location.Latitude, location.Longitude,
                                        region.Latitude, region.Longitude,
                                        region.DriftMeters, region.DisplayName,
                                        region.AddressType, region.PlaceRank,
                                        region.OsmType, region.OsmId);

                                    regionsBag.Add(region);
                                }
                                else
                                {
                                    _logger.LogWarning(
                                        "[Region] No region found for {collection}/{sourceId} ({name}) at [{lat},{lng}].",
                                        location.CollectionName, location.Id, location.Name ?? "N/A",
                                        location.Latitude, location.Longitude);
                                }

                                // ─── 2. Attractions ─────────────────────────
                                var attractions = await geoService.GetNearbyAttractionsAsync(
                                    location.Latitude, location.Longitude,
                                    radiusMeters: 1500,
                                    maxResults: 100,
                                    language: "en");

                                if (attractions.Count > 0)
                                {
                                    foreach (var attraction in attractions)
                                    {
                                        _logger.LogInformation(
                                            "[Attraction] {collection}/{sourceId} ({name}) [{lat},{lng}] => " +
                                            "{attractionName} | Type: {type} | Distance: {dist:F0}m | Relevance: {rel}",
                                            location.CollectionName, location.Id, location.Name ?? "N/A",
                                            location.Latitude, location.Longitude,
                                            attraction.Name,
                                            attraction.Amenity ?? attraction.Tourism ??
                                            attraction.Leisure ?? attraction.Shop ??
                                            attraction.HistoricTag ?? "unknown",
                                            attraction.DistanceMeters, attraction.Relevance);
                                    }
                                }
                                else
                                {
                                    _logger.LogWarning(
                                        "[Attraction] None found for {collection}/{sourceId} ({name}) at [{lat},{lng}].",
                                        location.CollectionName, location.Id, location.Name ?? "N/A",
                                        location.Latitude, location.Longitude);
                                }

                                Interlocked.Increment(ref processedCount);
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception ex)
                            {
                                Interlocked.Increment(ref errorCount);
                                _logger.LogError(ex,
                                    "[Error] Failed processing {collection}/{sourceId} ({name}).",
                                    location.CollectionName, location.Id, location.Name ?? "N/A");
                            }
                        });

                    // ── write all regions to one JSON file ───────────────────
                    var json = JsonSerializer.Serialize(regionsBag.ToArray(), JsonOpts);
                    await File.WriteAllTextAsync(OutputFile, json, stoppingToken);
                    _logger.LogInformation("[JSON] {count} regions written => {path}", regionsBag.Count, OutputFile);

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