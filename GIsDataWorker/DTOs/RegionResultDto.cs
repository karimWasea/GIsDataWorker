namespace GIsDataWorker.DTOs;

public sealed class RegionResultDto
{
    public required string Name { get; init; }
    public string? AdminLevel { get; init; }
    public string? Boundary { get; init; }
    public string? Place { get; init; }
    public string? OsmId { get; init; }
    public string? Suburb { get; init; }
}
