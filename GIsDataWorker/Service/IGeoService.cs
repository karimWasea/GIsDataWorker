using GIsDataWorker.DTos;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GIsDataWorker.Service { 

    public interface IGeoService
    {
        Task<RegionResultDto?> GetRegionByCoordinatesAsync(double latitude, double longitude, CancellationToken ct = default);
       Task<List<AttractionResultDto>> GetNearbyAttractionsAsync(
                   double latitude,
                   double longitude,
                   double radiusMeters = 1000,
                   int maxResults = 100);
    }
}