namespace GIsDataWorker.DTos;

public record MongoLocationDto
{
    public string Id { get; init; } = string.Empty;
    public string CollectionName { get; init; } = string.Empty;
    public string? Name { get; init; }
    public double Latitude { get; init; }
    public double Longitude { get; init; }
}
