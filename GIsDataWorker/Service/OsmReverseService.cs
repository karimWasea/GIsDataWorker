using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GIsDataWorker.DTos;
using GIsDataWorker.Models;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;

namespace GIsDataWorker.Services;

// ===========================================================================
//  OsmReverseService — Nominatim-matching reverse geocoding over osm2pgsql.
//
//  KEY DESIGN DECISIONS (matching Nominatim behaviour):
//
//  1. RESPONSE COORDINATES ≠ INPUT COORDINATES
//     Nominatim returns the center of the matched OSM object, not the
//     coordinates you sent. We do the same: OsmRegion.Latitude/Longitude
//     are the matched place's canonical coords. The input coords are stored
//     separately as InputLatitude/InputLongitude for reference.
//
//  2. NEAREST PLACE NODE AS PRIMARY RESULT
//     In Egypt (and many countries), suburbs/neighbourhoods are OSM nodes,
//     not polygons. Nominatim finds the nearest place=suburb/town/city NODE
//     and uses it as the primary result (osm_type=node, type=suburb).
//
//  3. PLACE_RANK
//     Nominatim assigns a numeric rank (suburb=19, town=18, city=15-16).
//     Higher rank = smaller area = less coordinate drift = more accurate.
//
//  4. MULTILINGUAL NAMES
//     Translated names live in the middle tables (planet_osm_nodes/ways/rels)
//     as jsonb tags. Fallback: name:{lang} → int_name → name:en → name.
//
//  Register: builder.Services.AddScoped<IOsmReverseService, OsmReverseService>();
// ===========================================================================

public sealed class OsmReverseService : IOsmReverseService
{
    private readonly ApplicationDbContext _ctx;
    public OsmReverseService(ApplicationDbContext ctx) => _ctx = ctx;

    private const double EarthRadius = 6_378_137.0;

    // ---- Nominatim place_rank mapping ------------------------------------
    private static int ToPlaceRank(string? place) => place?.ToLowerInvariant() switch
    {
        "neighbourhood" or "quarter" or "city_block" => 22,
        "suburb" => 19,
        "town" => 18,
        "village" => 17,
        "hamlet" => 20,
        "city" or "municipality" => 16,
        "borough" => 18,
        "county" or "district" or "subdistrict" => 12,
        "state" or "region" or "province" => 8,
        "country" => 4,
        _ => 25,
    };

    // Specificity for PRIMARY selection (higher = more specific area).
    private static int PlaceSpecificity(string? place) => place?.ToLowerInvariant() switch
    {
        "city_block" or "neighbourhood" or "quarter" => 6,
        "suburb" => 5,
        "hamlet" => 4,
        "village" => 3,
        "town" or "borough" => 2,
        "city" or "municipality" => 1,
        _ => 0,
    };

    private static readonly HashSet<string> SuburbTypes = new(StringComparer.OrdinalIgnoreCase)
        { "neighbourhood", "quarter", "city_block", "suburb" };
    private static readonly HashSet<string> CityTypes = new(StringComparer.OrdinalIgnoreCase)
        { "city", "town", "village", "hamlet", "municipality", "borough" };
    private static readonly HashSet<string> CountyTypes = new(StringComparer.OrdinalIgnoreCase)
        { "county", "district", "subdistrict" };
    private static readonly HashSet<string> StateTypes = new(StringComparer.OrdinalIgnoreCase)
        { "state", "region", "province" };

    private static readonly string[] NonAttractionTourism =
    {
        "hotel", "hostel", "motel", "guest_house", "apartment",
        "chalet", "caravan_site", "camp_site", "information"
    };

