using GIsDataWorker;
using GIsDataWorker.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

public class MapUpdateWorker : BackgroundService
{
    // Removed the shared HTTP client instance and factory method

    private readonly ILogger<MapUpdateWorker> _logger;
    private readonly MapSettings _mapSettings;
    private readonly PostgresSettings _postgresSettings;
    private readonly HttpClient _http;
    private readonly OsmImportState _osmState;
    private string _osm2pgsqlPath = string.Empty;
    private string? _psqlPath;

    public MapUpdateWorker(
        ILogger<MapUpdateWorker> logger,
        IOptions<MapSettings> mapSettings,
        IOptions<PostgresSettings> postgresSettings,
        IHttpClientFactory httpClientFactory,
        OsmImportState osmState)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _mapSettings = mapSettings?.Value ?? throw new ArgumentNullException(nameof(mapSettings));
        _postgresSettings = postgresSettings?.Value ?? throw new ArgumentNullException(nameof(postgresSettings));
        _http = httpClientFactory.CreateClient("GIsWorkerClient");
        _osmState = osmState;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Main loop
    // ─────────────────────────────────────────────────────────────────────────
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _psqlPath = FindPsqlOnPath()
            ?? throw new Exception(
                "psql not found. Add PostgreSQL bin to PATH, or set the " +
                "PSQL_PATH environment variable to the full path of psql.exe.");
        _logger.LogInformation("psql found at: {Path}", _psqlPath);

