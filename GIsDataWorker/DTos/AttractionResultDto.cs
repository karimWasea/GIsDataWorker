namespace GIsDataWorker.DTos
{
    /// <summary>
    /// Represents a single OSM point-of-interest returned by GetNearbyAttractionsAsync.
    /// Coordinates are always WGS84 degrees (latitude, longitude).
    /// </summary>
    public class AttractionResultDto
    {
        public long? OsmId { get; set; }
        public string? Name { get; set; }

        // OSM tag columns — at least one is non-null per result
        public string? Amenity { get; set; }
        public string? Tourism { get; set; }
        public string? Shop { get; set; }
        public string? Leisure { get; set; }
        public string? HistoricTag { get; set; }

        /// <summary>WGS84 latitude degrees (−90 … +90).</summary>
        public double Latitude { get; set; }

        /// <summary>WGS84 longitude degrees (−180 … +180).</summary>
        public double Longitude { get; set; }

        /// <summary>Straight-line distance in metres from the query origin.</summary>
        public double DistanceMeters { get; set; }

        /// <summary>
        /// Best human-readable type label: prefers tourism > amenity > leisure > shop > historic.
        /// </summary>
        public string TypeLabel =>
            Tourism ?? Amenity ?? Leisure ?? Shop ?? HistoricTag ?? "unknown";
    }
}