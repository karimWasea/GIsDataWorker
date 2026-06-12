//using System;
//using System.Collections.Generic;
//using System.Data;
//using System.Linq;
//using System.Threading;
//using System.Threading.Tasks;
//using GIsDataWorker.Models;
//using Microsoft.EntityFrameworkCore;
//using Npgsql;

//namespace GIsDataWorker.Services;

//// ---------------------------------------------------------------------------
////  Reverse geocoding over a raw osm2pgsql database (planet_osm_point /
////  planet_osm_polygon). Nothing here is Nominatim-specific: there is no
////  placex and no precomputed address hierarchy, so:
////
////    • GetRegionByCoordinatesAsync  ➜ the administrative boundaries that
////      CONTAIN the point, from smallest area up to the country.
////    • GetNearbyAttractionsAsync    ➜ tourism / historic / leisure features
////      around the point, ordered by true distance.
////
////  Key schema facts this is built on:
////    - geometry column is `way`, stored in SRID 3857 (Web Mercator)
////    - input coordinates are WGS84 (lat/lon, SRID 4326) and are transformed
////      to 3857 once per call so the GiST index on `way` is used
////    - standard osm2pgsql default-style columns are assumed to exist:
////      name, place, boundary, admin_level, tourism, historic, leisure
////
////  Run the AddReverseGeocodingIndexes migration first.
////  Register: builder.Services.AddScoped<OsmReverseService>();
//// ---------------------------------------------------------------------------

//public sealed class OsmReverseService
//{
//    private readonly ApplicationDbContext _ctx;
//    public OsmReverseService(ApplicationDbContext ctx) => _ctx = ctx;

//    // =======================================================================
//    //  1) GetRegionByCoordinatesAsync
//    // =======================================================================
//    private const string RegionSql = @"
//WITH pt AS (
//    SELECT ST_Transform(ST_SetSRID(ST_MakePoint(@lon, @lat), 4326), 3857) AS g
//)
//SELECT poly.admin_level::int AS level,
//       poly.name             AS name
//FROM planet_osm_polygon poly, pt
//WHERE poly.boundary = 'administrative'
//  AND poly.admin_level ~ '^[0-9]+$'   -- ignore non-numeric admin_level values
//  AND poly.name IS NOT NULL
//  AND ST_Contains(poly.way, pt.g)     -- bbox via GiST index, then exact PIP
//ORDER BY poly.admin_level::int DESC;  -- most specific area first, country last";

//    /// <summary>
//    /// Returns the administrative region (city / state / country …) that a
//    /// WGS84 coordinate falls in, or <c>null</c> when no named admin boundary
//    /// covers the point.
//    /// </summary>
//    public async Task<OsmRegion?> GetRegionByCoordinatesAsync(
//        double lat, double lon, CancellationToken ct = default)
//    {
//        var (conn, opened) = await OpenAsync(ct);
//        try
//        {
//            await using var cmd = new NpgsqlCommand(RegionSql, conn);
//            cmd.Parameters.AddWithValue("lat", lat);
//            cmd.Parameters.AddWithValue("lon", lon);

//            var areas = new List<AdminArea>();
//            await using var r = await cmd.ExecuteReaderAsync(ct);
//            while (await r.ReadAsync(ct))
//            {
//                areas.Add(new AdminArea(
//                    AdminLevel: r.GetInt32(0),
//                    Name: r.IsDBNull(1) ? null : r.GetString(1)));
//            }

//            return areas.Count == 0 ? null : OsmRegion.FromAdminAreas(areas, lat, lon);
//        }
//        finally { if (opened) await conn.CloseAsync(); }
//    }

//    // =======================================================================
//    //  2) GetNearbyAttractionsAsync
//    // =======================================================================
//    //
//    //  POIs live both as points and as polygons (a museum building, a park),
//    //  so we union both. Polygons use their centroid for coordinates.
//    //
//    //  Distance handling: SRID 3857 is metric but Mercator-distorted, growing
//    //  with latitude. We widen the index-filter radius by sec(lat) so real hits
//    //  aren't clipped, then refine and order by a TRUE geography distance.
//    private const string AttractionsSql = @"
//WITH pt AS (
//    SELECT ST_Transform(ST_SetSRID(ST_MakePoint(@lon, @lat), 4326), 3857) AS g3857,
//           ST_SetSRID(ST_MakePoint(@lon, @lat), 4326)                     AS g4326
//)
//SELECT osm_id, name, category, lat, lon, dist_m
//FROM (
//    SELECT pp.osm_id,
//           pp.name,
//           COALESCE(pp.tourism, pp.historic, pp.leisure)               AS category,
//           ST_Y(ST_Transform(pp.way, 4326))                            AS lat,
//           ST_X(ST_Transform(pp.way, 4326))                            AS lon,
//           ST_Distance(ST_Transform(pp.way, 4326)::geography,
//                       pt.g4326::geography)                            AS dist_m
//    FROM planet_osm_point pp, pt
//    WHERE pp.name IS NOT NULL
//      AND (pp.tourism IS NOT NULL OR pp.historic IS NOT NULL OR pp.leisure IS NOT NULL)
//      AND (pp.tourism IS NULL OR pp.tourism NOT IN
//           ('hotel','hostel','motel','guest_house','apartment','chalet',
//            'caravan_site','camp_site','information'))
//      AND ST_DWithin(pp.way, pt.g3857, @radius_merc)

//    UNION ALL

