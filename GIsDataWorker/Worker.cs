using GIsDataWorker.Models;

namespace GIsDataWorker
{
    public class Worker : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<Worker> _logger;

        public Worker(IServiceScopeFactory scopeFactory, ILogger<Worker> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                using (var scope = _scopeFactory.CreateScope())
                {
                    var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                    // جلب مناطق معينة (مثلاً الأماكن التي لها اسم)
                    //var regions = dbContext.Polygons
                    //    .Where(p => p.Name != null)
                    //    .Take(10) // للتجربة فقط
                    ////    .ToList();

                    //foreach (var region in regions)
                    //{
                    //    _logger.LogInformation("Processing: {Name}", region.Name);

                    //    // هنا نضع الكود الذي يرسل البيانات للمكان الآخر
                    //    await SendDataToExternalService(region);
                    //}
                }

                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }

        private async Task SendDataToExternalService(OsmPolygon region)
        {
            // منطق الإرسال (HttpClient أو أي شيء آخر)
            await Task.CompletedTask;
        }

    }
}