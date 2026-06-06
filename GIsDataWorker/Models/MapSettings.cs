namespace GIsDataWorker.Models;

 

 
public class MongoSettings
{
    public string Mongo { get; set; } = string.Empty;
    public string MongoDB { get; set; } = string.Empty;
}
 
    public class MapSettings
    {
        /// <summary>
        /// osm2pgsql.org Windows download listing page.
        /// Used to resolve the latest versioned ZIP filename.
        /// Example: "https://osm2pgsql.org/download/windows/"
        /// </summary>
        public string Osm2PgsqlUrl { get; set; } = string.Empty;

        /// <summary>
        /// Geofabrik region page for full .osm.pbf download.
        /// Example: "https://download.geofabrik.de/africa/egypt.html"
        /// </summary>
        public string BaseUrl { get; set; } = string.Empty;

        /// <summary>
        /// Geofabrik updates directory for diff (.osc.gz) downloads.
        /// Example: "https://download.geofabrik.de/africa/egypt-updates/"
        /// </summary>
        public string UpdatesUrl { get; set; } = string.Empty;

        /// <summary>
        /// Folder where osm2pgsql binary is installed/cached.
        /// Defaults to {AppBaseDir}/tools if null.
        /// </summary>
        public string? Osm2PgsqlFolder { get; set; }

        /// <summary>
        /// Path to a .lua (flex) or .style (legacy) file for osm2pgsql.
        /// If null, auto-detects from the osm2pgsql folder or uses pgsimple output.
        /// </summary>
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