namespace GIsDataWorker.Models;

public class MapSettings
{
    public string Osm2PgsqlUrl { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string UpdatesUrl { get; set; } = string.Empty;
    public string? Osm2PgsqlFolder { get; set; }
    public string? StyleFile { get; set; }   
}

public class PostgresSettings
{
    public string Host { get; set; } = "localhost";
    public string Port { get; set; } = "5432";
    public string Database { get; set; } = string.Empty;
    public string Username { get; set; } = "postgres";
    public string Password { get; set; } = string.Empty;
}