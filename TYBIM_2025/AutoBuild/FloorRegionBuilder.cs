using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;

namespace TYBIM_2025.AutoBuild
{
    /// <summary>Combines member cross-sections at a common elevation before finding slab bays.</summary>
    internal static class FloorRegionBuilder
    {
        private const double GeometryTolerance = 1e-6;

        internal static List<CurveLoop> GetProfiles(IList<Element> elements, Level level,
            List<string> diagnostics)
        {
            // Bounding boxes and geometry use project coordinates, not shared level elevations.
            double levelZ = level.ProjectElevation;
            const double levelTolerance = 1.0;
            double wallTolerance = UnitUtils.ConvertToInternalUnits(1, UnitTypeId.Millimeters);
            double wallSectionZ = levelZ + UnitUtils.ConvertToInternalUnits(25, UnitTypeId.Millimeters);
            var allLevels = new FilteredElementCollector(level.Document).OfClass(typeof(Level))
                .Cast<Level>().OrderBy(l => l.ProjectElevation).ThenBy(l => l.Id.Value).ToList();
            var elevations = allLevels.Select(l => l.ProjectElevation).ToList();
            var members = elements.Where(element =>
            {
                BoundingBoxXYZ bounds = element.get_BoundingBox(null);
                if (bounds == null) return false;
                if (IsBeam(element))
                {
                    Parameter reference = element.get_Parameter(BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM);
                    ElementId referenceId = reference != null && reference.StorageType == StorageType.ElementId
                        ? reference.AsElementId() : element.LevelId;
                    int referenceIndex = allLevels.FindIndex(l => l.Id == referenceId);
                    int assigned = FloorLevelMembership.FindLevelIndex(bounds.Max.Z,
                        elevations, referenceIndex, levelTolerance);
                    return assigned >= 0 && allLevels[assigned].Id == level.Id;
                }
                if (element is Wall)
                    return FloorLevelMembership.WallExtendsAboveLevel(bounds.Min.Z, bounds.Max.Z, levelZ, wallTolerance);
                return FloorLevelMembership.IntersectsLevel(
                    bounds.Min.Z, bounds.Max.Z, levelZ, levelTolerance);
            }).ToList();

            bool hasWalls = members.Any(member => member is Wall);
            if (!hasWalls && !members.Any(IsBeam)) return new List<CurveLoop>();

            int geometryFailures = 0, unionFailures = 0, sections = 0;
            var footprints = new FloorFootprintGeometry();
            BoundingBoxXYZ firstBounds = members.FirstOrDefault()?.get_BoundingBox(null);
            double originX = firstBounds == null ? 0 : firstBounds.Min.X;
            double originY = firstBounds == null ? 0 : firstBounds.Min.Y;
            var errors = new List<string>();
            var uncertainBounds = new List<System.Windows.Rect>();
            double retryOffset = UnitUtils.ConvertToInternalUnits(1, UnitTypeId.Millimeters);
            foreach (Element member in members)
            {
                bool incomplete = false;
                int memberSections = sections;
                try
                {
                    var solids = new List<Solid>();
                    GeometryElement geometry = member.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine });
                    if (geometry != null) CollectSolids(geometry, solids);

                    foreach (Solid solid in solids)
                    {
                        BoundingBoxXYZ local = solid.GetBoundingBox();
                        // Use every transformed corner: solid boxes can be rotated.
                        var corners = new List<XYZ>();
                        foreach (double x in new[] { local.Min.X, local.Max.X })
                        foreach (double y in new[] { local.Min.Y, local.Max.Y })
                        foreach (double z in new[] { local.Min.Z, local.Max.Z })
                            corners.Add(local.Transform.OfPoint(new XYZ(x, y, z)));
                        double minZ = corners.Min(p => p.Z), maxZ = corners.Max(p => p.Z);
                        if (maxZ - minZ <= GeometryTolerance) continue;
                        // Each beam gets its own section; a global average misses shallow/offset beams.
                        if (member is Wall && !FloorLevelMembership.WallExtendsAboveLevel(minZ, maxZ, levelZ, wallTolerance)) continue;
                        double cutZ = IsBeam(member) ? (minZ + maxZ) / 2.0
                            : Math.Max(minZ + (maxZ - minZ) * 0.1,
                                Math.Min(maxZ - (maxZ - minZ) * 0.1, levelZ));
                        if (hasWalls && !IsBeam(member)
                            && wallSectionZ > minZ + GeometryTolerance && wallSectionZ < maxZ - GeometryTolerance)
                            cutZ = wallSectionZ;

                        // The upper cut alone provides the footprint. Avoid a second Boolean on a thin solid.
                        IList<PlanarFace> faces = GetSectionFaces(solid, cutZ, minZ, maxZ, retryOffset, out string reason);
                        if (faces.Count == 0)
                        {
                            incomplete = true;
                            geometryFailures++;
                            if (errors.Count < 3) errors.Add("元件 " + member.Id.Value + "：" + reason);
                        }
                        foreach (PlanarFace top in faces)
                        {
                            var loops = new List<IList<System.Windows.Point>>();
                            foreach (CurveLoop loop in top.GetEdgesAsCurveLoops())
                            {
                                var points = new List<System.Windows.Point>();
                                foreach (Curve curve in loop)
                                foreach (XYZ point in curve.Tessellate())
                                {
                                    var projected = new System.Windows.Point(point.X - originX, point.Y - originY);
                                    if (points.Count == 0 || (projected - points[points.Count - 1]).Length > GeometryTolerance)
                                        points.Add(projected);
                                }
                                if (points.Count > 1 && (points[0] - points[points.Count - 1]).Length <= GeometryTolerance)
                                    points.RemoveAt(points.Count - 1);
                                loops.Add(points);
                            }
                            try { footprints.Add(loops); }
                            catch (Exception ex)
                            {
                                incomplete = true;
                                unionFailures++;
                                if (errors.Count < 3) errors.Add("元件 " + member.Id.Value + " 輪廓合併：" + ex.Message);
                                continue;
                            }
                            sections++;
                        }
                    }
                }
                catch (Autodesk.Revit.Exceptions.RegenerationFailedException) { throw; }
                catch (Exception ex) { incomplete = true; geometryFailures++; if (errors.Count < 3) errors.Add("元件 " + member.Id.Value + "：" + ex.Message); }
                if (sections == memberSections && !incomplete)
                {
                    incomplete = true;
                    geometryFailures++;
                    if (errors.Count < 3) errors.Add("元件 " + member.Id.Value + "：找不到有效截面。");
                }
                if (incomplete)
                {
                    BoundingBoxXYZ bounds = member.get_BoundingBox(null);
                    uncertainBounds.Add(new System.Windows.Rect(
                        new System.Windows.Point(bounds.Min.X - originX, bounds.Min.Y - originY),
                        new System.Windows.Point(bounds.Max.X - originX, bounds.Max.Y - originY)));
                }
            }

