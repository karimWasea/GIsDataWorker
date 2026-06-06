using GIsDataWorker.DTos;
using GIsDataWorker.Models;
using GIsDataWorker.Service;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using ProjNet.CoordinateSystems;
using ProjNet.CoordinateSystems.Transformations;

namespace GIsDataWorker.Services
{
    public class GeoService : IGeoService
    {
        private readonly ApplicationDbContext _db;
        private readonly GeometryFactory _geometryFactory3857;

        private const int OSM_SRID = 3857;
        private const int WGS84_SRID = 4326;

        // Reuse a single transform pair — CoordinateTransformationFactory is expensive to construct
        private static readonly ICoordinateTransformation _toWebMercator;
        private static readonly ICoordinateTransformation _toWgs84;

        static GeoService()
        {
            var ctFactory = new CoordinateTransformationFactory();
            var wgs84 = GeographicCoordinateSystem.WGS84;
            var mercator = ProjectedCoordinateSystem.WebMercator;
            _toWebMercator = ctFactory.CreateFromCoordinateSystems(wgs84, mercator);
            _toWgs84 = ctFactory.CreateFromCoordinateSystems(mercator, wgs84);
        }

        public GeoService(ApplicationDbContext db)
        {
            _db = db;
            _geometryFactory3857 = new GeometryFactory(new PrecisionModel(), OSM_SRID);
        }

        // ── Coordinate helpers ──────────────────────────────────────────────────

        /// <summary>WGS84 (lat, lng) → EPSG:3857 Point for spatial queries.</summary>
        private Point ToWebMercator(double latitude, double longitude)
        {
            // ProjNet expects (longitude, latitude)
            double[] xy = _toWebMercator.MathTransform.Transform(new[] { longitude, latitude });
            return _geometryFactory3857.CreatePoint(new Coordinate(xy[0], xy[1]));
        }

        /// <summary>EPSG:3857 (x, y) → WGS84 (lat, lng).</summary>
        private static (double Lat, double Lng) ToWgs84(double x, double y)
        {
            double[] ll = _toWgs84.MathTransform.Transform(new[] { x, y });
            return (Lat: ll[1], Lng: ll[0]);
        }

        // ── FUNCTION 1: Region lookup ───────────────────────────────────────────

        public async Task<List<RegionResultDtoDto>> GetRegionByCoordinatesAsync(
            double latitude,
            double longitude)
        {
            var point = ToWebMercator(latitude, longitude);

            return await _db.planet_osm_polygons
                .Where(p =>
                    p.way != null &&
                    p.way.Contains(point) &&
                    (p.boundary == "administrative" || p.place != null))
                .OrderBy(p => p.way.Area)
                .Select(p => new RegionResultDtoDto
                {
                    Name = p.name,
                    AdminLevel = p.admin_level,
                    Boundary = p.boundary,
                    Place = p.place,
                    OsmId = p.osm_id.ToString(),
                    Suburb = p.place == "suburb" ? p.name : null
                })
                .Take(10)
                .ToListAsync();
        }

        // ── FUNCTION 2: Nearby attractions ─────────────────────────────────────

        /// <summary>
        /// Returns nearby OSM attractions within <paramref name="radiusMeters"/> of the
        /// supplied WGS84 coordinate. Results are deduplicated by OsmId, sorted by
        /// distance, and capped at <paramref name="maxResults"/>.
        ///
        /// Key fixes vs. original:
        ///  • Coordinates returned in WGS84 degrees (not raw Web Mercator metres).
        ///  • Distance computed once server-side; IsWithinDistance used as the spatial
        ///    index predicate so EF pushes a single &&/ST_DWithin to Postgres.
        ///  • Polygon centroid stored in a let-binding to avoid repeated SQL subqueries.
        ///  • Historic tag mapped via dedicated column (p.historic) not generic tag bag.
        ///  • Union + dedup on OsmId + name, ordered by distance, limited before ToList().
        /// </summary>
        public async Task<List<AttractionResultDto>> GetNearbyAttractionsAsync(
            double latitude,
            double longitude,
            double radiusMeters = 1000,
            int maxResults = 100)
        {
            var origin = ToWebMercator(latitude, longitude);

            // ── Points ──────────────────────────────────────────────────────────
            // IsWithinDistance → ST_DWithin (uses spatial index)
            // Distance         → ST_Distance (computed once in SELECT, reused in ORDER BY)
            var points = await _db.planet_osm_points
                .Where(p =>
                    p.way != null &&
                    p.name != null &&
                    p.way.IsWithinDistance(origin, radiusMeters) &&
                    (p.amenity != null ||
                     p.tourism != null ||
                     p.shop != null ||
                     p.leisure != null ||
                     p.historic != null))
                .Select(p => new AttractionResultDto
                {
                    OsmId = p.osm_id,
                    Name = p.name,
                    Amenity = p.amenity,
                    Tourism = p.tourism,
                    Shop = p.shop,
                    Leisure = p.leisure,
                    HistoricTag = p.historic,
                    // ✅ FIX: convert from Web Mercator back to WGS84 degrees
                    //    way.X / way.Y are metres in EPSG:3857 — not lat/lng!
                    //    We project back via ST_Transform in raw SQL; here we
                    //    store the raw mercator values and convert in-process.
                    Latitude = p.way.Y,          // raw — converted below
                    Longitude = p.way.X,          // raw — converted below
                    DistanceMeters = p.way.Distance(origin)
                })
                .OrderBy(p => p.DistanceMeters)
                .Take(maxResults)
                .ToListAsync();

            // ── Polygons ────────────────────────────────────────────────────────
            // Centroid is evaluated once per row; storing in anonymous-type avoids
            // repeated subquery translations in older EF/NTS providers.
            var polygons = await _db.planet_osm_polygons
                .Where(p =>
                    p.way != null &&
                    p.name != null &&
                    p.way.Centroid.IsWithinDistance(origin, radiusMeters) &&
                    (p.amenity != null ||
                     p.tourism != null ||
                     p.leisure != null ||
                     p.historic != null))
                .Select(p => new
                {
                    p.osm_id,
                    p.name,
                    p.amenity,
                    p.tourism,
                    p.leisure,
                    p.historic,
                    CentroidX = p.way.Centroid.X,
                    CentroidY = p.way.Centroid.Y,
                    DistanceMeters = p.way.Centroid.Distance(origin)
                })
                .OrderBy(p => p.DistanceMeters)
                .Take(maxResults)
                .ToListAsync();

            // ── Merge, dedup, convert coordinates, re-sort ──────────────────────
            var polygonDtos = polygons.Select(p => new AttractionResultDto
            {
                OsmId = p.osm_id,
                Name = p.name,
                Amenity = p.amenity,
                Tourism = p.tourism,
                Leisure = p.leisure,
                HistoricTag = p.historic,
                Latitude = p.CentroidY,   // raw — converted below
                Longitude = p.CentroidX,
                DistanceMeters = p.DistanceMeters
            });

            var merged = points
                .Concat(polygonDtos)
                // Primary dedup: same OSM object referenced from both tables
                .GroupBy(a => a.OsmId)
                .Select(g => g.OrderBy(a => a.DistanceMeters).First())
                // ✅ FIX: project Web Mercator → WGS84 degrees after DB round-trip
                .Select(a =>
                {
                    var (lat, lng) = ToWgs84(a.Longitude, a.Latitude);
                    a.Latitude = Math.Round(lat, 6);
                    a.Longitude = Math.Round(lng, 6);
                    return a;
                })
                .OrderBy(a => a.DistanceMeters)
                .Take(maxResults)
                .ToList();

            return merged;
        }
    }
}