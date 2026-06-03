using System.Diagnostics;
using System.Text.RegularExpressions;

public class MapUpdateWorker : BackgroundService
{
    private readonly ILogger<MapUpdateWorker> _logger;
    private readonly IConfiguration _config;
 
    public MapUpdateWorker(ILogger<MapUpdateWorker> logger, IConfiguration config)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _config = config;

     
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Main loop
    // ─────────────────────────────────────────────────────────────────────────
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = _config.GetSection("MapSettings");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // التحقق: هل قاعدة البيانات موجودة؟
                bool dbExists = await IsDatabaseExists(settings);

                if (dbExists)
                {
                    _logger.LogInformation("Database found. Checking for updates...");
                    await ApplyDiffUpdate(settings);
 
                }
                else
                {
                    _logger.LogInformation("Database not found. Starting full import...");
                    await RunFullImport(settings);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in processing loop.");
            }

            await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Full import
    // ─────────────────────────────────────────────────────────────────────────
    private async Task RunFullImport(IConfigurationSection settings)
    {
        string baseUrl = settings["BaseUrl"]!;
        string downloadUrl = await GetLatestUrl(baseUrl, @"href=""([^""]+latest\.osm\.pbf)""");
        if (string.IsNullOrEmpty(downloadUrl))
            throw new Exception("Could not find full map URL.");

        // Ensure the database exists with PostGIS before importing
        string pgRoot = settings["PostgreSqlRootPath"]!;
        string binPath = FindPostgresBinPath(pgRoot)
            ?? throw new Exception($"Could not find PostgreSQL bin folder under: {pgRoot}");
        await SetupDatabase(binPath, settings);

        string tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pbf");
        try
        {
            await DownloadFile(downloadUrl, tempPath);

            var fileInfo = new FileInfo(tempPath);
            if (!fileInfo.Exists || fileInfo.Length == 0)
                throw new Exception($"Download produced an empty or missing file at: {tempPath}");

 
            await ExecuteOsm2PgSql(settings, tempPath, "--slim -s");
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
    private async Task ApplyDiffUpdate(IConfigurationSection settings)
    {
        string updatesUrl = settings["UpdatesUrl"]!;
        string diffUrl = await GetLatestUrl(updatesUrl, @"href=""([^""]+osc\.gz)""");
        if (string.IsNullOrEmpty(diffUrl))
        {
            _logger.LogInformation("There are no updates");
            return;
        }



        string tempDiffPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".osc.gz");
        try
        {
            await DownloadFile(diffUrl, tempDiffPath);
            await ExecuteOsm2PgSql(settings, tempDiffPath, "--append");
             _logger.LogInformation("Diff update applied successfully.");
        }
        finally
        {
            if (File.Exists(tempDiffPath)) File.Delete(tempDiffPath);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Download helper
    // ─────────────────────────────────────────────────────────────────────────
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
                if (pct / 5 != lastReportedPct / 5) // report every 5 %
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
        var root = Path.GetPathRoot(path) ?? "C:\\";
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
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            string html = await client.GetStringAsync(pageUrl);
            var matches = Regex.Matches(html, pattern);
            if (matches.Count == 0) return null!;
            return new Uri(new Uri(pageUrl), matches[^1].Groups[1].Value).ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching URL: {Url}", pageUrl);
            return null!;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  osm2pgsql execution  —  uses PgHost / PgPort / PgDatabase / PgUser
    // ─────────────────────────────────────────────────────────────────────────
    private async Task ExecuteOsm2PgSql(IConfigurationSection settings, string tempPath, string extraArgs)
    {
        if (!File.Exists(tempPath))
            throw new FileNotFoundException($"File not found before import: {tempPath}");

        string host = settings["PgHost"] ?? "localhost";
        string port = settings["PgPort"] ?? "5432";
        // تأكد هنا من استخدام الاسم بحروف صغيرة أو الاسم المطابق لما في الإعدادات تماماً
        string database = settings["PgDatabase"]!.ToLower();
        string user = settings["PgUser"] ?? "postgres";

        string styleFile = settings["Osm2PgsqlStyleFile"] ?? "";
        string styleArg = !string.IsNullOrEmpty(styleFile) && File.Exists(styleFile)
                            ? $"-S \"{styleFile}\""
                            : "";

        // نستخدم الاسم كما هو في الإعدادات
        string args = $"{extraArgs} {styleArg} -H {host} -P {port} -d {database} -U {user} \"{tempPath}\"";

        _logger.LogInformation("Running osm2pgsql with args: {Args}", args);
        await ExecuteCommand("osm2pgsql.exe", args);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Database setup (optional — call manually if needed)
    // ─────────────────────────────────────────────────────────────────────────
    private async Task SetupDatabase(string binPath, IConfigurationSection settings)
    {
        string psql = Path.Combine(binPath, "psql.exe");
        string dbName = settings["PgDatabase"]!;
        string user = settings["PgUser"]!;
        string host = settings["PgHost"]!;

        // 1. التحقق من وجود قاعدة البيانات
        string checkCmd = $"-U {user} -h {host} -d postgres -tAc \"SELECT 1 FROM pg_database WHERE datname='{dbName.ToLower()}'\"";
        string existsResult = await ExecuteCommand(psql, checkCmd);

        if (existsResult.Trim() == "1")
        {
            _logger.LogInformation("Database {DbName} already exists. Skipping creation.", dbName);
            return; // خروج آمن: القاعدة موجودة بالفعل
        }

        // 2. إذا لم تكن موجودة، نقوم بإنشائها
        _logger.LogInformation("Creating database: {DbName}", dbName);
        await ExecuteCommand(psql, $"-U {user} -h {host} -d postgres -c \"CREATE DATABASE \\\"{dbName}\\\";\"");

        // 3. تفعيل الإضافات (Extensions)
        _logger.LogInformation("Enabling PostGIS extensions...");
        await ExecuteCommand(psql, $"-U {user} -h {host} -d {dbName} -c \"CREATE EXTENSION IF NOT EXISTS postgis; CREATE EXTENSION IF NOT EXISTS postgis_topology;\"");
    }
    private string FindPostgresBinPath(string root)
    {
        if (!Directory.Exists(root)) return null!;
        return Directory.GetDirectories(root)
            .Select(d => new DirectoryInfo(d))
            .Where(di => int.TryParse(di.Name, out _))
            .OrderByDescending(di => int.Parse(di.Name))
            .Select(di => Path.Combine(di.FullName, "bin"))
            .FirstOrDefault()!;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Process runner
    // ─────────────────────────────────────────────────────────────────────────
    private async Task<string> ExecuteCommand(string fileName, string args)
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

        process.StartInfo.EnvironmentVariables["PGPASSWORD"] = _config["MapSettings:PgPassword"]! ;

        process.Start();

        string output = await process.StandardOutput.ReadToEndAsync();
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
            throw new Exception(
                $"Process {Path.GetFileName(fileName)} failed (Exit Code: {process.ExitCode}).\nError: {error}");

        return output;
    }

    private async Task<bool> IsDatabaseExists(IConfigurationSection settings)
    {
        try
        {
            string pgRoot = settings["PostgreSqlRootPath"]!;
            string binPath = FindPostgresBinPath(pgRoot);
            if (binPath == null) return false;

            string psql = Path.Combine(binPath, "psql.exe");
            string dbName = settings["PgDatabase"]!.ToLower();
            string user = settings["PgUser"]!;
            string host = settings["PgHost"]!;

            // استعلام سريع للتحقق
            string checkCmd = $"-U {user} -h {host} -tAc \"SELECT 1 FROM pg_database WHERE datname='{dbName}'\"";
            string result = await ExecuteCommand(psql, checkCmd);
            return result.Trim() == "1";
        }
        catch
        {
            return false;
        }
    }
}