using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace TYBIM_2025.AutoBuild
{
    /// <summary>Unions cross-sections in 2D, avoiding coplanar Revit solid unions.</summary>
    internal sealed class FloorFootprintGeometry
    {
        private const double Tolerance = 1e-5; // Feet, about 0.003 mm.
        private PathGeometry occupied;
        private readonly List<(Point A, Point B)> inputEdges = new List<(Point, Point)>();

        public void Add(IEnumerable<IList<Point>> loops)
        {
            var footprint = new PathGeometry { FillRule = FillRule.EvenOdd };
            foreach (IList<Point> points in loops)
            {
                if (points.Count < 3) continue;
                for (int i = 0; i < points.Count; i++)
                    inputEdges.Add((points[i], points[(i + 1) % points.Count]));
                var figure = new PathFigure { StartPoint = points[0], IsClosed = true, IsFilled = true };
                figure.Segments.Add(new PolyLineSegment(points.Skip(1), true));
                footprint.Figures.Add(figure);
            }
            if (footprint.Figures.Count == 0) return;
            occupied = occupied == null ? footprint : Geometry.Combine(occupied, footprint,
                GeometryCombineMode.Union, null, Tolerance, ToleranceType.Absolute);
        }

        public List<IList<Point>> GetInnerRegions()
        {
            var result = new List<IList<Point>>();
            var boundaries = ReadBoundaries();
            // Odd nesting depth identifies holes, independent of loop orientation.
            for (int i = 0; i < boundaries.Count; i++)
            {
                int depth = 0;
                for (int j = 0; j < boundaries.Count; j++)
                    if (i != j && Contains(boundaries[j], boundaries[i][0])) depth++;
                if (depth % 2 == 1) result.Add(boundaries[i]);
            }
            return result;
        }

        // WPF can separate coincident faces during successive unions. Seat a short
        // strip across the middle of their shared span using original coordinates.
        internal void StabilizeContacts()
        {
            var edges = inputEdges.ToArray();
            const double seat = 0.0003; // feet; about 0.09 mm into each member
            for (int i = 0; i < edges.Length; i++)
            {
                var a = edges[i];
                Vector axis = a.B - a.A;
                double length = axis.Length;
                if (length < seat * 4) continue;
                axis /= length;
                var normal = new Vector(-axis.Y, axis.X);
                for (int j = i + 1; j < edges.Length; j++)
                {
                    var b = edges[j];
                    if (Math.Abs((b.A - a.A) * normal) > Tolerance ||
                        Math.Abs((b.B - a.A) * normal) > Tolerance) continue;
                    double u = (b.A - a.A) * axis, v = (b.B - a.A) * axis;
                    if (u <= v) continue;
                    double lo = Math.Max(0, v), hi = Math.Min(length, u);
                    if (hi - lo < seat * 4) continue;
                    Point p = a.A + axis * (lo + seat), q = a.A + axis * (hi - seat);
                    Add(new[] { new[] { p - normal * seat, q - normal * seat,
                        q + normal * seat, p + normal * seat } });
                }
            }
        }

        internal int CloseSmallGaps(double maximumGap, Func<Point, Vector, bool> acceptJoint = null)
        {
            var edges = new List<(Point Start, Point End)>();
            var boundaries = ReadBoundaries();
            bool IsOccupied(Point point) => boundaries.Count(loop => Contains(loop, point)) % 2 != 0;
            foreach (IList<Point> loop in boundaries)
                for (int i = 0; i < loop.Count; i++)
                    edges.Add((loop[i], loop[(i + 1) % loop.Count]));

            var bridges = new List<IList<Point>>();
            for (int i = 0; i < edges.Count; i++)
            {
                var a = edges[i];
                Vector tangent = a.End - a.Start;
                double length = tangent.Length;
                if (length <= maximumGap) continue;
                tangent /= length;
                var normal = new Vector(-tangent.Y, tangent.X);
                var search = new Rect(a.Start, a.End);
                search.Inflate(maximumGap + Tolerance, maximumGap + Tolerance);
                for (int j = i + 1; j < edges.Count; j++)
                {
                    var b = edges[j];
                    if (!search.IntersectsWith(new Rect(b.Start, b.End))) continue;
                    Vector other = b.End - b.Start;
                    double otherLength = other.Length;
                    if (otherLength <= maximumGap || Math.Abs(tangent * other / otherLength) < 0.999) continue;

                    double b0 = (b.Start - a.Start) * tangent;
                    double b1 = (b.End - a.Start) * tangent;
                    double start = Math.Max(0, Math.Min(b0, b1));
                    double end = Math.Min(length, Math.Max(b0, b1));
                    // Require shared edge length, so nearby points alone cannot close a room.
                    if (end - start <= maximumGap) continue;
                    Point nearStart = a.Start + start * tangent;
                    Point nearEnd = a.Start + end * tangent;
                    Point farStart = b.Start + ((start - b0) / (b1 - b0)) * other;
                    Point farEnd = b.Start + ((end - b0) / (b1 - b0)) * other;
                    double gapStart = (farStart - nearStart) * normal;
                    double gapEnd = (farEnd - nearEnd) * normal;
                    if (gapStart * gapEnd <= 0 || Math.Min(Math.Abs(gapStart), Math.Abs(gapEnd)) <= Tolerance
                        || Math.Max(Math.Abs(gapStart), Math.Abs(gapEnd)) > maximumGap + Tolerance) continue;

                    Point midpoint = nearStart + (nearEnd - nearStart) * 0.5
                        + normal * ((gapStart + gapEnd) * 0.25);
                    if (!IsOccupied(midpoint) && (acceptJoint == null || acceptJoint(midpoint, normal)))
                    {
                        // Give the union an actual overlap on both ends instead of relying on
                        // numerically identical edges after projecting the bridge.
                        Vector overlap = normal * (Math.Sign(gapStart) * Tolerance * 10);
                        if (IsOccupied(nearStart + (nearEnd - nearStart) * 0.5 - overlap)
                            && IsOccupied(farStart + (farEnd - farStart) * 0.5 + overlap))
                            bridges.Add(new[] { nearStart - overlap, nearEnd - overlap, farEnd + overlap, farStart + overlap });
                    }
                }
            }

            // Find bridges against the original geometry only. A bridge cannot extend another bridge.
            foreach (IList<Point> bridge in bridges) Add(new[] { bridge });
            return bridges.Count;
        }

        private List<IList<Point>> ReadBoundaries()
        {
            var boundaries = new List<IList<Point>>();
            if (occupied == null) return boundaries;
            foreach (PathFigure figure in occupied.GetFlattenedPathGeometry(Tolerance, ToleranceType.Absolute).Figures)
            {
                var points = new List<Point> { figure.StartPoint };
                foreach (PathSegment segment in figure.Segments)
                {
                    if (segment is PolyLineSegment polyline) points.AddRange(polyline.Points);
                    else if (segment is LineSegment line) points.Add(line.Point);
                }
                if (points.Count > 1 && (points[points.Count - 1] - points[0]).Length < Tolerance)
                    points.RemoveAt(points.Count - 1);
                if (points.Count >= 3 && Math.Abs(SignedArea(points)) > Tolerance * Tolerance)
                    boundaries.Add(points);
            }
            return boundaries;
        }

        internal static bool IsRegionClear(IList<Point> boundary, IEnumerable<Rect> uncertainBounds)
        {
            var bounds = new Rect(boundary[0], boundary[0]);
            foreach (Point point in boundary) bounds.Union(point);
            bounds.Inflate(Tolerance, Tolerance);
            foreach (Rect uncertain in uncertainBounds)
            {
                if (!bounds.IntersectsWith(uncertain)) continue;
                Rect box = uncertain;
                box.Inflate(Tolerance, Tolerance);
                if (Contains(boundary, box.TopLeft)) return false;
                for (int i = 0; i < boundary.Count; i++)
                {
                    Point a = boundary[i], b = boundary[(i + 1) % boundary.Count];
                    if (box.Contains(a)) return false;
                    if (!box.IntersectsWith(new Rect(a, b))) continue;
                    Vector edge = b - a;
                    double[] sides = { Vector.CrossProduct(edge, box.TopLeft - a),
                        Vector.CrossProduct(edge, box.TopRight - a),
                        Vector.CrossProduct(edge, box.BottomLeft - a),
                        Vector.CrossProduct(edge, box.BottomRight - a) };
                    if (sides.Min() <= 0 && sides.Max() >= 0) return false;
                }
            }
            return true;
        }

        private static bool Contains(IList<Point> polygon, Point point)
        {
            bool inside = false;
            for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
            {
                Point a = polygon[i], b = polygon[j];
                if ((a.Y > point.Y) != (b.Y > point.Y)
                    && point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X)
                    inside = !inside;
            }
            return inside;
        }

        internal static double SignedArea(IList<Point> points)
        {
            double area = 0;
            for (int i = 0; i < points.Count; i++)
            {
                Point a = points[i], b = points[(i + 1) % points.Count];
                area += a.X * b.Y - b.X * a.Y;
            }
            return area / 2;
        }
    }
}