    // =======================================================================
    //  1) GetRegionByCoordinatesAsync
    // =======================================================================
    public async Task<OsmRegion?> GetRegionByCoordinatesAsync(
        double lat, double lon, string language = "en", CancellationToken ct = default)
    {
        // ── Coordinate validation ──────────────────────────────────────────
        if (lat < -90 || lat > 90 || lon < -180 || lon > 180)
            return null;

        // Detect lat/lon swap (lat should be smaller than lon for most of
        // the Eastern Hemisphere; this catches the common Egypt mistake).
        if (Math.Abs(lat) > 90)
            return null;

        var pt = ToMercator(lon, lat);
        double cosLat = Math.Max(Math.Cos(lat * Math.PI / 180.0), 0.01);

        // ── (1) Nearest place NODES ────────────────────────────────────────
        double nodeRadiusMerc = 12_000.0 / cosLat;

        var placeNodes = await _ctx.planet_osm_points
            .Where(p => p.place != null && p.name != null
                     && p.way.IsWithinDistance(pt, nodeRadiusMerc))
            .OrderBy(p => p.way.Distance(pt))
            .Take(60)
            .Select(p => new { p.place, p.name, p.osm_id, X = p.way.X, Y = p.way.Y })
            .ToListAsync(ct);

        // ── (2) Place POLYGONS containing the point ────────────────────────
        var placePolys = await _ctx.planet_osm_polygons
            .Where(p => p.place != null && p.name != null && p.way.Contains(pt))
            .Select(p => new { p.place, p.name, p.osm_id, X = p.way.Centroid.X, Y = p.way.Centroid.Y })
            .ToListAsync(ct);

        // ── (3) Admin boundary POLYGONS containing the point ───────────────
        var admins = await _ctx.planet_osm_polygons
            .Where(p => p.boundary == "administrative"
                     && p.admin_level != null && p.name != null
                     && p.way.Contains(pt))
            .Select(p => new { p.admin_level, p.name, p.osm_id, X = p.way.Centroid.X, Y = p.way.Centroid.Y })
            .ToListAsync(ct);

        if (placeNodes.Count == 0 && placePolys.Count == 0 && admins.Count == 0)
            return null;

        // ---- Build unified candidate list ---------------------------------
        var placeCands = new List<PlaceCand>();

        foreach (var n in placeNodes)
        {
            var (nLat, nLon) = FromMercator(n.X, n.Y);
            placeCands.Add(new PlaceCand(
                n.place!, n.name, n.osm_id ?? 0, Src.Node,
                HaversineMeters(lat, lon, nLat, nLon), nLat, nLon));
        }
        foreach (var p in placePolys)
        {
            var (pLat, pLon) = FromMercator(p.X, p.Y);
            var (src, _) = PolySrc(p.osm_id ?? 0);
            placeCands.Add(new PlaceCand(
                p.place!, p.name, p.osm_id ?? 0, src, 0, pLat, pLon));
        }

        // ---- Pick PRIMARY (most specific, then nearest) -------------------
        var primary = placeCands
            .Where(c => PlaceSpecificity(c.Place) > 0)
            .OrderByDescending(c => PlaceSpecificity(c.Place))
            .ThenBy(c => c.DistanceM)
            .FirstOrDefault();

        // ---- Fill hierarchy slots -----------------------------------------
        var suburb = NearestOfKind(placeCands, SuburbTypes);
        var city = NearestOfKind(placeCands, CityTypes);
        var county = NearestOfKind(placeCands, CountyTypes);
        var state = (PlaceCand?)null;
        AdminCand? countryCand = null;

        var adminCands = admins
            .Select(a => new AdminCand(
                int.TryParse(a.admin_level, out var l) ? l : -1,
                a.name, a.osm_id ?? 0, a.X, a.Y))
            .Where(a => a.Level >= 0)
            .OrderBy(a => a.Level)
            .ToList();

        foreach (var a in adminCands)
        {
            switch (a.Level)
            {
                case 2: countryCand ??= a; break;
                case 3 or 4: state ??= AdminToPlace(a); break;
                case 5 or 6: county ??= AdminToPlace(a); break;
                case 7 or 8: city ??= AdminToPlace(a); break;
                case >= 9: suburb ??= AdminToPlace(a); break;
            }
        }

        // Fallback primary
        primary ??= placeCands.OrderBy(c => c.DistanceM).FirstOrDefault();
        if (primary is null && adminCands.Count > 0)
        {
            var a = adminCands.Last();
            primary = AdminToPlace(a);
        }
        if (primary is null) return null;

        // ---- Resolve multilingual names -----------------------------------
        var allParts = new List<PlaceCand?> { primary, suburb, city, county, state }
            .Where(p => p is not null).Select(p => p!).ToList();

        var feats = new List<(long osmId, Src src)>();
        feats.AddRange(allParts.Select(p =>
        {
            // Node ids stay positive; polygon osm_ids: negative=relation, positive=way
            if (p.Src == Src.Node) return (p.OsmId, Src.Node);
            var (s, id) = PolySrc(p.OsmId);
            return (id, s);
        }));
        if (countryCand is not null)
        {
            var (s, id) = PolySrc(countryCand.OsmId);
            feats.Add((id, s));
        }

        var tags = await LoadTagsAsync(feats, ct);

        string? Tr(PlaceCand? p)
        {
            if (p is null) return null;
            if (p.Src == Src.Node)
                return PickName(LookupTags(tags, p.OsmId, Src.Node), p.Name, language);
            var (s, id) = PolySrc(p.OsmId);
            return PickName(LookupTags(tags, id, s), p.Name, language);
        }
        string? TrAdmin(AdminCand? a)
        {
            if (a is null) return null;
            var (s, id) = PolySrc(a.OsmId);
            return PickName(LookupTags(tags, id, s), a.Name, language);
        }

        // ---- Postcode from primary's tags ---------------------------------
        string? postcode = null;
        {
            Dictionary<string, string>? ptags;
            if (primary.Src == Src.Node)
                ptags = LookupTags(tags, primary.OsmId, Src.Node);
            else
            {
                var (s, id) = PolySrc(primary.OsmId);
                ptags = LookupTags(tags, id, s);
            }
            if (ptags is not null)
                postcode = Get(ptags, "addr:postcode") ?? Get(ptags, "postal_code");
        }

        // ---- OsmType string (matching Nominatim: "node" / "way" / "relation")
        string osmType = primary.Src switch
        {
            Src.Node => "node",
            Src.Way => "way",
            Src.Rel => "relation",
            _ => "node"
        };

        return new OsmRegion(
            Name: Tr(primary),
            AddressType: primary.Place,
            PlaceRank: ToPlaceRank(primary.Place),
            OsmType: osmType,
            OsmId: primary.Src == Src.Node ? primary.OsmId
                                : (primary.Src == Src.Rel ? -primary.OsmId : primary.OsmId),
            AdminLevel: adminCands.FirstOrDefault(a => a.Name == primary.Name)?.Level ?? 0,
            Place: primary.Place,
            Country: TrAdmin(countryCand),
            State: Tr(state),
            County: Tr(county),
            City: Tr(city),
            Suburb: Tr(suburb),
            Postcode: postcode,
            // Canonical coordinates of the MATCHED OSM OBJECT (not the input)
            Latitude: primary.Lat,
            Longitude: primary.Lon,
            // Original input for reference / distance calculation
            InputLatitude: lat,
            InputLongitude: lon);
    }