        // ── Retry loop for osm2pgsql install (network may be temporarily unavailable) ──
        string osm2pgsqlFolder = _mapSettings.Osm2PgsqlFolder
            ?? Path.Combine(AppContext.BaseDirectory, "tools");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EnsureOsm2PgSqlExists(osm2pgsqlFolder);
                _logger.LogInformation("osm2pgsql is ready at: {Path}", _osm2pgsqlPath);
                break; // success — exit retry loop
            }
            catch (HttpRequestException ex) when (
                ex.InnerException is System.Net.Sockets.SocketException)
            {
                _logger.LogWarning(
                    "DNS/network error while downloading osm2pgsql. " +
                    "Retrying in 2 minutes... ({Msg})", ex.Message);
                await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize osm2pgsql. Retrying in 2 minutes...");
                await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
            }
        }

        if (stoppingToken.IsCancellationRequested) return;

        // ── Main processing loop ──
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                bool dbExists = await IsDatabaseExists();

                if (!dbExists)
                {
                    _logger.LogInformation("OSM data not imported. Running full import...");
                    await RunFullImport();
                }
                else
                {
                    // ✅ Signal Worker that OSM tables are ready
                    if (!_osmState.IsReady)
                    {
                        _logger.LogInformation("OSM data confirmed. Signaling Worker to start.");
                        _osmState.SetReady();
                    }

                    _logger.LogInformation("OSM data exists. Checking for updates...");
                    await ApplyDiffUpdate();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in processing loop.");
            }

            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Full import
    // ─────────────────────────────────────────────────────────────────────────
    private async Task RunFullImport()
    {
        string baseUrl = _mapSettings.BaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new InvalidOperationException("MapSettings:BaseUrl missing from appsettings.json.");

        string downloadUrl = await GetLatestUrl(baseUrl, @"href=""([^""]+latest\.osm\.pbf)""");
        if (string.IsNullOrEmpty(downloadUrl))
            throw new Exception("Could not find full map download URL.");

        string tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pbf");
        try
        {
            await DownloadFile(downloadUrl, tempPath);

            var fi = new FileInfo(tempPath);
            if (!fi.Exists || fi.Length == 0)
                throw new Exception($"Download produced an empty or missing file: {tempPath}");

            _logger.LogInformation("Starting full osm2pgsql import (--create)...");
            await ExecuteOsm2PgSql(tempPath, "--create --slim --cache 1000");
            _logger.LogInformation("Full import completed successfully.");
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Diff update
    // ─────────────────────────────────────────────────────────────────────────
    private static string EnsureTrailingSlash(string url) =>
    url.EndsWith("/") ? url : url + "/";

    private async Task ApplyDiffUpdate()
    {
        string updatesUrl = EnsureTrailingSlash(_mapSettings.UpdatesUrl);

        // تأكد من أن الرابط يبدأ بـ https
        if (!updatesUrl.StartsWith("http"))
        {
            _logger.LogError("Invalid URL format: {Url}", updatesUrl);
            return;
        }

        try
        {
            // اختبار الاتصال بالخادم أولاً
            using var client = new HttpClient();
            var response = await _http.GetAsync(updatesUrl);
            response.EnsureSuccessStatusCode();

            string diffUrl = await GetLatestUrl(updatesUrl, @"href=""([^""]+osc\.gz)""");

            if (string.IsNullOrEmpty(diffUrl))
            {
                _logger.LogInformation("No diff updates available.");
                return;
            }

            // كمل باقي منطق التحميل هنا...
        }
        catch (HttpRequestException ex) when (ex.InnerException is System.Net.Sockets.SocketException)
        {
            _logger.LogError("DNS Error: Cannot resolve download.geofabrik.de. Check your internet/DNS settings.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during update process.");
        }
    }
    // ─────────────────────────────────────────────────────────────────────────
    //  Download helper
    // ─────────────────────────────────────────────────────────────────────────
    private async Task DownloadFile(string url, string dest)
    {
        _logger.LogInformation("Downloading {Url} → {Dest}", url, dest);

    using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        long totalBytes = response.Content.Headers.ContentLength ?? -1L;
        if (totalBytes > 0 && !HasEnoughSpace(dest, totalBytes))
            throw new Exception($"Disk space insufficient. Required: {totalBytes / 1024 / 1024} MB.");

        await using var httpStream = await response.Content.ReadAsStreamAsync();
        await using var fileStream = new FileStream(
            dest, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 81920, useAsync: true);

        var buffer = new byte[81920];
        long totalRead = 0;
        int read;
        int lastReportedPct = -1;

        while ((read = await httpStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            await fileStream.WriteAsync(buffer, 0, read);
            totalRead += read;

            if (totalBytes > 0)
            {
                int pct = (int)(totalRead * 100 / totalBytes);
                if (pct / 5 != lastReportedPct / 5)
                {
                    lastReportedPct = pct;
                    _logger.LogInformation("Download progress: {Pct}%", pct);
                }
            }
        }

        await fileStream.FlushAsync();

        long written = new FileInfo(dest).Length;
        if (written == 0)
            throw new Exception($"Download wrote 0 bytes to {dest}.");
        if (totalBytes > 0 && written != totalBytes)
            _logger.LogWarning("Size mismatch: expected {Expected}, got {Actual}", totalBytes, written);

        _logger.LogInformation("Download complete. Size: {MB:F1} MB", written / 1_048_576.0);
    }

    private static bool HasEnoughSpace(string path, long requiredBytes)
    {
        var root = Path.GetPathRoot(path)!;
        var drive = new DriveInfo(root);
        return drive.AvailableFreeSpace > requiredBytes * 1.2;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  URL scraping
    // ─────────────────────────────────────────────────────────────────────────
    private async Task<string> GetLatestUrl(string pageUrl, string pattern)
    {
        try
        {
            string html = await _http.GetStringAsync(pageUrl);
            var matches = Regex.Matches(html, pattern);
            if (matches.Count == 0) return string.Empty;
            return new Uri(new Uri(pageUrl), matches[^1].Groups[1].Value).ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching URL: {Url}", pageUrl);
            return string.Empty;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  osm2pgsql execution
    // ─────────────────────────────────────────────────────────────────────────
    private async Task ExecuteOsm2PgSql(string filePath, string extraArgs)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Input file not found: {filePath}");

        string styleArg = ResolveStyleArg();

        string args = $"{extraArgs} {styleArg} " +
                      $"-H {_postgresSettings.Host} -P {_postgresSettings.Port} " +
                      $"-d {_postgresSettings.Database} -U {_postgresSettings.Username} " +
                      $"\"{filePath}\"";

        _logger.LogInformation("Running osm2pgsql with args: {Args}", args);

        // ✅ Fixed: was _settings.Password
        string output = await ExecuteCommand(_osm2pgsqlPath, args, _postgresSettings.Password);

        if (!string.IsNullOrWhiteSpace(output))
            _logger.LogInformation("osm2pgsql output: {Output}", output);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Style resolver
    // ─────────────────────────────────────────────────────────────────────────
    private string ResolveStyleArg()
    {
        // ✅ Fixed: was Osm2PgsqlFolder (a folder path, not a style file)
        string configuredStyle = _mapSettings.StyleFile ?? "";
        if (!string.IsNullOrEmpty(configuredStyle))
        {
            if (!File.Exists(configuredStyle))
                _logger.LogWarning("Configured style file not found: {Path}. Falling back.", configuredStyle);
            else
                return BuildStyleArg(configuredStyle);
        }

        string exeDir = Path.GetDirectoryName(_osm2pgsqlPath) ?? "";

        string? luaFile = Directory
            .GetFiles(exeDir, "*.lua", SearchOption.TopDirectoryOnly)
            .FirstOrDefault();
        if (luaFile is not null)
        {
            _logger.LogInformation("Using Lua style: {File}", luaFile);
            return BuildStyleArg(luaFile);
        }

        string? styleFile = Directory
            .GetFiles(exeDir, "*.style", SearchOption.TopDirectoryOnly)
            .FirstOrDefault();
        if (styleFile is not null)
        {
            _logger.LogInformation("Using legacy style: {File}", styleFile);
            return BuildStyleArg(styleFile);
        }

        _logger.LogWarning("No style file found. Using built-in pgsimple output.");
        return "--output=pgsimple";
    }

    private static string BuildStyleArg(string stylePath) =>
        stylePath.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)
            ? $"--output=flex -S \"{stylePath}\""
            : $"-S \"{stylePath}\"";

    private async Task<bool> IsDatabaseExists()
    {
        try
        {
            string psql = _psqlPath!;
            string safeDbName = _postgresSettings.Database.Replace("'", "''");

            // Step 1: check the database itself exists
            string dbExistsArgs =
                $"-U {_postgresSettings.Username} -h {_postgresSettings.Host} " +
                $"-p {_postgresSettings.Port} -d postgres -tAc " +
                $"\"SELECT 1 FROM pg_database WHERE datname='{safeDbName}'\"";

            string dbResult = await ExecuteCommand(psql, dbExistsArgs, _postgresSettings.Password);
            if (dbResult.Trim() != "1")
            {
                _logger.LogInformation("Database '{DB}' does not exist.", _postgresSettings.Database);
                return false;
            }

            // Step 2: check if planet_osm_point TABLE exists (safe — no error if missing)
            string tableExistsArgs =
                $"-U {_postgresSettings.Username} -h {_postgresSettings.Host} " +
                $"-p {_postgresSettings.Port} -d {_postgresSettings.Database} -tAc " +
                "\"SELECT COUNT(*) FROM information_schema.tables " +
                "WHERE table_schema='public' AND table_name='planet_osm_point'\"";

            string tableResult = await ExecuteCommand(psql, tableExistsArgs, _postgresSettings.Password);
            if (tableResult.Trim() != "1")
            {
                _logger.LogInformation(
                    "Database '{DB}' exists but OSM tables not imported yet.",
                    _postgresSettings.Database);
                return false;
            }

            // Step 3: table exists — check it has data
            string countArgs =
                $"-U {_postgresSettings.Username} -h {_postgresSettings.Host} " +
                $"-p {_postgresSettings.Port} -d {_postgresSettings.Database} -tAc " +
                "\"SELECT COUNT(*) FROM planet_osm_point\"";

            string countResult = await ExecuteCommand(psql, countArgs, _postgresSettings.Password);
            bool hasData = long.TryParse(countResult.Trim(), out var rows) && rows > 0;

            _logger.LogInformation(
                hasData
                    ? "Database '{DB}' has OSM data ({Rows} rows in planet_osm_point)."
                    : "Database '{DB}' exists but planet_osm_point is empty.",
                _postgresSettings.Database, rows);

            return hasData;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking database or OSM tables.");
            return false;
        }
    }
    // ─────────────────────────────────────────────────────────────────────────
    //  Process runner
    // ─────────────────────────────────────────────────────────────────────────
    private async Task<string> ExecuteCommand(string fileName, string args, string pgPassword = "")
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            }
        };

        process.StartInfo.EnvironmentVariables["PGPASSWORD"] = pgPassword;
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        string output = await stdoutTask;
        string error = await stderrTask;

        if (process.ExitCode != 0)
        {
            _logger.LogError("Process stderr: {Err}", error);
            throw new Exception(
                $"{Path.GetFileName(fileName)} failed (exit {process.ExitCode}).\n{error}");
        }

        if (!string.IsNullOrWhiteSpace(error))
            _logger.LogInformation("{Tool} stderr: {Err}", Path.GetFileName(fileName), error);

        return output;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  osm2pgsql installation
    // ─────────────────────────────────────────────────────────────────────────
    private async Task EnsureOsm2PgSqlExists(string targetFolder)
    {
        bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        string exeName = isWindows ? "osm2pgsql.exe" : "osm2pgsql";

        string? existingExe = FindExecutable(targetFolder, exeName);
        if (existingExe != null)
        {
            _osm2pgsqlPath = existingExe;
            _logger.LogInformation("osm2pgsql found at: {Path}", _osm2pgsqlPath);
            return;
        }

        _logger.LogInformation("osm2pgsql not found. Installing for {OS}...",
            isWindows ? "Windows" : "Linux");

        if (isWindows)
            await EnsureOsm2PgSqlWindows(targetFolder, exeName);
        else
            await EnsureOsm2PgSqlLinux(exeName);
    }

    private string? FindExecutable(string targetFolder, string exeName)
    {
        if (Directory.Exists(targetFolder))
        {
            string? found = Directory
                .GetFiles(targetFolder, exeName, SearchOption.AllDirectories)
                .FirstOrDefault();
            if (found != null) return found;
        }
        return FindOnPath(exeName);
    }

    private async Task EnsureOsm2PgSqlWindows(string targetFolder, string exeName)
    {
        string folder = Path.Combine(targetFolder, "osm2pgsql_bin");
        string downloadUrl = await ScrapeWindowsDownloadUrl();

        _logger.LogInformation("Downloading osm2pgsql from: {Url}", downloadUrl);

        string archivePath = Path.Combine(Path.GetTempPath(), $"osm2pgsql_{Guid.NewGuid()}.zip");
        Directory.CreateDirectory(folder);

        try
        {
            // ✅ uses _http internally + streams to disk (no RAM spike)
            await DownloadFile(downloadUrl, archivePath);

            _logger.LogInformation("Extracting osm2pgsql to: {Folder}", folder);
            ZipFile.ExtractToDirectory(archivePath, folder, overwriteFiles: true);

            _osm2pgsqlPath = Directory
                .GetFiles(folder, exeName, SearchOption.AllDirectories)
                .FirstOrDefault()
                ?? throw new Exception($"osm2pgsql.exe not found after extraction in: {folder}");

            _logger.LogInformation("osm2pgsql installed at: {Path}", _osm2pgsqlPath);
        }
        finally
        {
            if (File.Exists(archivePath)) File.Delete(archivePath);
        }
    }

    private async Task EnsureOsm2PgSqlLinux(string exeName)
    {
        string? onPath = FindOnPath(exeName);
        if (onPath is not null)
        {
            _osm2pgsqlPath = onPath;
            _logger.LogInformation("osm2pgsql found on PATH: {Path}", _osm2pgsqlPath);
            return;
        }

        _logger.LogInformation("Installing osm2pgsql via apt-get...");
        await ExecuteCommand("apt-get", "update -qq");
        await ExecuteCommand("apt-get", "install -y osm2pgsql");

        onPath = FindOnPath(exeName);
        if (onPath is null)
            throw new Exception("osm2pgsql not found after apt-get install. Install manually.");

        _osm2pgsqlPath = onPath;
        _logger.LogInformation("osm2pgsql installed at: {Path}", _osm2pgsqlPath);
    }

    private async Task<string> ScrapeWindowsDownloadUrl()
    {
        // ✅ Fixed: was _settings.Osm2PgsqlUrl
        string listingUrl = _mapSettings.Osm2PgsqlUrl;
        if (string.IsNullOrWhiteSpace(listingUrl))
            throw new InvalidOperationException("MapSettings:Osm2PgsqlUrl missing from appsettings.json.");

        _logger.LogInformation("Scraping osm2pgsql download page: {Url}", listingUrl);

        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        string html = await client.GetStringAsync(listingUrl);
        var matches = Regex.Matches(html, @"(osm2pgsql-[\d.]+-x64\.zip)");

        if (matches.Count == 0)
            throw new Exception($"No download links found at: {listingUrl}");

        string newest = matches
            .Cast<Match>()
            .Select(m => m.Groups[1].Value)
            .OrderByDescending(name =>
            {
                var v = Regex.Match(name, @"(\d+\.\d+\.\d+)");
                return v.Success ? Version.Parse(v.Groups[1].Value) : new Version(0, 0);
            })
            .First();

        return new Uri(new Uri(listingUrl), newest).ToString();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  psql discovery
    // ─────────────────────────────────────────────────────────────────────────
    private static string? FindPsqlOnPath()
    {
        string exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "psql.exe" : "psql";

        string? envOverride = Environment.GetEnvironmentVariable("PSQL_PATH");
        if (!string.IsNullOrEmpty(envOverride) && File.Exists(envOverride))
            return envOverride;

        string? onPath = FindOnPath(exeName);
        if (onPath != null) return onPath;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var systemDrive = Environment.GetEnvironmentVariable("SystemDrive") !;
var searchRoots = new[]
{
    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
    Path.Combine(systemDrive, "PostgreSQL"),
};

            foreach (string baseDir in searchRoots.Where(d => !string.IsNullOrEmpty(d)))
            {
                string pgRoot = Path.Combine(baseDir, "PostgreSQL");
                if (!Directory.Exists(pgRoot)) pgRoot = baseDir;
                if (!Directory.Exists(pgRoot)) continue;

                string? found = Directory
                    .GetDirectories(pgRoot)
                    .OrderByDescending(dir =>
                    {
                        double.TryParse(Path.GetFileName(dir),
                            System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out double v);
                        return v;
                    })
                    .Select(dir => Path.Combine(dir, "bin", exeName))
                    .FirstOrDefault(File.Exists);

                if (found != null) return found;
            }

            string? regPath = GetPsqlFromRegistry(exeName);
            if (regPath != null) return regPath;
        }
        else
        {
            var linuxCandidates = new[]
            {
                "/usr/bin/psql",
                "/usr/local/bin/psql",
                "/usr/lib/postgresql/bin/psql",
            };

            var versionedPaths = Directory.Exists("/usr/lib/postgresql")
                ? Directory.GetDirectories("/usr/lib/postgresql")
                           .OrderByDescending(d => d)
                           .Select(d => Path.Combine(d, "bin", "psql"))
                : Enumerable.Empty<string>();

            string? found = linuxCandidates.Concat(versionedPaths).FirstOrDefault(File.Exists);
            if (found != null) return found;
        }

        return null;
    }

    private static string? GetPsqlFromRegistry(string exeName)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return null;
        try
        {
            using var hklm = Microsoft.Win32.Registry.LocalMachine;
            using var pgKey = hklm.OpenSubKey(@"SOFTWARE\PostgreSQL\Installations");
            if (pgKey == null) return null;

            return pgKey.GetSubKeyNames()
                .OrderByDescending(name =>
                {
                    var m = Regex.Match(name, @"(\d+)$");
                    return m.Success ? int.Parse(m.Groups[1].Value) : 0;
                })
                .Select(name =>
                {
                    using var sub = pgKey.OpenSubKey(name);
                    string? baseDir = sub?.GetValue("Base Directory") as string;
                    return baseDir is null ? null : Path.Combine(baseDir, "bin", exeName);
                })
                .FirstOrDefault(p => p != null && File.Exists(p));
        }
        catch { return null; }
    }

    private static string? FindOnPath(string exeName)
    {
        string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        return pathEnv
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir, exeName))
            .FirstOrDefault(File.Exists);
    }
}