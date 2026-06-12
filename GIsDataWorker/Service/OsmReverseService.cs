using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GIsDataWorker.Models;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;

namespace GIsDataWorker.Services;

// ---------------------------------------------------------------------------
//  Reverse geocoding over osm2pgsql that matches Nominatim's output.
//
//  Nominatim builds addresses from TWO sources, not just admin boundaries:
//
//    1) boundary=administrative polygons (admin_level 2..12)
//       → country, state/governorate, county, district
//
//    2) place=* polygons AND nodes (suburb, city, town, village, ...)
//       → the named places people actually recognise
//
//  Example: Luxor (25.697, 32.637)
//    - Nominatim returns: "Luxor City, Luxor, Egypt"
//    - "Luxor City" is a place=suburb POLYGON (not an admin boundary)
//    - "Luxor" city comes from either a place=city polygon or admin boundary
//    - "Egypt" is admin_level=2
//
//  This service now queries both sources, merges them with the same priority
//  Nominatim uses (place polygons win over admin boundaries for suburb/city
//  when both exist), and returns the combined result.
//
//  No EF.Functions.Transform — all coordinate conversion is done in C# with
//  the exact EPSG:3857 Web Mercator formula.
//
//  Register: builder.Services.AddScoped<IOsmReverseService, OsmReverseService>();
// ---------------------------------------------------------------------------

public sealed class OsmReverseService : IOsmReverseService
{
    private readonly ApplicationDbContext _ctx;
    public OsmReverseService(ApplicationDbContext ctx) => _ctx = ctx;

    private static readonly string[] NonAttractionTourism =
    {
        "hotel", "hostel", "motel", "guest_house", "apartment",
        "chalet", "caravan_site", "camp_site", "information"
    };

    // Place types ordered from most specific to broadest, mirroring
    // Nominatim's place_rank hierarchy.
    private static readonly string[] PlaceTypes =
    {
        "neighbourhood", "quarter", "suburb",          // rank 22–17
        "village", "hamlet", "town", "city",           // rank 16–13
        "municipality",                                // rank 12
        "county", "district", "region", "state",       // rank 12–5
        "country"                                      // rank 4
    };

    // Which place types map to which address slot.
    private static readonly HashSet<string> SuburbTypes = new(StringComparer.OrdinalIgnoreCase)
        { "neighbourhood", "quarter", "suburb" };
    private static readonly HashSet<string> CityTypes = new(StringComparer.OrdinalIgnoreCase)
        { "village", "hamlet", "town", "city", "municipality" };
    private static readonly HashSet<string> CountyTypes = new(StringComparer.OrdinalIgnoreCase)
        { "county", "district" };
    private static readonly HashSet<string> StateTypes = new(StringComparer.OrdinalIgnoreCase)
        { "region", "state" };

    private const double EarthRadius = 6_378_137.0;

    private static Point ToMercator(double lon, double lat)
    {
        double x = lon * Math.PI / 180.0 * EarthRadius;
        double y = Math.Log(Math.Tan(Math.PI / 4.0 + lat * Math.PI / 360.0)) * EarthRadius;
        return new Point(x, y) { SRID = 3857 };
    }

    private static (double Lat, double Lon) FromMercator(double x, double y)
    {
        double lon = x / EarthRadius * 180.0 / Math.PI;
        double lat = (2.0 * Math.Atan(Math.Exp(y / EarthRadius)) - Math.PI / 2.0) * 180.0 / Math.PI;
        return (lat, lon);
    }

