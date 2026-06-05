namespace GIsDataWorker.DTOs;

public sealed class AttractionResultDto
{
    public long? OsmId { get; init; }
    public string? Name { get; init; }
    public string? Amenity { get; init; }
    public string? Tourism { get; init; }
    public string? Shop { get; init; }
    public string? Leisure { get; init; }
    public string? HistoricTag { get; init; }
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public double DistanceMeters { get; init; }

    /// <summary>
    /// Returns the most descriptive type label available for this attraction.
    /// </summary>
    public string TypeLabel =>
        Amenity ?? Tourism ?? Leisure ?? Shop ?? HistoricTag ?? "unknown";
}
