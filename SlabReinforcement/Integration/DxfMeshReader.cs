using Autodesk.Revit.DB;
using SlabReinforcement.Core;
using SlabReinforcement.Dxf;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SlabReinforcement.Integration
{
    /// <summary>
    /// Original DXF metadata fallback for Revit versions which expose anonymous CAD meshes.
    /// Coordinates are converted once from explicit drawing units to feet and then placed by
    /// the import transform. Matching distinct source and actual placed mesh vertex sets in
    /// both directions rejects an incorrect unit factor, placement, or incomplete DXF.
    /// </summary>
    internal static class DxfMeshReader
    {
        public static ImportedMeshSnapshot Read(Document doc, ImportInstance import, SlabFrame frame, string path, double mmPerUnit)
        {
            if (doc == null || import == null || frame == null) throw new ArgumentNullException("Не задана плита или подложка.");
            if (double.IsNaN(mmPerUnit) || double.IsInfinity(mmPerUnit) || mmPerUnit <= 0) throw new ArgumentException("Явно задайте единицы исходного DXF.");
            var source = DxfFaceSource.Read(path);
            var visible = source.Faces.Where(f => f.IsVisible).ToList();
            if (visible.Count == 0) throw new ArgumentException("В DXF нет видимых поверхностей 3DFACE в пространстве модели.");
            var transform = import.GetTotalTransform();
            Func<Point3, XYZ> placed = point => transform.OfPoint(new XYZ(point.X * mmPerUnit / 304.8, point.Y * mmPerUnit / 304.8, point.Z * mmPerUnit / 304.8));
            var rawPlaced = visible.SelectMany(f => f.Vertices).Select(p => SlabGeometryReader.ToPoint(placed(p))).ToList();
            var actual = new List<Point3>();
            using (var options = new Options { DetailLevel = ViewDetailLevel.Fine, IncludeNonVisibleObjects = false })
                ReadPlacedMeshVertices(import.get_Geometry(options), actual, 0);
            int checkedVertices = DxfVertexAlignment.Validate(rawPlaced, actual, 0.2);
            var result = new ImportedMeshSnapshot();
            var faces = visible.Select(face => new CadFace
            {
                Id = face.Id, Layer = face.Layer, ColorKey = DxfPalette.RgbKey(face.ColorKey),
                Vertices = face.Vertices.Select(p => frame.ToLocal(SlabGeometryReader.ToPoint(placed(p)))).ToList()
            }).ToList();
            var grouped = CadPlanarGroups.Group(faces);
            foreach (var group in grouped.Groups)
            {
                int unresolved = group.Faces.Count(f => f.ColorKey.StartsWith("UNRESOLVED:", StringComparison.Ordinal));
                if (unresolved > 0)
                {
                    // A partly coloured demand map is unsafe: reject the entire layer/plane,
                    // while keeping independently valid source groups available for selection.
                    result.Warnings.Add("Карта «" + group.Layer + "», ΔZ " + group.ElevationMm.ToString("0.###", CultureInfo.CurrentCulture) + " мм исключена целиком: " + unresolved + " граней без определённого цвета (ByBlock). Задайте им цвет в исходном DXF.");
                    continue;
                }
                result.Groups.Add(group);
            }
            if (result.Groups.Count == 0) throw new ArgumentException("В DXF нет допустимых расчётных карт. " + string.Join(" ", result.Warnings));
            foreach (var face in visible)
                if (DxfPalette.TryRgb(face.ColorKey, out byte r, out byte g, out byte b))
                    result.Colors[DxfPalette.RgbKey(face.ColorKey)] = new Color(r, g, b);
            result.Warnings.Add("Слои и цвета прочитаны из исходного DXF; совпадение с размещённой подложкой проверено по " + checkedVertices + " вершинам (допуск 0,2 мм). Масштаб: " + mmPerUnit.ToString("0.###", CultureInfo.CurrentCulture) + " мм/ед.");
            if (grouped.NonPlanarFaceCount > 0 || grouped.DegenerateFaceCount > 0)
                result.Warnings.Add("Вне параллельных расчётных плоскостей: " + grouped.NonPlanarFaceCount + "; вырожденных граней: " + grouped.DegenerateFaceCount + ".");
            return result;
        }

        private static void ReadPlacedMeshVertices(GeometryElement geometry, List<Point3> vertices, int depth)
        {
            if (geometry == null) throw new ArgumentException("Нет геометрии подложки для проверки DXF.");
            if (depth > 32) throw new ArgumentException("Слишком большая вложенность подложки.");
            foreach (GeometryObject item in geometry)
            {
                if (item is GeometryInstance instance)
                    using (var placed = instance.GetInstanceGeometry()) ReadPlacedMeshVertices(placed, vertices, depth + 1);
                else if (item is Mesh mesh)
                    foreach (var vertex in mesh.Vertices)
                    {
                        vertices.Add(SlabGeometryReader.ToPoint(vertex));
                        if (vertices.Count > 4000000) throw new ArgumentException("Слишком много вершин подложки для проверки исходного DXF.");
                    }
            }
        }
    }
}
