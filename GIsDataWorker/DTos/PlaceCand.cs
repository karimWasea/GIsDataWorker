using GIsDataWorker.Utailites;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GIsDataWorker.DTos
{
    public sealed record PlaceCand(string Place, string? Name, long OsmId, Src Src,
       double DistanceM, double Lat, double Lon);
    public sealed record AdminCand(int Level, string? Name, long OsmId, double X, double Y);
}
