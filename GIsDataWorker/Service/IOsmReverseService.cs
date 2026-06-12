using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace GIsDataWorker.Services;

/// <summary>
/// Reverse geocoding over the osm2pgsql database. Implemented by
/// <see cref="OsmReverseService"/>. Registered as scoped because it depends
/// on <c>ApplicationDbContext</c>.
/// </summary>
public interface IOsmReverseService
{
    /// <summary>
    /// Returns the most specific named administrative area that contains the
    /// coordinate (e.g. city/governorate/country), or <c>null</c> if no
    /// administrative boundary covers the point.
    /// </summary>
    Task<OsmRegion?> GetRegionByCoordinatesAsync(
        double lat, double lon, CancellationToken ct = default);

    /// <summary>
    /// Returns nearby points of interest ordered by true distance.
    /// </summary>
    /// <param name="radiusMeters">Search radius in metres.</param>
    /// <param name="maxResults">Maximum number of results to return.</param>
    Task<IReadOnlyList<OsmAttraction>> GetNearbyAttractionsAsync(
        double lat, double lon, int radiusMeters = 2000, int maxResults = 20,
        CancellationToken ct = default);
}