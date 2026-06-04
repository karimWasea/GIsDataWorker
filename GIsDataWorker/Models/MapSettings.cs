namespace GIsDataWorker.Models;

public class MapSettings
{
    public const string SectionName = "MapSettings";

    public string Osm2PgsqlUrl { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string UpdatesUrl { get; set; } = string.Empty;
    public string? Osm2PgsqlFolder { get; set; }
}