    // =======================================================================
    //  1) GetRegionByCoordinatesAsync
    // =======================================================================
    public async Task<OsmRegion?> GetRegionByCoordinatesAsync(
        double lat, double lon, CancellationToken ct = default)
    {
        var pointMerc = ToMercator(lon, lat);

        // ── A) Admin boundaries that CONTAIN the point ─────────────────────
        var adminRows = await _ctx.planet_osm_polygons
            .Where(p => p.boundary == "administrative"
                     && p.admin_level != null
                     && p.name != null
                     && p.way.Contains(pointMerc))
            .Select(p => new { p.admin_level, p.name, p.place, p.osm_id })
            .ToListAsync(ct);

        // ── B) Place polygons that CONTAIN the point ───────────────────────
        //    (suburb, city, town, village, neighbourhood, etc.)
        var placePolyRows = await _ctx.planet_osm_polygons
            .Where(p => p.place != null
                     && p.name != null
                     && p.way.Contains(pointMerc))
            .Select(p => new { p.place, p.name, p.osm_id })
            .ToListAsync(ct);

        // ── C) Nearest place NODES within ~5 km ────────────────────────────
        //    Many cities/suburbs exist only as nodes, not polygons.
        //    Nominatim uses a radius that depends on place type; 5 km is a
        //    reasonable approximation for city-level reverse geocoding.
        double nodeRadiusMerc = 5000.0 / Math.Max(Math.Cos(lat * Math.PI / 180.0), 0.01);

        var placeNodeRows = await _ctx.planet_osm_points
            .Where(p => p.place != null
                     && p.name != null
                     && p.way.IsWithinDistance(pointMerc, nodeRadiusMerc))
            .Select(p => new { p.place, p.name, p.osm_id, X = p.way.X, Y = p.way.Y })
            .ToListAsync(ct);

        // Nothing at all?
        if (adminRows.Count == 0 && placePolyRows.Count == 0 && placeNodeRows.Count == 0)
            return null;

        // ── Merge into a unified address ───────────────────────────────────
        // Start with slots empty; fill from most-authoritative source.
        string? country = null, state = null, county = null, city = null, suburb = null;
        long? primaryOsmId = null;
        string? primaryName = null;
        string? primaryPlace = null;
        int primaryAdminLevel = 0;

        // 1. Admin boundaries → country / state / county (and sometimes city)
        var admins = adminRows
            .Select(r => (Level: int.TryParse(r.admin_level, out var l) ? l : (int?)null,
                          r.name, r.place, r.osm_id))
            .Where(r => r.Level is not null)
            .OrderBy(r => r.Level!.Value)
            .ToList();

        foreach (var a in admins)
        {
            int lvl = a.Level!.Value;
            // Egypt:  2=country, 4=governorate(≈state), 6=district(≈county)
            // Global: 2=country, 3-4=state, 5-6=county, 7-8=city
            if (lvl == 2) country = a.name;
            else if (lvl <= 4) state ??= a.name;
            else if (lvl <= 6) county ??= a.name;
            else if (lvl <= 8) city ??= a.name;
            else suburb ??= a.name;  // 9+ = neighbourhood-level admin

            // Track the most specific admin as a candidate primary
            if (a.Level!.Value > primaryAdminLevel)
            {
                primaryAdminLevel = a.Level.Value;
                primaryName = a.name;
                primaryOsmId = a.osm_id;
                primaryPlace = a.place;
            }
        }

        // 2. Place polygons → override suburb / city from named places
        //    (these are what Nominatim actually prefers for display)
        var placesFromPolygons = placePolyRows
            .Where(r => PlaceTypes.Contains(r.place ?? ""))
            .OrderBy(r => Array.IndexOf(PlaceTypes, r.place))
            .ToList();

        foreach (var p in placesFromPolygons)
        {
            if (SuburbTypes.Contains(p.place!))
            {
                suburb ??= p.name;
                // Suburb is more specific, so it's the primary
                primaryName = p.name;
                primaryOsmId = p.osm_id;
                primaryPlace = p.place;
                primaryAdminLevel = 0; // not an admin level
            }
            else if (CityTypes.Contains(p.place!)) city ??= p.name;
            else if (CountyTypes.Contains(p.place!)) county ??= p.name;
            else if (StateTypes.Contains(p.place!)) state ??= p.name;
        }

        // 3. Place nodes → fill any remaining gaps from nearest nodes
        //    Prefer nodes that are closer; for each slot, take the first
        //    match (the list is already ordered by distance via IsWithinDistance).
        var placesFromNodes = placeNodeRows
            .Select(n =>
            {
                var (nLat, nLon) = FromMercator(n.X, n.Y);
                return new
                {
                    n.place,
                    n.name,
                    n.osm_id,
                    Dist = HaversineMeters(lat, lon, nLat, nLon)
                };
            })
            .Where(n => PlaceTypes.Contains(n.place ?? ""))
            .OrderBy(n => n.Dist)
            .ToList();

        foreach (var n in placesFromNodes)
        {
            if (SuburbTypes.Contains(n.place!) && suburb == null)
            {
                suburb = n.name;
                primaryName = n.name;
                primaryOsmId = n.osm_id;
                primaryPlace = n.place;
            }
            else if (CityTypes.Contains(n.place!) && city == null) city = n.name;
            else if (CountyTypes.Contains(n.place!) && county == null) county = n.name;
            else if (StateTypes.Contains(n.place!) && state == null) state = n.name;
        }

        // Build the full areas list for callers that need it
        var allAreas = admins
            .Select(a => new AdminArea(a.Level!.Value, a.name, a.place, a.osm_id))
            .OrderByDescending(a => a.AdminLevel)
            .ToList();

        // If we have no primary from place polygons, use the most specific admin
        primaryName ??= allAreas.FirstOrDefault()?.Name;
        primaryOsmId ??= allAreas.FirstOrDefault()?.OsmId;

        return new OsmRegion(
            Name: primaryName,
            AdminLevel: primaryAdminLevel,
            Place: primaryPlace,
            OsmId: primaryOsmId,
            Country: country,
            State: state,
            County: county,
            City: city,
            Suburb: suburb,
            Latitude: lat,
            Longitude: lon,
            Areas: allAreas);
    }

