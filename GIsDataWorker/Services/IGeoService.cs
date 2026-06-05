using GIsDataWorker.DTOs;

namespace GIsDataWorker.Services;

public interface IGeoService
{
    /// <summary>
    /// Returns administrative regions that contain the given WGS-84 coordinate,
    /// ordered by area (smallest first).
    /// </summary>
    Task<List<RegionResultDto>> GetRegionByCoordinatesAsync(
        double latitude,
        double longitude,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns points of interest within <paramref name="radiusMeters"/> of
    /// the given WGS-84 coordinate, ordered by distance (nearest first).
    /// </summary>
    Task<List<AttractionResultDto>> GetNearbyAttractionsAsync(
        double latitude,
        double longitude,
        double radiusMeters = 1000,
        CancellationToken cancellationToken = default);
}
