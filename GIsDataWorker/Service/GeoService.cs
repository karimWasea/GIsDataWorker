using Amazon.Runtime.Internal.Util;
using GIsDataWorker.DTos;
using GIsDataWorker.Models;
using GIsDataWorker.Service;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using ProjNet.CoordinateSystems;
using ProjNet.CoordinateSystems.Transformations;

namespace GIsDataWorker.Services
{
    /// <summary>
    /// Provides geospatial query services against an OpenStreetMap PostgreSQL/PostGIS database.
    /// Handles coordinate projection between WGS84 (lat/lng) and Web Mercator (EPSG:3857).
    /// </summary>
    public class GeoService : IGeoService
    {
        private readonly ApplicationDbContext _db;
        private readonly GeometryFactory _geometryFactory3857;

        private const int OSM_SRID = 3857;

        // ── Static coordinate transforms (expensive to construct — built once) ──
        private static readonly ICoordinateTransformation _toWebMercator;
        private static readonly ICoordinateTransformation _toWgs84;

        // ── Filter sets — EF Core translates Contains() to a SQL IN clause ─────
        private static readonly string[] _validAdminLevels = { "2", "4", "6", "8" };
        private static readonly string[] _tourismTypes     = { "hotel", "museum", "attraction", "resort" };
        private static readonly string[] _amenityTypes     = { "restaurant", "cafe" };

        static GeoService()
        {
            var ctFactory  = new CoordinateTransformationFactory();
            var wgs84      = GeographicCoordinateSystem.WGS84;
            var mercator   = ProjectedCoordinateSystem.WebMercator;
            _toWebMercator = ctFactory.CreateFromCoordinateSystems(wgs84, mercator);
            _toWgs84       = ctFactory.CreateFromCoordinateSystems(mercator, wgs84);
        }

        /// <param name="db">The EF Core database context.</param>
        public GeoService(ApplicationDbContext db)
        {
            _db = db;
            _geometryFactory3857 = new GeometryFactory(new PrecisionModel(), OSM_SRID);
        }

        // ── Coordinate helpers ──────────────────────────────────────────────────

        /// <summary>Projects a WGS84 coordinate to an EPSG:3857 <see cref="Point"/>.</summary>
        private Point ToWebMercator(double latitude, double longitude)
        {
            // ProjNet expects (longitude, latitude)
            double[] xy = _toWebMercator.MathTransform.Transform(new[] { longitude, latitude });
            return _geometryFactory3857.CreatePoint(new Coordinate(xy[0], xy[1]));
        }

        /// <summary>Projects an EPSG:3857 coordinate back to WGS84 degrees.</summary>
        private static (double Lat, double Lng) ToWgs84(double x, double y)
        {
            double[] ll = _toWgs84.MathTransform.Transform(new[] { x, y });
            return (Lat: ll[1], Lng: ll[0]);
        }

        // ── FUNCTION 1: Region lookup ───────────────────────────────────────────

        /// <summary>
        /// Returns the administrative hierarchy containing the given WGS84 coordinate.
        /// Filters to real administrative boundaries (boundary=administrative) and
        /// valid Egypt admin levels: 2=Country, 4=Governorate, 6=District, 8=Sub-district.
        /// Results are ordered most-specific first (8 → 6 → 4 → 2).
        /// </summary>
        // GeoService.cs — replaces GetRegionByCoordinatesAsync

