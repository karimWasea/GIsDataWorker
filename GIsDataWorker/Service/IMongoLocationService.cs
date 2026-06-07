using GIsDataWorker.DTos;

namespace GIsDataWorker.Service;

public interface IMongoLocationService
{
    IAsyncEnumerable<MongoLocationDto> GetLocationsAsync(CancellationToken cancellationToken = default);
}
