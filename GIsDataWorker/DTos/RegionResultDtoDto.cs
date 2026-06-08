using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GIsDataWorker.DTos
{
    public class RegionResultDto
    {
        public string? Name { get; set; }
        public string? AdminLevel { get; set; }
        public string? Boundary { get; set; }
        public string? Place { get; set; }
        public string? OsmId { get; set; }

        // إضافات للهيكل الإداري
        public string? City { get; set; }
        public string? Governorate { get; set; }
        public string? District { get; set; }
    }
}