            const double maximumGapMm = 5;
            int gapBridges = footprints.CloseSmallGaps(UnitUtils.ConvertToInternalUnits(maximumGapMm, UnitTypeId.Millimeters));
            var profiles = new List<CurveLoop>();
            int uncertainRegions = 0;
            foreach (IList<System.Windows.Point> boundary in footprints.GetInnerRegions())
            {
                if (!FloorFootprintGeometry.IsRegionClear(boundary, uncertainBounds))
                {
                    uncertainRegions++;
                    continue;
                }
                var points = boundary.Select(p => new XYZ(p.X + originX, p.Y + originY, levelZ)).ToList();
                RemoveShortEdges(points, level.Document.Application.ShortCurveTolerance);
                if (points.Count < 3) continue;
                var loop = new CurveLoop();
                for (int i = 0; i < points.Count; i++)
                    loop.Append(Line.CreateBound(points[i], points[(i + 1) % points.Count]));
                profiles.Add(loop);
            }
            diagnostics.Add(level.Name + "：樑 " + members.Count(IsBeam) + " 支、柱 "
                + members.Count(e => !(e is Wall) && !IsBeam(e)) + " 支、牆 "
                + members.Count(e => e is Wall) + " 道；有效截面 " + sections
                + " 個；幾何失敗 " + geometryFailures + " 次；聯集失敗 " + unionFailures
                + " 次；可建板封閉區域 " + profiles.Count + " 個。"
                + " 已處理 " + gapBridges + " 處不超過 " + maximumGapMm + " mm 的截面間隙。"
                + (uncertainRegions > 0 ? " 因邊界無法確認，略過 " + uncertainRegions + " 個區域。" : "")
                + (errors.Count > 0 ? "\n" + string.Join("\n", errors) : ""));
            return profiles;
        }

        private static IList<PlanarFace> GetSectionFaces(Solid solid, double cutZ, double minZ,
            double maxZ, double retryOffset, out string reason)
        {
            reason = "切片未產生水平截面。";
            foreach (double z in new[] { cutZ, cutZ + retryOffset, cutZ - retryOffset })
            {
                if (z <= minZ + GeometryTolerance || z >= maxZ - GeometryTolerance) continue;
                try
                {
                    Solid section = BooleanOperationsUtils.CutWithHalfSpace(solid,
                        Plane.CreateByNormalAndOrigin(-XYZ.BasisZ, new XYZ(0, 0, z)));
                    var faces = section.Faces.Cast<Face>().OfType<PlanarFace>().Where(face =>
                        face.FaceNormal.IsAlmostEqualTo(XYZ.BasisZ)
                        && Math.Abs(face.Origin.Z - z) <= GeometryTolerance).ToList();
                    if (faces.Count > 0) return faces;
                }
                catch (Autodesk.Revit.Exceptions.InvalidOperationException ex)
                {
                    reason = ex.Message;
                }
            }
            // A straight vertical prism has an identical cap and interior section.
            var caps = new List<PlanarFace>();
            foreach (Face face in solid.Faces)
            {
                if (!(face is PlanarFace planar)) return new List<PlanarFace>();
                if (Math.Abs(planar.FaceNormal.Z) < 1e-9) continue;
                if (planar.FaceNormal.IsAlmostEqualTo(XYZ.BasisZ)
                    && Math.Abs(planar.Origin.Z - maxZ) <= GeometryTolerance) caps.Add(planar);
                else if (!planar.FaceNormal.IsAlmostEqualTo(-XYZ.BasisZ)
                    || Math.Abs(planar.Origin.Z - minZ) > GeometryTolerance) return new List<PlanarFace>();
            }
            return caps;
        }

        private static bool IsBeam(Element element)
        {
            return element.Category != null
                && element.Category.Id.Value == (long)BuiltInCategory.OST_StructuralFraming;
        }

        private static void CollectSolids(GeometryElement geometry, List<Solid> solids)
        {
            foreach (GeometryObject item in geometry)
            {
                if (item is Solid solid && solid.Volume > GeometryTolerance) solids.Add(solid);
                else if (item is GeometryInstance instance) CollectSolids(instance.GetInstanceGeometry(), solids);
            }
        }

        private static void RemoveShortEdges(List<XYZ> points, double minimumLength)
        {
            bool changed = true;
            while (changed && points.Count >= 3)
            {
                changed = false;
                for (int i = 0; i < points.Count; i++)
                    if (points[i].DistanceTo(points[(i + 1) % points.Count]) <= minimumLength)
                    { points.RemoveAt((i + 1) % points.Count); changed = true; break; }
            }
        }
    }
}