//    SELECT pg.osm_id,
//           pg.name,
//           COALESCE(pg.tourism, pg.historic, pg.leisure),
//           ST_Y(ST_Transform(ST_Centroid(pg.way), 4326)),
//           ST_X(ST_Transform(ST_Centroid(pg.way), 4326)),
//           ST_Distance(ST_Transform(ST_Centroid(pg.way), 4326)::geography,
//                       pt.g4326::geography)
//    FROM planet_osm_polygon pg, pt
//    WHERE pg.name IS NOT NULL
//      AND (pg.tourism IS NOT NULL OR pg.historic IS NOT NULL OR pg.leisure IS NOT NULL)
//      AND (pg.tourism IS NULL OR pg.tourism NOT IN
//           ('hotel','hostel','motel','guest_house','apartment','chalet',
//            'caravan_site','camp_site','information'))
//      AND ST_DWithin(pg.way, pt.g3857, @radius_merc)
//) s
//WHERE s.dist_m <= @radius_m            -- exact distance refine
//ORDER BY s.dist_m
//LIMIT @limit;";

//    /// <summary>
//    /// Returns nearby attractions ordered by real-world distance.
//    /// <paramref name="radiusMeters"/> is the true search radius in metres.
//    /// </summary>
//    public async Task<IReadOnlyList<OsmAttraction>> GetNearbyAttractionsAsync(
//        double lat, double lon, int radiusMeters = 2000, int limit = 20,
//        CancellationToken ct = default)
//    {
//        // Mercator scale factor ≈ 1 / cos(lat); clamp near the poles.
//        double cosLat = Math.Cos(lat * Math.PI / 180.0);
//        if (cosLat < 0.01) cosLat = 0.01;
//        double radiusMerc = radiusMeters / cosLat;

//        var (conn, opened) = await OpenAsync(ct);
//        try
//        {
//            await using var cmd = new NpgsqlCommand(AttractionsSql, conn);
//            cmd.Parameters.AddWithValue("lat", lat);
//            cmd.Parameters.AddWithValue("lon", lon);
//            cmd.Parameters.AddWithValue("radius_merc", radiusMerc);
//            cmd.Parameters.AddWithValue("radius_m", (double)radiusMeters);
//            cmd.Parameters.AddWithValue("limit", limit);

//            var list = new List<OsmAttraction>(limit);
//            await using var r = await cmd.ExecuteReaderAsync(ct);
//            while (await r.ReadAsync(ct))
//            {
//                list.Add(new OsmAttraction(
//                    OsmId: r.GetInt64(0),
//                    Name: r.IsDBNull(1) ? null : r.GetString(1),
//                    Category: r.IsDBNull(2) ? null : r.GetString(2),
//                    Latitude: r.GetDouble(3),
//                    Longitude: r.GetDouble(4),
//                    DistanceMeters: (int)Math.Round(r.GetDouble(5))));
//            }
//            return list;
//        }
//        finally { if (opened) await conn.CloseAsync(); }
//    }

//    // ---- shared connection helper -----------------------------------------
//    // Reuses the DbContext's connection; only closes it if we opened it.
//    private async Task<(NpgsqlConnection conn, bool opened)> OpenAsync(CancellationToken ct)
//    {
//        var conn = (NpgsqlConnection)_ctx.Database.GetDbConnection();
//        if (conn.State == ConnectionState.Open) return (conn, false);
//        await conn.OpenAsync(ct);
//        return (conn, true);
//    }
//}

//// ===========================================================================
////  DTOs  (plain results, not EF entities — no DbSet needed)
//// ===========================================================================

///// <summary>One administrative area covering the point.</summary>
//public sealed record AdminArea(int AdminLevel, string? Name);

///// <summary>A nearby point of interest.</summary>
//public sealed record OsmAttraction(
//    long OsmId,
//    string? Name,
//    string? Category,
//    double Latitude,
//    double Longitude,
//    int DistanceMeters);

///// <summary>
///// The resolved region for a coordinate, flattened from the covering admin
///// areas into the usual buckets. The raw <see cref="Areas"/> list is kept for
///// anything finer-grained.
///// </summary>
//public sealed record OsmRegion(
//    string? Country,
//    string? State,
//    string? County,
//    string? City,
//    string? Suburb,
//    double Latitude,
//    double Longitude,
//    IReadOnlyList<AdminArea> Areas)
//{
//    // OSM admin_level → human buckets. These are sensible DEFAULTS, but the
//    // meaning of each level varies by country (e.g. in Egypt level 4 is a
//    // governorate). Tune per country if you need exact labels.
//    //   2        country
//    //   3– 4     state / region / governorate
//    //   5– 6     county / district
//    //   7– 8     city / municipality
//    //   9–12     suburb / neighbourhood
//    public static OsmRegion FromAdminAreas(
//        IReadOnlyList<AdminArea> areas, double lat, double lon)
//    {
//        // Within a band, take the most specific (highest admin_level) present.
//        string? Pick(int lo, int hi) =>
//            areas.Where(a => a.AdminLevel >= lo && a.AdminLevel <= hi)
//                 .OrderByDescending(a => a.AdminLevel)
//                 .FirstOrDefault()?.Name;

//        return new OsmRegion(
//            Country: Pick(2, 2),
//            State: Pick(3, 4),
//            County: Pick(5, 6),
//            City: Pick(7, 8),
//            Suburb: Pick(9, 12),
//            Latitude: lat,
//            Longitude: lon,
//            Areas: areas);
//    }

//    /// <summary>e.g. "Zamalek, Cairo, Cairo Governorate, Egypt"</summary>
//    public string DisplayName =>
//        string.Join(", ", new[] { Suburb, City, County, State, Country }
//            .Where(s => !string.IsNullOrEmpty(s)));
//}