using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using GIsDataWorker.DTos;

namespace GIsDataWorker.Services;

public interface IOsmReverseService
{
    /// <summary>
    /// Resolves the place a coordinate falls in, Nominatim-style.
    /// Returns the matched OSM object's canonical coordinates (not the input),
    /// plus PlaceRank for accuracy assessment.
    /// </summary>
    Task<OsmRegion?> GetRegionByCoordinatesAsync(
        double lat, double lon, string language = "en", CancellationToken ct = default);

    /// <summary>
    /// Returns nearby attractions ranked by relevance.
    /// </summary>
    Task<IReadOnlyList<OsmAttraction>> GetNearbyAttractionsAsync(
        double lat, double lon, int radiusMeters = 2000, int maxResults = 20,
        string language = "en", CancellationToken ct = default);
}