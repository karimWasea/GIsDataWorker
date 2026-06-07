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
        /// <summary>EF Core database context targeting the OSM PostGIS schema.</summary>
        private readonly ApplicationDbContext _db;

        /// <summary>NTS geometry factory scoped to EPSG:3857 (Web Mercator).</summary>
        private readonly GeometryFactory _geometryFactory3857;

        /// <summary>SRID used by OSM tiles and PostGIS spatial columns.</summary>
        private const int OSM_SRID = 3857;

        /// <summary>SRID for standard GPS coordinates (degrees).</summary>
        private const int WGS84_SRID = 4326;

        /// <summary>Reusable WGS84 → Web Mercator transform.</summary>
        private static readonly ICoordinateTransformation _toWebMercator;

        /// <summary>Reusable Web Mercator → WGS84 transform.</summary>
        private static readonly ICoordinateTransformation _toWgs84;

        /// <summary>
        /// Initializes the shared coordinate transformations.
        /// <see cref="CoordinateTransformationFactory"/> is expensive to construct so it is created once.
        /// </summary>
        static GeoService()
        {
            var ctFactory = new CoordinateTransformationFactory();
            var wgs84 = GeographicCoordinateSystem.WGS84;
            var mercator = ProjectedCoordinateSystem.WebMercator;
            _toWebMercator = ctFactory.CreateFromCoordinateSystems(wgs84, mercator);
            _toWgs84 = ctFactory.CreateFromCoordinateSystems(mercator, wgs84);
        }

        /// <summary>
        /// Initializes a new instance of <see cref="GeoService"/>.
        /// </summary>
        /// <param name="db">The EF Core database context.</param>
        public GeoService(ApplicationDbContext db)
        {
            _db = db;
            _geometryFactory3857 = new GeometryFactory(new PrecisionModel(), OSM_SRID);
        }

        // ── Coordinate helpers ──────────────────────────────────────────────────

        /// <summary>
        /// Projects a WGS84 coordinate to an EPSG:3857 <see cref="Point"/> for spatial queries.
        /// </summary>
        /// <param name="latitude">Latitude in decimal degrees.</param>
        /// <param name="longitude">Longitude in decimal degrees.</param>
        /// <returns>A <see cref="Point"/> in Web Mercator metres.</returns>
        private Point ToWebMercator(double latitude, double longitude)
        {
            // ProjNet expects (longitude, latitude)
            double[] xy = _toWebMercator.MathTransform.Transform(new[] { longitude, latitude });
            return _geometryFactory3857.CreatePoint(new Coordinate(xy[0], xy[1]));
        }

        /// <summary>
        /// Projects an EPSG:3857 coordinate back to WGS84 degrees.
        /// </summary>
        /// <param name="x">Easting in Web Mercator metres.</param>
        /// <param name="y">Northing in Web Mercator metres.</param>
        /// <returns>A tuple of <c>(Lat, Lng)</c> in decimal degrees.</returns>
        private static (double Lat, double Lng) ToWgs84(double x, double y)
        {
            double[] ll = _toWgs84.MathTransform.Transform(new[] { x, y });
            return (Lat: ll[1], Lng: ll[0]);
        }

        // ── FUNCTION 1: Region lookup ───────────────────────────────────────────

        /// <summary>
        /// Returns the administrative hierarchy containing the given WGS84 coordinate.
        /// Filters to real administrative boundaries only (boundary=administrative)
        /// and the valid admin levels for Egypt: 2=Country, 4=Governorate, 6=District, 8=Sub-district.
        /// Results are ordered most-specific first (8 → 6 → 4 → 2).
        /// </summary>
        /// <param name="latitude">Latitude in decimal degrees (WGS84).</param>
        /// <param name="longitude">Longitude in decimal degrees (WGS84).</param>
        /// <returns>
        /// A list of <see cref="RegionResultDtoDto"/> ordered from the most-local
        /// to the broadest containing region.
        /// </returns>
        public async Task<List<RegionResultDtoDto>> GetRegionByCoordinatesAsync(
            double latitude,
            double longitude)
        {
            // Valid admin levels for Egypt (expand if needed for other countries)
            var validAdminLevels = new[] { "2", "4", "6", "8" };

            var point = ToWebMercator(latitude, longitude);

            var results = await _db.planet_osm_polygons
                .Where(p =>
                    p.way != null                          &&
                    p.name != null                         &&   // must have a name
                    p.boundary == "administrative"         &&   // only real admin boundaries
                    p.admin_level != null                  &&   // must have admin_level
                    validAdminLevels.Contains(p.admin_level) && // only known levels
                    p.way.Contains(point))                      // point is inside polygon
                .OrderByDescending(p => p.admin_level)          // most specific first: 8→6→4→2
                .Select(p => new RegionResultDtoDto
                {
                    Name       = p.name,
                    AdminLevel = p.admin_level,
                    Boundary   = p.boundary,
                    Place      = p.place,
                    OsmId      = p.osm_id.ToString(),
                    Suburb     = p.admin_level == "8" ? p.name : null  // level 8 = sub-district
                })
                .Take(5)   // max 4 levels (2,4,6,8) + 1 safety margin
                .ToListAsync();

            return results;
        }

        // ── FUNCTION 2: Nearby attractions ─────────────────────────────────────

        /// <summary>
        /// Returns nearby OSM attractions within <paramref name="radiusMeters"/> of the
        /// supplied WGS84 coordinate. Results are deduplicated by OsmId, sorted by
        /// distance, and capped at <paramref name="maxResults"/>.
        /// </summary>
        /// <param name="latitude">Origin latitude in decimal degrees (WGS84).</param>
        /// <param name="longitude">Origin longitude in decimal degrees (WGS84).</param>
        /// <param name="radiusMeters">Search radius in metres. Default is 1 000 m.</param>
        /// <param name="maxResults">Maximum number of results to return. Default is 100.</param>
        /// <returns>
        /// A deduplicated list of <see cref="AttractionResultDto"/> with WGS84 coordinates,
        /// ordered by distance from the origin.
        /// </returns>
        /// <remarks>
        /// Queries both <c>planet_osm_points</c> and <c>planet_osm_polygons</c> (using the
        /// polygon centroid). Results are merged in-process after each query uses
        /// <c>ST_DWithin</c> via <see cref="NetTopologySuite.Geometries.Geometry.IsWithinDistance"/>
        /// to leverage the PostGIS spatial index.
        /// </remarks>
        public async Task<List<AttractionResultDto>> GetNearbyAttractionsAsync(
            double latitude,
            double longitude,
            double radiusMeters = 1000,
            int maxResults = 100)
        {
            var origin = ToWebMercator(latitude, longitude);

            // ── Points ──────────────────────────────────────────────────────────
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
                    Latitude = p.way.Y,
                    Longitude = p.way.X,
                    DistanceMeters = p.way.Distance(origin)
                })
                .OrderBy(p => p.DistanceMeters)
                .Take(maxResults)
                .ToListAsync();

            // ── Polygons ────────────────────────────────────────────────────────
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
                Latitude = p.CentroidY,
                Longitude = p.CentroidX,
                DistanceMeters = p.DistanceMeters
            });

            var merged = points
                .Concat(polygonDtos)
                .GroupBy(a => a.OsmId)
                .Select(g => g.OrderBy(a => a.DistanceMeters).First())
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