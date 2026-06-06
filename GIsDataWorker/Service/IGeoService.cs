using GIsDataWorker.DTos;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GIsDataWorker.Service { 

    public interface IGeoService
    {
        Task<List<RegionResultDtoDto>> GetRegionByCoordinatesAsync(double latitude, double longitude);
       Task<List<AttractionResultDto>> GetNearbyAttractionsAsync(
                   double latitude,
                   double longitude,
                   double radiusMeters = 1000,
                   int maxResults = 100);
    }
}