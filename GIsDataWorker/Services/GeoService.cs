using GIsDataWorker.DTOs;
using GIsDataWorker.Models;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using ProjNet.CoordinateSystems;
using ProjNet.CoordinateSystems.Transformations;

namespace GIsDataWorker.Services;

public sealed class GeoService : IGeoService
{
    private readonly ApplicationDbContext _db;
    private readonly GeometryFactory _geometryFactory3857;

    // Lazy-initialized and cached coordinate transform (thread-safe, allocation-free after first use).
    private static readonly Lazy<MathTransform> _wgs84ToMercator = new(() =>
    {
        var ctFactory = new CoordinateTransformationFactory();
        var transform = ctFactory.CreateFromCoordinateSystems(
            GeographicCoordinateSystem.WGS84,
            ProjectedCoordinateSystem.WebMercator);
        return transform.MathTransform;
    });

    /// <summary>osm2pgsql stores all geometries in SRID 3857 (Web Mercator).</summary>
    private const int OsmSrid = 3857;

    private const int DefaultRegionLimit = 10;

    public GeoService(ApplicationDbContext db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _geometryFactory3857 = new GeometryFactory(new PrecisionModel(), OsmSrid);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Coordinate projection
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Projects a WGS-84 (lat/lng in degrees) coordinate to Web Mercator (x/y in metres).
    /// </summary>
    private Point ToWebMercator(double latitude, double longitude)
    {
        // ProjNet expects (longitude, latitude) order.
        double[] result = _wgs84ToMercator.Value.Transform(new[] { longitude, latitude });
        return _geometryFactory3857.CreatePoint(new Coordinate(result[0], result[1]));
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Regions
    // ────────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<List<RegionResultDto>> GetRegionByCoordinatesAsync(
        double latitude,
        double longitude,
        CancellationToken cancellationToken = default)
    {
        var point = ToWebMercator(latitude, longitude);

        return await _db.planet_osm_polygons
            .AsNoTracking()
            .Where(p =>
                p.way != null &&
                p.way.Contains(point) &&
                (p.boundary == "administrative" || p.place != null))
            .OrderBy(p => p.way!.Area)
            .Select(p => new RegionResultDto
            {
                Name = p.name ?? "Unnamed",
                AdminLevel = p.admin_level,
                Boundary = p.boundary,
                Place = p.place,
                OsmId = p.osm_id.ToString(),
                Suburb = p.place == "suburb" ? p.name : null
            })
            .Take(DefaultRegionLimit)
            .ToListAsync(cancellationToken);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Nearby Attractions
    // ────────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<List<AttractionResultDto>> GetNearbyAttractionsAsync(
        double latitude,
        double longitude,
        double radiusMeters = 1000,
        CancellationToken cancellationToken = default)
    {
        var origin = ToWebMercator(latitude, longitude);

        var pointAttractionsTask = _db.planet_osm_points
            .AsNoTracking()
            .Where(p =>
                p.way != null &&
                p.way.IsWithinDistance(origin, radiusMeters) &&
                (p.amenity != null ||
                 p.tourism != null ||
                 p.shop != null ||
                 p.leisure != null ||
                 p.historic != null) &&
                p.name != null)
            .Select(p => new AttractionResultDto
            {
                OsmId = p.osm_id,
                Name = p.name,
                Amenity = p.amenity,
                Tourism = p.tourism,
                Shop = p.shop,
                Leisure = p.leisure,
                HistoricTag = p.historic,
                Latitude = p.way!.Y,
                Longitude = p.way.X,
                DistanceMeters = p.way.Distance(origin)
            })
            .ToListAsync(cancellationToken);

        var polygonAttractionsTask = _db.planet_osm_polygons
            .AsNoTracking()
            .Where(p =>
                p.way != null &&
                p.way.Centroid.IsWithinDistance(origin, radiusMeters) &&
                (p.amenity != null ||
                 p.tourism != null ||
                 p.leisure != null ||
                 p.historic != null) &&
                p.name != null)
            .Select(p => new AttractionResultDto
            {
                OsmId = p.osm_id,
                Name = p.name,
                Amenity = p.amenity,
                Tourism = p.tourism,
                Leisure = p.leisure,
                HistoricTag = p.historic,
                Latitude = p.way!.Centroid.Y,
                Longitude = p.way.Centroid.X,
                DistanceMeters = p.way.Centroid.Distance(origin)
            })
            .ToListAsync(cancellationToken);

        // Run both queries concurrently.
        var pointAttractions = await pointAttractionsTask;
        var polygonAttractions = await polygonAttractionsTask;

        return pointAttractions
            .Concat(polygonAttractions)
            .GroupBy(a => a.OsmId)
            .Select(g => g.First())
            .OrderBy(a => a.DistanceMeters)
            .ToList();
    }
}