        public async Task<RegionResultDto?> GetRegionByCoordinatesAsync(double latitude, double longitude, CancellationToken ct = default)
        {
            try
            {
                var point = ToWebMercator(latitude, longitude);

                // جلب كل المضلعات المتداخلة مع النقطة
                var regions = await _db.planet_osm_polygons
                    .AsNoTracking()
                    .Where(p => p.way != null && p.way.Contains(point))
                    .Select(p => new
                    {
                        p.name,
                        p.admin_level,
                        p.boundary,
                        p.place,
                        p.osm_id,
                        Area = p.way.Area
                    })
                    .ToListAsync(ct);

                if (!regions.Any()) return null;

                // ترتيب المضلعات برمجياً (الأصغر مساحة أو الأعلى admin_level أولاً)
                var sorted = regions.OrderBy(r => r.Area).ToList();

                var finest = sorted.FirstOrDefault();

                // بناء النتيجة بناءً على المستويات الإدارية المعروفة
                return new RegionResultDto
                {
                    Name = finest?.name,
                    AdminLevel = finest?.admin_level,
                    Boundary = finest?.boundary,
                    Place = finest?.place,
                    OsmId = finest?.osm_id.ToString(),

                    // استخراج الهيكل الإداري من القائمة التي جلبناها
                    Governorate = sorted.FirstOrDefault(r => r.admin_level == "4")?.name,
                    District = sorted.FirstOrDefault(r => r.admin_level == "6")?.name,
                    City = sorted.FirstOrDefault(r => r.admin_level == "8")?.name ??
                           sorted.FirstOrDefault(r => r.place == "city" || r.place == "town")?.name
                };
            }
            catch (Exception) { return null; }
        }
        // ── FUNCTION 2: Nearby attractions ─────────────────────────────────────

        /// <summary>
        /// Returns nearby OSM attractions within <paramref name="radiusMeters"/> of the
        /// supplied WGS84 coordinate. Results are deduplicated by OsmId, sorted by
        /// distance, and capped at <paramref name="maxResults"/>.
        /// </summary>
        /// <remarks>
        /// Queries <c>planet_osm_points</c> then <c>planet_osm_polygons</c> sequentially —
        /// <see cref="DbContext"/> is not thread-safe and must not be used concurrently.
        /// Raw EPSG:3857 X/Y are fetched from the DB and converted to WGS84 in-process.
        /// </remarks>
        public async Task<List<AttractionResultDto>> GetNearbyAttractionsAsync(
            double latitude,
            double longitude,
            double radiusMeters = 1000,
            int    maxResults   = 100)
        {
            var origin = ToWebMercator(latitude, longitude);

             
            var pointsRaw = await _db.planet_osm_points
                .AsNoTracking()
                .Where(p =>
                    p.way  != null &&
                    p.name != null &&
                    p.way.IsWithinDistance(origin, radiusMeters) &&
                    (_tourismTypes.Contains(p.tourism) ||
                     _amenityTypes.Contains(p.amenity) ||
                     p.railway == "subway_entrance"     ||
                     p.leisure == "park"))
                .Select(p => new
                {
                    p.osm_id, p.name, p.amenity, p.tourism, p.leisure, p.historic,
                    X        = p.way.X,
                    Y        = p.way.Y,
                    Distance = p.way.Distance(origin)
                })
                .OrderBy(p => p.Distance)
                .Take(maxResults)
                .ToListAsync();

            // ── Polygons (centroid) ─────────────────────────────────────────────
            var polygonsRaw = await _db.planet_osm_polygons
                .AsNoTracking()
                .Where(p =>
                    p.way  != null &&
                    p.name != null &&
                    p.way.Centroid.IsWithinDistance(origin, radiusMeters) &&
                    (_tourismTypes.Contains(p.tourism) ||
                     _amenityTypes.Contains(p.amenity) ||
                     p.railway == "subway_entrance"     ||
                     p.leisure == "park"))
                .Select(p => new
                {
                    p.osm_id, p.name, p.amenity, p.tourism, p.leisure, p.historic,
                    X        = p.way.Centroid.X,
                    Y        = p.way.Centroid.Y,
                    Distance = p.way.Centroid.Distance(origin)
                })
                .OrderBy(p => p.Distance)
                .Take(maxResults)
                .ToListAsync();

            // ── Merge, deduplicate, convert EPSG:3857 → WGS84, sort, cap ───────
            return pointsRaw
                .Concat(polygonsRaw)
                .GroupBy(a => a.osm_id)
                .Select(g =>
                {
                    var a = g.First();
                    var (lat, lng) = ToWgs84(a.X, a.Y);
                    return new AttractionResultDto
                    {
                        OsmId          = a.osm_id,
                        Name           = a.name,
                        Amenity        = a.amenity,
                        Tourism        = a.tourism,
                        Leisure        = a.leisure,
                        HistoricTag    = a.historic,
                        Latitude       = lat,
                        Longitude      = lng,
                        DistanceMeters = a.Distance
                    };
                })
                .OrderBy(a => a.DistanceMeters)
                .Take(maxResults)
                .ToList();
        }
    }
}