    // =======================================================================
    //  2) GetNearbyAttractionsAsync  (relevance-ranked)
    // =======================================================================
    public async Task<IReadOnlyList<OsmAttraction>> GetNearbyAttractionsAsync(
        double lat, double lon, int radiusMeters = 2000, int maxResults = 20,
        string language = "en", CancellationToken ct = default)
    {
        if (lat < -90 || lat > 90 || lon < -180 || lon > 180)
            return Array.Empty<OsmAttraction>();

        var pt = ToMercator(lon, lat);
        double cosLat = Math.Max(Math.Cos(lat * Math.PI / 180.0), 0.01);
        double radiusMerc = radiusMeters / cosLat;

        var pointHits = await _ctx.planet_osm_points
            .Where(p => p.name != null
                     && (p.tourism != null || p.historic != null || p.leisure != null)
                     && (p.tourism == null || !NonAttractionTourism.Contains(p.tourism))
                     && p.way.IsWithinDistance(pt, radiusMerc))
            .Select(p => new RawHit
            {
                OsmId = p.osm_id,
                Name = p.name,
                Amenity = p.amenity,
                Tourism = p.tourism,
                Leisure = p.leisure,
                Shop = p.shop,
                HistoricTag = p.historic,
                IsPolygon = false,
                X = p.way.X,
                Y = p.way.Y
            })
            .ToListAsync(ct);

        var polygonHits = await _ctx.planet_osm_polygons
            .Where(p => p.name != null
                     && (p.tourism != null || p.historic != null || p.leisure != null)
                     && (p.tourism == null || !NonAttractionTourism.Contains(p.tourism))
                     && p.way.IsWithinDistance(pt, radiusMerc))
            .Select(p => new RawHit
            {
                OsmId = p.osm_id,
                Name = p.name,
                Amenity = p.amenity,
                Tourism = p.tourism,
                Leisure = p.leisure,
                Shop = p.shop,
                HistoricTag = p.historic,
                IsPolygon = true,
                X = p.way.Centroid.X,
                Y = p.way.Centroid.Y
            })
            .ToListAsync(ct);

        var all = pointHits.Concat(polygonHits).ToList();
        if (all.Count == 0) return Array.Empty<OsmAttraction>();

        // Batch-load translated names
        var feats = all.Select(h =>
        {
            if (!h.IsPolygon) return (h.OsmId ?? 0, Src.Node);
            var (s, id) = PolySrc(h.OsmId ?? 0); return (id, s);
        }).ToList();
        var tags = await LoadTagsAsync(feats, ct);

        return all
            .Select(h =>
            {
                var (hLat, hLon) = FromMercator(h.X, h.Y);
                int dist = (int)Math.Round(HaversineMeters(lat, lon, hLat, hLon));
                string? type = h.Tourism ?? h.HistoricTag ?? h.Leisure ?? h.Amenity ?? h.Shop;
                double score = CategoryWeight(type) / (1.0 + dist / 500.0);

                Src src; long lookupId;
                if (!h.IsPolygon) { src = Src.Node; lookupId = h.OsmId ?? 0; }
                else { (src, lookupId) = PolySrc(h.OsmId ?? 0); }
                string? name = PickName(LookupTags(tags, lookupId, src), h.Name, language);

                return new OsmAttraction(
                    OsmId: h.OsmId, Name: name,
                    Amenity: h.Amenity, Tourism: h.Tourism, Leisure: h.Leisure,
                    Shop: h.Shop, HistoricTag: h.HistoricTag,
                    Latitude: hLat, Longitude: hLon,
                    DistanceMeters: dist, Relevance: Math.Round(score, 4));
            })
            .Where(a => a.DistanceMeters <= radiusMeters)
            .OrderByDescending(a => a.Relevance)
            .ThenBy(a => a.DistanceMeters)
            .Take(maxResults)
            .ToList();
    }

