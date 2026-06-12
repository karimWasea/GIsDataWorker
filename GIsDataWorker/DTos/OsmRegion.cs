using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GIsDataWorker.DTos
{

    // ===========================================================================
    //  DTOs
    // ===========================================================================

    /// <summary>
    /// Reverse-geocoding result shaped to mirror Nominatim's response.
    /// IMPORTANT: Latitude/Longitude are the matched OSM object's canonical
    /// coordinates — NOT the input coordinates. Use InputLatitude/InputLongitude
    /// for the original query point.
    /// </summary>
    public sealed record OsmRegion(
        // ── Primary matched place ───────────────────────────────────────
        string? Name,           // e.g. "Nazlet al Batran"
        string? AddressType,    // e.g. "suburb", "town", "city"
        int PlaceRank,          // Nominatim place_rank: suburb=19, town=18, city=16
        string? OsmType,        // "node" / "way" / "relation"
        long? OsmId,            // OSM id of the matched object
        int AdminLevel,         // admin_level if admin boundary, else 0
        string? Place,          // raw OSM place tag value

        // ── Address hierarchy ──────────────────────────────────────────
        string? Country,
        string? State,
        string? County,
        string? City,
        string? Suburb,
        string? Postcode,

        // ── Coordinates of the MATCHED object (like Nominatim) ─────────
        double Latitude,        // Canonical lat of matched OSM object
        double Longitude,       // Canonical lon of matched OSM object

        // ── Original input coordinates (for reference / distance calc) ─
        double InputLatitude,
        double InputLongitude)
    {
        /// <summary>
        /// Distance in metres between the input point and the matched object.
        /// Higher PlaceRank = smaller drift.
        /// </summary>
        public double DriftMeters => Math.Round(
            6_371_000.0 * 2.0 * Math.Asin(Math.Sqrt(
                Math.Pow(Math.Sin((Latitude - InputLatitude) * Math.PI / 360.0), 2) +
                Math.Cos(InputLatitude * Math.PI / 180.0) *
                Math.Cos(Latitude * Math.PI / 180.0) *
                Math.Pow(Math.Sin((Longitude - InputLongitude) * Math.PI / 360.0), 2))), 1);

        /// <summary>Nominatim-style display: "Nazlet al Batran, Giza, 12561, Egypt"</summary>
        public string DisplayName
        {
            get
            {
                var parts = new List<string?>();
                // primary name (avoid duplicating suburb if primary IS the suburb)
                parts.Add(Name ?? Suburb);
                if (City is not null && City != Name) parts.Add(City);
                if (State is not null && State != City && State != Name) parts.Add(State);
                if (Postcode is not null) parts.Add(Postcode);
                if (Country is not null) parts.Add(Country);
                return string.Join(", ", parts
                    .Where(s => !string.IsNullOrEmpty(s))
                    .Distinct());
            }
        }
    }

    /// <summary>A nearby attraction with a relevance score (higher = more important).</summary>
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
        int DistanceMeters,
        double Relevance);
}
