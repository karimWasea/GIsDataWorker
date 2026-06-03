using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

public class MapUpdateWorker : BackgroundService
{
    private readonly ILogger<MapUpdateWorker> _logger;
    private readonly IConfiguration _config;
    private readonly IConfigurationSection _mapSettings;
    private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);
     private string _osm2pgsqlPath = string.Empty;
    private IConfigurationSection _settings => _mapSettings;

    public MapUpdateWorker(ILogger<MapUpdateWorker> logger, IConfiguration config)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _config = config;
        _mapSettings = _config.GetSection("MapSettings");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            string osm2pgsqlFolder = _mapSettings["Osm2PgsqlFolder"] ?? Path.Combine(AppContext.BaseDirectory, "tools");
            await EnsureOsm2PgSqlExists(osm2pgsqlFolder);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Initialization failed. Worker will stop.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            await _lock.WaitAsync(stoppingToken);
            try
            {
                if (await IsDatabaseExists())
                    await ApplyDiffUpdate();
                else
                    await RunFullImport();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in processing loop.");
            }
            finally
            {
                _lock.Release();
            }
            await Task.Delay(TimeSpan.FromDays(30), stoppingToken);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Full import
    // ─────────────────────────────────────────────────────────────────────────
    private async Task RunFullImport()
    {
        string baseUrl = _settings["BaseUrl"]!;
        string downloadUrl = await GetLatestUrl(baseUrl, @"href=""([^""]+latest\.osm\.pbf)""");
        if (string.IsNullOrEmpty(downloadUrl))
            throw new Exception("Could not find full map URL.");

        string pgRoot = _settings["PostgreSqlRootPath"]!;
        string binPath = FindPostgresBinPath(pgRoot)
            ?? throw new Exception($"Could not find PostgreSQL bin folder under: {pgRoot}");
        await SetupDatabase(binPath);

        string tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pbf");
        try
        {
            await DownloadFile(downloadUrl, tempPath);

            var fileInfo = new FileInfo(tempPath);
            if (!fileInfo.Exists || fileInfo.Length == 0)
                throw new Exception($"Download produced an empty or missing file at: {tempPath}");

            await ExecuteOsm2PgSql(tempPath, "--slim -s");
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
    private async Task ApplyDiffUpdate()
    {
        string updatesUrl = _settings["UpdatesUrl"]!;
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
            await ExecuteOsm2PgSql(tempDiffPath, "--append");
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
            if (matches.Count == 0)
                return string.Empty;  // FIX 2: was null! — inconsistent with IsNullOrEmpty checks

            return new Uri(new Uri(pageUrl), matches[^1].Groups[1].Value).ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching URL: {Url}", pageUrl);
            return string.Empty;  // FIX 2: was null!
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  osm2pgsql execution
    // ─────────────────────────────────────────────────────────────────────────
    private async Task ExecuteOsm2PgSql(string tempPath, string extraArgs)
    {
        if (!File.Exists(tempPath))
            throw new FileNotFoundException($"File not found before import: {tempPath}");

        string host = _settings["PgHost"] ?? "localhost";
        string port = _settings["PgPort"] ?? "5432";
        string database = _settings["PgDatabase"]!;
        string user = _settings["PgUser"] ?? "postgres";

        string styleArg = ResolveStyleArg();  // ✅ Use the comprehensive resolver

        string args = $"{extraArgs} {styleArg} -H {host} -P {port} -d {database} -U {user} \"{tempPath}\"";

        _logger.LogInformation("Running osm2pgsql with args: {Args}", args);
        await ExecuteCommand(_osm2pgsqlPath, args);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Resolve the style / output argument (flex vs. pgsql)
    // ─────────────────────────────────────────────────────────────────────────
    private string ResolveStyleArg()
    {
        string configuredStyle = _settings["Osm2PgsqlStyleFile"]!;

        // ── 1 & 2: explicitly configured path ────────────────────────────────
        if (!string.IsNullOrEmpty(configuredStyle))
        {
            if (!File.Exists(configuredStyle))
            {
                _logger.LogWarning(
                    "Configured Osm2PgsqlStyleFile not found: {Path}. " +
                    "Will search next to the osm2pgsql binary instead.",
                    configuredStyle);
            }
            else
            {
                return BuildStyleArg(configuredStyle);
            }
        }

        // ── 3 & 4: search next to the exe (the ZIP bundles style files there) ─
        string exeDir = Path.GetDirectoryName(_osm2pgsqlPath) ?? "";

        // Prefer any .lua file (flex output) — avoids the deprecation warning
        string? luaFile = Directory
            .GetFiles(exeDir, "*.lua", SearchOption.TopDirectoryOnly)
            .FirstOrDefault();

        if (luaFile is not null)
        {
            _logger.LogInformation("Using bundled Lua style: {File}", luaFile);
            return BuildStyleArg(luaFile);
        }

        // Fall back to .style file next to the exe
        string? styleFile = Directory
            .GetFiles(exeDir, "*.style", SearchOption.TopDirectoryOnly)
            .FirstOrDefault();

        if (styleFile is not null)
        {
            _logger.LogInformation("Using bundled legacy style: {File}", styleFile);
            return BuildStyleArg(styleFile);
        }

        // ── 5: nothing found — omit -S entirely ──────────────────────────────
        _logger.LogWarning(
            "No style file found. osm2pgsql will use its built-in default " +
            "(deprecated pgsql output). Consider adding a Lua style file.");
        return string.Empty;
    }

    private static string BuildStyleArg(string stylePath)
    {
        bool isLua = stylePath.EndsWith(".lua", StringComparison.OrdinalIgnoreCase);
        return isLua
            ? $"--output=flex -S \"{stylePath}\""
            : $"-S \"{stylePath}\"";
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Database setup
    // ─────────────────────────────────────────────────────────────────────────
    private async Task SetupDatabase(string binPath)
    {
        // Fix: Dynamic extension for Linux vs Windows
        string exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "psql.exe" : "psql";
        string psql = Path.Combine(binPath, exeName);

        string dbName = _mapSettings["PgDatabase"]!;
        string user = _mapSettings["PgUser"]!;
        string host = _mapSettings["PgHost"]!;

        // Use quotes to handle case-sensitive names properly in PostgreSQL
        string quotedDbName = $"\\\"{dbName}\\\"";

        if (await IsDatabaseExists())
        {
            _logger.LogInformation("Database {DbName} exists.", dbName);
            return;
        }

        _logger.LogInformation("Creating database: {DbName}", dbName);
        await ExecuteCommand(psql, $"-U {user} -h {host} -d postgres -c \"CREATE DATABASE {quotedDbName};\"");

        // PostGIS creation
        await ExecuteCommand(psql, $"-U {user} -h {host} -d {quotedDbName} -c \"CREATE EXTENSION IF NOT EXISTS postgis;\"");
    }

    private string? FindPostgresBinPath(string root)
    {
        if (!Directory.Exists(root)) return null;
        return Directory.GetDirectories(root)
            .Select(d => new DirectoryInfo(d))
            .Where(di => int.TryParse(di.Name, out _))
            .OrderByDescending(di => int.Parse(di.Name))
            .Select(di => Path.Combine(di.FullName, "bin"))
            .FirstOrDefault();
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

        process.StartInfo.EnvironmentVariables["PGPASSWORD"] = _config["MapSettings:PgPassword"]!;

        process.Start();

        string output = await process.StandardOutput.ReadToEndAsync();
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
            throw new Exception(
                $"Process {Path.GetFileName(fileName)} failed (Exit Code: {process.ExitCode}).\nError: {error}");

        return output;
    }

    private async Task<bool> IsDatabaseExists()
    {
        try
        {
            string pgRoot = _mapSettings["PostgreSqlRootPath"]!;
            string? binPath = FindPostgresBinPath(pgRoot);
            if (binPath is null)
            {
                _logger.LogWarning("PostgreSQL bin path not found.");
                return false;
            }

            // تحديد اسم الملف التنفيذي بناءً على نظام التشغيل
            string exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "psql.exe" : "psql";
            string psqlPath = Path.Combine(binPath, exeName);

            // الاسم الأصلي من الإعدادات للإنشاء (لا نستخدم Lower هنا)
            string dbName = _mapSettings["PgDatabase"]!;
            // الاسم بصيغة Lower للتحقق داخل قاعدة بيانات PostgreSQL
            string dbNameLower = dbName;

            string user = _mapSettings["PgUser"]!;
            string host = _mapSettings["PgHost"]!;

            // أمر التحقق: نتصل بقاعدة بيانات 'postgres' الافتراضية للبحث عن وجود قاعدة البيانات الأخرى
            // نستخدم -d postgres لضمان الاتصال الناجح قبل الاستعلام
            string checkCmd = $"-U {user} -h {host} -d postgres -tAc \"SELECT 1 FROM pg_database WHERE datname='{dbNameLower}'\"";

            string result = await ExecuteCommand(psqlPath, checkCmd);

            bool exists = result?.Trim() == "1";

            if (exists)
                _logger.LogInformation("Database '{DbName}' found.", dbName);
            else
                _logger.LogInformation("Database '{DbName}' does not exist.", dbName);

            return exists;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking if database exists.");
            return false;
        }
    }
    private async Task EnsureOsm2PgSqlExists(string targetFolder)
    {
        bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        string exeName = isWindows ? "osm2pgsql.exe" : "osm2pgsql";

        // 1. فحص هل هو موجود مسبقاً (سواء في المسار المخصص أو على الـ PATH)
        string? existingExe = FindExecutable(targetFolder, exeName);
        if (existingExe != null)
        {
            _osm2pgsqlPath = existingExe;
            _logger.LogInformation("osm2pgsql found at: {Path}", _osm2pgsqlPath);
            return;
        }

        _logger.LogInformation("osm2pgsql not found. Initializing installation for {OS}...",
            isWindows ? "Windows" : "Linux");

        if (isWindows)
            await EnsureOsm2PgSqlWindows(targetFolder, exeName);
        else
            await EnsureOsm2PgSqlLinux(exeName);
    }

    // دالة ذكية للبحث عن الملف في كل مكان
    private string? FindExecutable(string targetFolder, string exeName)
    {
        // أ- البحث في مجلد المشروع (targetFolder)
        if (Directory.Exists(targetFolder))
        {
            var found = Directory.GetFiles(targetFolder, exeName, SearchOption.AllDirectories).FirstOrDefault();
            if (found != null) return found;
        }

        // ب- البحث في الـ PATH (مفيد جداً للينكس إذا تم تثبيته بـ apt)
        string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        return pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                      .Select(dir => Path.Combine(dir, exeName))
                      .FirstOrDefault(File.Exists);
    }

    private async Task EnsureOsm2PgSqlWindows(string targetFolder, string exeName)
    {
        string folder = Path.Combine(targetFolder, "osm2pgsql_bin");
        string downloadUrl = await ScrapeWindowsDownloadUrl(); // الرابط الديناميكي

        _logger.LogInformation("Downloading latest osm2pgsql from: {Url}", downloadUrl);

        string archivePath = Path.Combine(Path.GetTempPath(), $"osm2pgsql_{Guid.NewGuid()}.zip");
        Directory.CreateDirectory(folder);

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            var response = await client.GetAsync(downloadUrl);
            response.EnsureSuccessStatusCode();
            await File.WriteAllBytesAsync(archivePath, await response.Content.ReadAsByteArrayAsync());

            ZipFile.ExtractToDirectory(archivePath, folder, overwriteFiles: true);

            _osm2pgsqlPath = Directory.GetFiles(folder, exeName, SearchOption.AllDirectories).First();
            _logger.LogInformation("osm2pgsql installed successfully at: {Path}", _osm2pgsqlPath);
        }
        finally
        {
            if (File.Exists(archivePath)) File.Delete(archivePath);
        }
    }
  
    // ── Linux: install via apt (no prebuilt binaries exist on osm2pgsql.org) ─
    private async Task EnsureOsm2PgSqlLinux(string exeName)
    {
        // Check if already on PATH (installed by apt or previously by this method)
        string? onPath = FindOnPath(exeName);
        if (onPath is not null)
        {
            _osm2pgsqlPath = onPath;
            _logger.LogInformation("osm2pgsql found on PATH at: {Path}", _osm2pgsqlPath);
            return;
        }

        _logger.LogInformation(
            "osm2pgsql not found. Installing via apt-get...");

        await ExecuteCommand("apt-get", "update -qq");
        await ExecuteCommand("apt-get", "install -y osm2pgsql");

        // Re-check PATH after install
        onPath = FindOnPath(exeName);
        if (onPath is null)
            throw new Exception(
                "osm2pgsql was not found on PATH even after apt-get install. " +
                "Please install it manually: sudo apt-get install osm2pgsql");

        _osm2pgsqlPath = onPath;
        _logger.LogInformation("osm2pgsql installed at: {Path}", _osm2pgsqlPath);
    }

    // ── Scrape the Windows directory listing as a fallback ───────────────────
    private async Task<string> ScrapeWindowsDownloadUrl()
    {
        // استخدام الرابط الموجود في الـ appsettings.json
        string listingUrl = _mapSettings["Osm2PgsqlUrl"]!;

        _logger.LogInformation("Scraping directory listing: {Url}", listingUrl);

        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        string html = await client.GetStringAsync(listingUrl);

        // Regex يطابق الملفات المعتادة لنسخة الويندوز
        var matches = Regex.Matches(html, @"(osm2pgsql-[\d.]+-x64\.zip)");

        if (matches.Count == 0)
            throw new Exception($"Could not find any download links on: {listingUrl}");

        string newest = matches.Cast<Match>()
            .Select(m => m.Groups[1].Value)
            .OrderByDescending(name => {
                var v = Regex.Match(name, @"(\d+\.\d+\.\d+)");
                return v.Success ? Version.Parse(v.Groups[1].Value) : new Version(0, 0);
            })
            .First();

        return new Uri(new Uri(listingUrl), newest).ToString();
    }
    // ── Helper: find an executable on the system PATH ────────────────────────
    private static string? FindOnPath(string exeName)
    {
        string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        return pathEnv
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir, exeName))
            .FirstOrDefault(File.Exists);
    }
}