    // -----------------------------------------------------------------------
    //  Category weights for relevance ranking
    // -----------------------------------------------------------------------
    private static double CategoryWeight(string? type) => type?.ToLowerInvariant() switch
    {
        "attraction" or "viewpoint" => 1.00,
        "museum" or "monument" or "castle" or "fort" => 0.95,
        "archaeological_site" or "ruins" or "theme_park" or "zoo" => 0.90,
        "aquarium" or "memorial" or "gallery" or "artwork" => 0.80,
        "nature_reserve" or "beach_resort" => 0.75,
        "park" or "garden" => 0.65,
        "place_of_worship" or "mosque" or "church" or "temple" => 0.60,
        "water_park" or "stadium" => 0.60,
        _ => 0.45,
    };

    // -----------------------------------------------------------------------
    //  Internal helpers
    // -----------------------------------------------------------------------
    private static PlaceCand? NearestOfKind(IEnumerable<PlaceCand> c, HashSet<string> k) =>
        c.Where(x => k.Contains(x.Place)).OrderBy(x => x.DistanceM).FirstOrDefault();

    private static PlaceCand AdminToPlace(AdminCand a)
    {
        var (src, _) = PolySrc(a.OsmId);
        var (aLat, aLon) = FromMercator(a.X, a.Y);
        string place = a.Level switch
        {
            2 => "country",
            <= 4 => "state",
            <= 6 => "county",
            <= 8 => "city",
            _ => "suburb"
        };
        return new PlaceCand(place, a.Name, a.OsmId, src, 0, aLat, aLon);
    }

