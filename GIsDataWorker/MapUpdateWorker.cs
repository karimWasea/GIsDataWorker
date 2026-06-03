using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using GIsDataWorker.Models;
using Microsoft.EntityFrameworkCore;

public class MapUpdateWorker : BackgroundService
{
    private readonly ILogger<MapUpdateWorker> _logger;
    private readonly IConfiguration _config;
    private readonly IConfigurationSection _mapSettings;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SemaphoreSlim _importLock = new(1, 1);
    private string _osm2pgsqlPath = string.Empty;

    private IConfigurationSection _settings => _mapSettings;

    public MapUpdateWorker(
        ILogger<MapUpdateWorker> logger,
        IConfiguration config,
        IServiceScopeFactory scopeFactory)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _config = config;
        _mapSettings = _config.GetSection("MapSettings");
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            string osm2pgsqlFolder = _settings["Osm2PgsqlFolder"] ?? Path.Combine(AppContext.BaseDirectory, "tools");
            await EnsureOsm2PgSqlExists(osm2pgsqlFolder);
            _logger.LogInformation("osm2pgsql is ready at: {Path}", _osm2pgsqlPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize osm2pgsql. The worker will not function properly.");
            throw;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            await _importLock.WaitAsync(stoppingToken);
            try
            {
                if (await IsDatabaseReady())
                {
                    bool isInitialized = await IsOsm2PgSqlInitialized();

                    if (!isInitialized)
                    {
                        _logger.LogInformation("Database not initialized. Starting full import...");
                        await RunFullImport();
                    }
                    else
                    {
                        _logger.LogInformation("Database already initialized. Checking for updates...");
                        await ApplyDiffUpdate();
                    }
                }
                else
                {
                    _logger.LogError("Database is not ready. Will retry in 15 minutes.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in processing loop.");
            }
            finally
            {
                _importLock.Release();
            }

            await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken);
        }
    }

    private async Task<bool> IsDatabaseReady()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            return await db.Database.CanConnectAsync();
        }
        catch { return false; }
    }

    private async Task<bool> IsOsm2PgSqlInitialized()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var result = await db.Database.SqlQueryRaw<int>(
                "SELECT COUNT(*) FROM information_schema.tables " +
                "WHERE table_name = 'planet_osm_point' AND table_schema = 'public'"
            ).ToListAsync();

            return result.FirstOrDefault() > 0;
        }
        catch { return false; }
    }

    private async Task RunFullImport()
    {
        string baseUrl = _settings["BaseUrl"]!;
        string downloadUrl = await GetLatestUrl(baseUrl, @"href=""([^""]+latest\.osm\.pbf)""");
        if (string.IsNullOrEmpty(downloadUrl))
            throw new Exception("Could not find full map URL.");

        string tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pbf");
        try
        {
            await DownloadFile(downloadUrl, tempPath);

            var fileInfo = new FileInfo(tempPath);
            if (!fileInfo.Exists || fileInfo.Length == 0)
                throw new Exception($"Download produced an empty or missing file at: {tempPath}");

            _logger.LogInformation("Starting full osm2pgsql import with --create mode...");
            await ExecuteOsm2PgSql(tempPath, "--create --slim --cache 1000");
            _logger.LogInformation("Full import completed successfully.");
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    private async Task ApplyDiffUpdate()
    {
        string updatesUrl = _settings["UpdatesUrl"]!;
        string diffUrl = await GetLatestUrl(updatesUrl, @"href=""([^""]+osc\.gz)""");
        if (string.IsNullOrEmpty(diffUrl))
        {
            _logger.LogInformation("No updates available at this time.");
            return;
        }

        string tempDiffPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".osc.gz");
        try
        {
            await DownloadFile(diffUrl, tempDiffPath);
            _logger.LogInformation("Starting differential update with --append mode...");
            await ExecuteOsm2PgSql(tempDiffPath, "--append --slim --cache 1000");
            _logger.LogInformation("Differential update completed successfully.");
        }
        finally
        {
            if (File.Exists(tempDiffPath)) File.Delete(tempDiffPath);
        }
    }

    private async Task DownloadFile(string url, string dest)
    {
        _logger.LogInformation("Downloading {Url} → {Dest}", url, dest);

        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
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
                    _logger.LogInformation("Download Progress: {Pct}%", pct);
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

    private bool HasEnoughSpace(string path, long requiredBytes)
    {
        var root = Path.GetPathRoot(path)!;
        var drive = new DriveInfo(root);
        return drive.AvailableFreeSpace > requiredBytes * 1.2;
    }

    private async Task<string> GetLatestUrl(string pageUrl, string pattern)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            string html = await client.GetStringAsync(pageUrl);
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

    private async Task ExecuteOsm2PgSql(string tempPath, string extraArgs)
    {
        if (!File.Exists(tempPath))
            throw new FileNotFoundException($"File not found before import: {tempPath}");

        string connStr = _config.GetConnectionString("DefaultConnection")!;
        string host = GetConnPart(connStr, "Host") ?? "localhost";
        string port = GetConnPart(connStr, "Port") ?? "5432";
        string database = GetConnPart(connStr, "Database") ?? throw new Exception("Database not found in ConnectionString");
        string user = GetConnPart(connStr, "Username") ?? "postgres";
        string password = GetConnPart(connStr, "Password") ?? "";

        string styleArg = ResolveStyleArg();

        string args = $"{extraArgs} {styleArg} -H {host} -P {port} -d {database} -U {user} \"{tempPath}\"";

        _logger.LogInformation("Running osm2pgsql on database: {Database}", database);
        _logger.LogInformation("Running osm2pgsql with args: {Args}", args);

        await ExecuteCommand(_osm2pgsqlPath, args, password);
    }

    // ✅ يدور على default.style الموجود جنب الـ exe - بدون lua
    private string ResolveStyleArg()
    {
        string exeDir = Path.GetDirectoryName(_osm2pgsqlPath)!;

        // أولاً: دور على default.style
        string? styleFile = Directory
            .GetFiles(exeDir, "*.style", SearchOption.AllDirectories)
            .FirstOrDefault(f => Path.GetFileName(f) == "default.style");

        // ثانياً: لو مش لاقي default.style خد أي .style موجود
        styleFile ??= Directory
            .GetFiles(exeDir, "*.style", SearchOption.AllDirectories)
            .FirstOrDefault();

        if (styleFile is not null)
        {
            _logger.LogInformation("Using style file: {File}", styleFile);
            return $"-S \"{styleFile}\"";
        }

        throw new Exception(
            $"No .style file found next to osm2pgsql in: {exeDir}. " +
            "Please ensure default.style exists in the osm2pgsql folder.");
    }

    private static string? GetConnPart(string connStr, string key) =>
        connStr.Split(';')
               .Select(p => p.Split('=', 2))
               .FirstOrDefault(kv => kv.Length == 2 &&
                    kv[0].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))?[1].Trim();

    private async Task<string> ExecuteCommand(string fileName, string args, string pgPassword = "")
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        process.StartInfo.EnvironmentVariables["PGPASSWORD"] = pgPassword;
        process.Start();

        string output = await process.StandardOutput.ReadToEndAsync();
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
            throw new Exception(
                $"Process {Path.GetFileName(fileName)} failed (Exit Code: {process.ExitCode}).\nError: {error}");

        return output;
    }

    private async Task EnsureOsm2PgSqlExists(string targetFolder)
    {
        bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        string exeName = isWindows ? "osm2pgsql.exe" : "osm2pgsql";

        if (isWindows)
            await EnsureOsm2PgSqlWindows(targetFolder, exeName);
        else
            await EnsureOsm2PgSqlLinux(exeName);
    }

    private async Task EnsureOsm2PgSqlWindows(string targetFolder, string exeName)
    {
        string folder = Path.Combine(targetFolder, "osm2pgsql_bin");

        string? existingExe = Directory.Exists(folder)
            ? Directory.GetFiles(folder, exeName, SearchOption.AllDirectories).FirstOrDefault()
            : null;

        if (existingExe is not null)
        {
            _osm2pgsqlPath = existingExe;
            _logger.LogInformation("osm2pgsql already present at: {Path}", _osm2pgsqlPath);
            return;
        }

        string listingUrl = _settings["Osm2PgsqlUrl"]!;
        string downloadUrl = await ScrapeWindowsDownloadUrl(listingUrl);

        _logger.LogInformation("Downloading osm2pgsql from: {Url}", downloadUrl);

        string archivePath = Path.Combine(Path.GetTempPath(), $"osm2pgsql_{Guid.NewGuid()}.zip");
        Directory.CreateDirectory(folder);

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            var response = await client.GetAsync(downloadUrl);
            response.EnsureSuccessStatusCode();
            await File.WriteAllBytesAsync(archivePath, await response.Content.ReadAsByteArrayAsync());

            _logger.LogInformation("Extracting archive to: {Folder}", folder);
            ZipFile.ExtractToDirectory(archivePath, folder, overwriteFiles: true);

            string? found = Directory.GetFiles(folder, exeName, SearchOption.AllDirectories).FirstOrDefault();
            if (found is null)
                throw new Exception($"osm2pgsql.exe not found anywhere under {folder} after extraction.");

            _osm2pgsqlPath = found;
            _logger.LogInformation("osm2pgsql ready at: {Path}", _osm2pgsqlPath);
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
            _logger.LogInformation("osm2pgsql found on PATH at: {Path}", _osm2pgsqlPath);
            return;
        }

        _logger.LogInformation("osm2pgsql not found. Installing via apt-get...");
        await ExecuteCommand("apt-get", "update -qq");
        await ExecuteCommand("apt-get", "install -y osm2pgsql");

        onPath = FindOnPath(exeName);
        if (onPath is null)
            throw new Exception("osm2pgsql was not found on PATH even after apt-get install.");

        _osm2pgsqlPath = onPath;
        _logger.LogInformation("osm2pgsql installed at: {Path}", _osm2pgsqlPath);
    }

    private async Task<string> ScrapeWindowsDownloadUrl(string listingUrl)
    {
        _logger.LogInformation("Scraping directory listing: {Url}", listingUrl);

        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        string html = await client.GetStringAsync(listingUrl);
        var matches = Regex.Matches(html, @"(osm2pgsql-[\d.]+-x64\.zip)");

        if (matches.Count == 0)
            throw new Exception($"Could not find any osm2pgsql download links on: {listingUrl}");

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

    private static string? FindOnPath(string exeName)
    {
        string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        return pathEnv
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir, exeName))
            .FirstOrDefault(File.Exists);
    }
}