    // =======================================================================
    //  2) GetNearbyAttractionsAsync
    // =======================================================================
    public async Task<IReadOnlyList<OsmAttraction>> GetNearbyAttractionsAsync(
        double lat, double lon, int radiusMeters = 2000, int maxResults = 20,
        CancellationToken ct = default)
    {
        var pointMerc = ToMercator(lon, lat);

        double cosLat = Math.Cos(lat * Math.PI / 180.0);
        if (cosLat < 0.01) cosLat = 0.01;
        double radiusMerc = radiusMeters / cosLat;

        // --- POI nodes -------------------------------------------------------
        var pointHits = await _ctx.planet_osm_points
            .Where(p => p.name != null
                     && (p.tourism != null || p.historic != null || p.leisure != null)
                     && (p.tourism == null || !NonAttractionTourism.Contains(p.tourism))
                     && p.way.IsWithinDistance(pointMerc, radiusMerc))
            .Select(p => new RawHit
            {
                OsmId = p.osm_id,
                Name = p.name,
                Amenity = p.amenity,
                Tourism = p.tourism,
                Leisure = p.leisure,
                Shop = p.shop,
                HistoricTag = p.historic,
                X = p.way.X,
                Y = p.way.Y
            })
            .ToListAsync(ct);

        // --- POI areas (use centroid) ----------------------------------------
        var polygonHits = await _ctx.planet_osm_polygons
            .Where(p => p.name != null
                     && (p.tourism != null || p.historic != null || p.leisure != null)
                     && (p.tourism == null || !NonAttractionTourism.Contains(p.tourism))
                     && p.way.IsWithinDistance(pointMerc, radiusMerc))
            .Select(p => new RawHit
            {
                OsmId = p.osm_id,
                Name = p.name,
                Amenity = p.amenity,
                Tourism = p.tourism,
                Leisure = p.leisure,
                Shop = p.shop,
                HistoricTag = p.historic,
                X = p.way.Centroid.X,
                Y = p.way.Centroid.Y
            })
            .ToListAsync(ct);

        return pointHits.Concat(polygonHits)
            .Select(h =>
            {
                var (hLat, hLon) = FromMercator(h.X, h.Y);
                return new OsmAttraction(
                    OsmId: h.OsmId,
                    Name: h.Name,
                    Amenity: h.Amenity,
                    Tourism: h.Tourism,
                    Leisure: h.Leisure,
                    Shop: h.Shop,
                    HistoricTag: h.HistoricTag,
                    Latitude: hLat,
                    Longitude: hLon,
                    DistanceMeters: (int)Math.Round(HaversineMeters(lat, lon, hLat, hLon)));
            })
            .Where(a => a.DistanceMeters <= radiusMeters)
            .OrderBy(a => a.DistanceMeters)
            .Take(maxResults)
            .ToList();
    }

    private static double HaversineMeters(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6_371_000.0;
        double dLat = (lat2 - lat1) * Math.PI / 180.0;
        double dLon = (lon2 - lon1) * Math.PI / 180.0;
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                 + Math.Cos(lat1 * Math.PI / 180.0) * Math.Cos(lat2 * Math.PI / 180.0)
                 * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * R * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private sealed class RawHit
    {
        public long? OsmId { get; set; }
        public string? Name { get; set; }
        public string? Amenity { get; set; }
        public string? Tourism { get; set; }
        public string? Leisure { get; set; }
        public string? Shop { get; set; }
        public string? HistoricTag { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
    }
}

// ===========================================================================
//  DTOs
// ===========================================================================

public sealed record AdminArea(int AdminLevel, string? Name, string? Place, long? OsmId);

public sealed record OsmAttraction(
    long? OsmId,
    string? Name,
    string? Amenity,
    string? Tourism,
    string? Leisure,
    string? Shop,
    string? HistoricTag,
    double Latitude,
    double Longitude,
    int DistanceMeters);

/// <summary>
/// The resolved region for a coordinate. Matches Nominatim's reverse output:
///   Name       = most specific place covering the point (e.g. "Luxor City")
///   Place      = OSM place tag of that place (e.g. "suburb")
///   Suburb     = neighbourhood / quarter / suburb name
///   City       = city / town / village name
///   County     = county / district name
///   State      = state / governorate name
///   Country    = country name
/// </summary>
public sealed record OsmRegion(
    string? Name,
    int AdminLevel,
    string? Place,
    long? OsmId,
    string? Country,
    string? State,
    string? County,
    string? City,
    string? Suburb,
    double Latitude,
    double Longitude,
    IReadOnlyList<AdminArea> Areas)
{
    /// <summary>e.g. "Luxor City, Luxor, Luxor, Egypt"</summary>
    public string DisplayName =>
        string.Join(", ", new[] { Suburb, City, County, State, Country }
            .Where(s => !string.IsNullOrEmpty(s)));
}