    // ---- Middle-table tag loading (batched) --------------------------------
    private async Task<Dictionary<string, Dictionary<string, string>>> LoadTagsAsync(
        IReadOnlyCollection<(long osmId, Src src)> feats, CancellationToken ct)
    {
        var map = new Dictionary<string, Dictionary<string, string>>();
        var nodeIds = feats.Where(f => f.src == Src.Node).Select(f => f.osmId).Distinct().ToList();
        var wayIds = feats.Where(f => f.src == Src.Way).Select(f => f.osmId).Distinct().ToList();
        var relIds = feats.Where(f => f.src == Src.Rel).Select(f => f.osmId).Distinct().ToList();

        if (nodeIds.Count > 0)
            foreach (var x in await _ctx.planet_osm_nodes.Where(n => nodeIds.Contains(n.id))
                                  .Select(n => new { n.id, n.tags }).ToListAsync(ct))
                map[$"n{x.id}"] = ParseTags(x.tags);

        if (wayIds.Count > 0)
            foreach (var x in await _ctx.planet_osm_ways.Where(w => wayIds.Contains(w.id))
                                  .Select(w => new { w.id, w.tags }).ToListAsync(ct))
                map[$"w{x.id}"] = ParseTags(x.tags);

        if (relIds.Count > 0)
            foreach (var x in await _ctx.planet_osm_rels.Where(r => relIds.Contains(r.id))
                                  .Select(r => new { r.id, r.tags }).ToListAsync(ct))
                map[$"r{x.id}"] = ParseTags(x.tags);

        return map;
    }

    private static Dictionary<string, string>? LookupTags(
        Dictionary<string, Dictionary<string, string>> map, long id, Src src)
    {
        string key = src switch { Src.Node => $"n{id}", Src.Way => $"w{id}", _ => $"r{id}" };
        return map.TryGetValue(key, out var t) ? t : null;
    }

    private static Dictionary<string, string> ParseTags(string? json)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json)) return d;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
                foreach (var p in doc.RootElement.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.String)
                        d[p.Name] = p.Value.GetString() ?? "";
        }
        catch { }
        return d;
    }

    private static string? Get(Dictionary<string, string> t, string k) =>
        t.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    // name:{lang} → int_name → name:en → name → any name:*
    private static string? PickName(Dictionary<string, string>? tags, string? baseName, string lang)
    {
        if (tags is null || tags.Count == 0) return baseName;
        foreach (var k in new[] { $"name:{lang}", "int_name", "name:en", "name" })
            if (Get(tags, k) is { } v) return v;
        var any = tags.Keys.FirstOrDefault(k => k.StartsWith("name:", StringComparison.OrdinalIgnoreCase));
        if (any is not null && Get(tags, any) is { } a) return a;
        return baseName;
    }

    // osm2pgsql polygon osm_id: positive = way, negative = relation
    private static (Src src, long id) PolySrc(long osmId) =>
        osmId < 0 ? (Src.Rel, -osmId) : (Src.Way, osmId);

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

    private enum Src { Node, Way, Rel }
    private sealed record PlaceCand(string Place, string? Name, long OsmId, Src Src,
        double DistanceM, double Lat, double Lon);
    private sealed record AdminCand(int Level, string? Name, long OsmId, double X, double Y);

    
}
