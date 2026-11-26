using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;
using Point = TYBIM_2025.AutoBuild.WallRegionGeometry.Point;
using Segment = TYBIM_2025.AutoBuild.WallRegionGeometry.Segment;

namespace TYBIM_2025.AutoBuild
{
    internal static class ColumnWallCaps
    {
        private const double Contact = 5 / 304.8;

        private static IEnumerable<Solid> Solids(GeometryElement geometry)
        {
            if (geometry == null) yield break;
            foreach (var item in geometry)
            {
                if (item is Solid solid && solid.Volume > 0) yield return solid;
                if (item is GeometryInstance instance)
                    foreach (var nested in Solids(instance.GetInstanceGeometry())) yield return nested;
            }
        }

        internal static List<List<Face>> ReadFaces(Document doc, IEnumerable<double> heights)
        {
            var columns = new FilteredElementCollector(doc).WhereElementIsNotElementType()
                .WherePasses(new ElementMulticategoryFilter(new[] { BuiltInCategory.OST_Columns, BuiltInCategory.OST_StructuralColumns }))
                .ToElements();
            var slices = heights.Select(z => new List<Face>()).ToList();
            var elevations = heights.ToList();
            foreach (var column in columns)
            {
                var box = column.get_BoundingBox(null);
                if (box == null) continue;
                var indices = Enumerable.Range(0, slices.Count).Where(i => elevations[i] >= box.Min.Z && elevations[i] <= box.Max.Z).ToList();
                if (indices.Count == 0) continue;
                var faces = Solids(column.get_Geometry(new Options())).SelectMany(s => s.Faces.Cast<Face>())
                    .OfType<PlanarFace>().Where(f => Math.Abs(f.FaceNormal.Z) < 0.95).ToList();
                foreach (int i in indices) slices[i].AddRange(faces);
            }
            return slices;
        }

        internal static List<Segment> Find(List<Segment> lines, List<List<Face>> slices, List<double> elevations, double maxWidth)
        {
            var caps = new List<Segment>();
            if (slices.Any(slice => slice.Count == 0)) return caps;
            foreach (var face in slices[0])
            {
                bool Touches(Face f, Point p, double z) =>
                    f.Project(new XYZ(p.X, p.Y, z)) is IntersectionResult hit && hit.Distance <= Contact;
                var vertices = face.EdgeLoops.Cast<EdgeArray>().SelectMany(loop => loop.Cast<Edge>())
                    .SelectMany(edge => edge.Tessellate()).ToList();
                if (vertices.Count == 0) continue;
                double xmin = vertices.Min(p => p.X) - Contact, xmax = vertices.Max(p => p.X) + Contact;
                double ymin = vertices.Min(p => p.Y) - Contact, ymax = vertices.Max(p => p.Y) + Contact;
                var ends = lines.Where(s => s.Length > Contact).SelectMany(s => new[] {
                    (p: s.A, d: (s.B - s.A) * (1 / s.Length), length: s.Length),
                    (p: s.B, d: (s.A - s.B) * (1 / s.Length), length: s.Length)
                }).Where(e => e.p.X >= xmin && e.p.X <= xmax && e.p.Y >= ymin && e.p.Y <= ymax && Touches(face, e.p, elevations[0])).ToList();
                for (int i = 0; i < ends.Count; i++)
                    foreach (var b in ends.Skip(i + 1))
                    {
                        var a = ends[i];
                        if (a.d.X * b.d.X + a.d.Y * b.d.Y < 0.99996) continue;
                        var delta = b.p - a.p;
                        double width = Math.Abs(delta.X * a.d.Y - delta.Y * a.d.X);
                        double offset = delta.X * a.d.X + delta.Y * a.d.Y;
                        double overlap = Math.Min(a.length, offset + b.length) - Math.Max(0, offset);
                        if (width < 50 / 304.8 || width > maxWidth || overlap <= width || Math.Abs(offset) > width) continue;
                        var mid = (a.p + b.p) * 0.5;
                        if (!Touches(face, mid, elevations[0])) continue;
                        if (Enumerable.Range(1, slices.Count - 1).All(j => slices[j].Any(f =>
                            Touches(f, a.p, elevations[j]) && Touches(f, b.p, elevations[j]) && Touches(f, mid, elevations[j]))))
                            caps.Add(new Segment(a.p, b.p));
                    }
            }
            return caps;
        }
    }
}

