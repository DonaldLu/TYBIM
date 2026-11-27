using System;
using System.Collections.Generic;
using System.Linq;
using Point = TYBIM.AutoBuild.WallRegionGeometry.Point;
using Segment = TYBIM.AutoBuild.WallRegionGeometry.Segment;

namespace TYBIM.AutoBuild
{
    internal static class BeamGeometry
    {
        internal const double Tolerance = 1 / 304.8;

        internal sealed class Candidate
        {
            internal Segment Axis;
            internal string Layer;
            internal double Width;
        }

        internal sealed class Result
        {
            internal List<Candidate> Candidates { get; } = new List<Candidate>();
            internal int Short, Sloped, Unsupported, Overlapping, Existing;

            internal string Summary => $"過短 {Short} ｜非水平 {Sloped} ｜曲線 {Unsupported} ｜CAD 軸重疊 {Overlapping} ｜既有樑 {Existing}";
        }

        internal static bool IsSuggestedLayer(string layer) =>
            string.Equals(layer, "0S-B", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(layer, "0S-SB", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(layer, "S-BEAMH", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(layer, "S-GIRH", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(layer, "BEAM", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(layer, "樑", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(layer, "梁", StringComparison.OrdinalIgnoreCase);

        internal static Point ToPlan(Autodesk.Revit.DB.XYZ point) => new Point(point.X, point.Y);

        internal static Result Build(IEnumerable<LayersForm.CadWallLine> source, IReadOnlyList<Segment> existing,
            bool pairEdges, double minWidth, double maxWidth, double minLength, int unsupported)
        {
            var result = new Result { Unsupported = unsupported };
            var candidates = new List<Candidate>();
            foreach (var layer in source.GroupBy(line => line.Layer))
            {
                var byElevation = new List<Segment>();
                double elevation = double.NegativeInfinity;
                void FinishPlane()
                {
                    if (byElevation.Count == 0) return;
                    if (pairEdges)
                    {
                        foreach (var beam in WallPairBuilder.BuildBeamAxes(byElevation, minWidth, maxWidth))
                            candidates.Add(new Candidate { Axis = beam.Axis, Width = beam.Width, Layer = layer.Key });
                    }
                    else
                    {
                        foreach (var axis in byElevation)
                            candidates.Add(new Candidate { Axis = axis, Layer = layer.Key });
                    }
                    byElevation.Clear();
                }

                foreach (var line in layer.OrderBy(line => (line.Start.Z + line.End.Z) / 2))
                {
                    if (Math.Abs(line.End.Z - line.Start.Z) > Tolerance) { result.Sloped++; continue; }
                    var axis = new Segment(ToPlan(line.Start), ToPlan(line.End));
                    if (axis.Length <= minLength) { result.Short++; continue; }
                    double z = (line.Start.Z + line.End.Z) / 2;
                    if (z - elevation > Tolerance)
                    {
                        FinishPlane();
                        elevation = z;
                    }
                    byElevation.Add(axis);
                }
                FinishPlane();
            }

            foreach (var candidate in candidates.OrderByDescending(item => item.Axis.Length))
            {
                if (candidate.Axis.Length <= minLength) { result.Short++; continue; }
                if (existing.Any(axis => Overlaps(candidate.Axis, axis))) { result.Existing++; continue; }
                if (result.Candidates.Any(item => Overlaps(candidate.Axis, item.Axis))) { result.Overlapping++; continue; }
                result.Candidates.Add(candidate);
            }
            return result;
        }

        private static bool Overlaps(Segment axis, Segment other)
        {
            var direction = (axis.B - axis.A) * (1 / axis.Length);
            var a = other.A - axis.A;
            var b = other.B - axis.A;
            if (Math.Abs(a.X * direction.Y - a.Y * direction.X) > Tolerance ||
                Math.Abs(b.X * direction.Y - b.Y * direction.X) > Tolerance) return false;
            double pa = a.X * direction.X + a.Y * direction.Y;
            double pb = b.X * direction.X + b.Y * direction.Y;
            return Math.Min(axis.Length, Math.Max(pa, pb)) - Math.Max(0, Math.Min(pa, pb)) > Tolerance;
        }
    }
}
