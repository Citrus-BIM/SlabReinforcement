using Autodesk.Revit.DB;
using SlabReinforcement.Core;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SlabReinforcement.Integration
{
    internal sealed class CadMetadataUnavailableException : ArgumentException
    {
        public CadMetadataUnavailableException(string message) : base(message) { }
    }
    internal sealed class ImportedMeshSnapshot
    {
        public List<CadPlaneGroup> Groups { get; set; } = new List<CadPlaneGroup>();
        public Dictionary<string, Color> Colors { get; } = new Dictionary<string, Color>();
        public List<string> Warnings { get; } = new List<string>();
    }

    internal static class ImportedMeshReader
    {
        // Import geometry is already in internal feet with the instance placement applied.
        // Do not multiply GetTotalTransform or a DXF unit factor into it a second time.
        public static ImportedMeshSnapshot Read(Document doc, ImportInstance import, SlabFrame frame)
        {
            var result = new ImportedMeshSnapshot();
            var sourceFaces = new List<CadFace>();
            int fallbackColors = 0;
            using (var options = new Options { DetailLevel = ViewDetailLevel.Fine, IncludeNonVisibleObjects = false })
            {
                var geometry = import.get_Geometry(options);
                if (geometry == null) throw new ArgumentException("Не удалось прочитать подложку. Проверьте файл и связь с проектом.");
                ReadGeometry(geometry, doc, frame, result, sourceFaces, ref fallbackColors, 0, ElementId.InvalidElementId);
            }
            if (sourceFaces.Count == 0)
                throw new ArgumentException("Нет цветных расчётных поверхностей. Нужна сетка или элементы 3DFACE.");
            var grouped = CadPlanarGroups.Group(sourceFaces);
            result.Groups = grouped.Groups;
            if (result.Groups.Count == 0)
                throw new ArgumentException("Нет подходящих поверхностей. Проверьте форму и положение карты.");
            if (grouped.NonPlanarFaceCount > 0 || grouped.DegenerateFaceCount > 0)
                result.Warnings.Add($"Пропущены поверхности: наклонные — {grouped.NonPlanarFaceCount}, нулевые — {grouped.DegenerateFaceCount}.");
            if (result.Groups.Count > 1)
                result.Warnings.Add($"Найдено карт: {result.Groups.Count}. Выберите слой и высоту.");
            if (fallbackColors > 0)
                result.Warnings.Add("Для " + fallbackColors + " сеток взят цвет слоя. Сверьте с легендой; при расхождении импортируйте с сохранением цветов.");
            return result;
        }

        private static void ReadGeometry(GeometryElement geometry, Document doc, SlabFrame frame,
            ImportedMeshSnapshot result, List<CadFace> sourceFaces, ref int fallbackColors, int depth, ElementId inheritedStyle)
        {
            if (depth > 32) throw new ArgumentException("Слишком много вложенных блоков. Разбейте их в исходном файле и обновите подложку.");
            foreach (GeometryObject item in geometry)
            {
                var style = item.GraphicsStyleId;
                if (style == null || style.Equals(ElementId.InvalidElementId)) style = inheritedStyle;
                if (item is GeometryInstance instance)
                {
                    using (var placed = instance.GetInstanceGeometry())
                        ReadGeometry(placed, doc, frame, result, sourceFaces, ref fallbackColors, depth + 1, style);
                }
                else if (item is Mesh mesh)
                    ReadMesh(mesh, mesh.MaterialElementId, style, doc, frame, result, sourceFaces, ref fallbackColors);
                else if (item is Solid solid)
                    foreach (Face face in solid.Faces)
                    {
                        if (!(face is PlanarFace)) throw new ArgumentException("В подложке есть изогнутые поверхности. Нужна плоская расчётная карта.");
                        using (var faceMesh = face.Triangulate())
                            ReadMesh(faceMesh, face.MaterialElementId,
                                face.GraphicsStyleId == null || face.GraphicsStyleId.Equals(ElementId.InvalidElementId) ? style : face.GraphicsStyleId,
                                doc, frame, result, sourceFaces, ref fallbackColors);
                    }
            }
        }

        private static void ReadMesh(Mesh mesh, ElementId materialId, ElementId styleId, Document doc,
            SlabFrame frame, ImportedMeshSnapshot result, List<CadFace> sourceFaces, ref int fallbackColors)
        {
            if (mesh.NumTriangles == 0) return;
            var layerCategory = (doc.GetElement(styleId) as GraphicsStyle)?.GraphicsStyleCategory;
            // A root import category is not an identifiable CAD layer. If the
            // layer was lost, do not silently combine all materials at one Z.
            if (layerCategory == null || layerCategory.Parent == null || string.IsNullOrWhiteSpace(layerCategory.Name))
                throw new CadMetadataUnavailableException("Не определён слой сетки. Проверьте слои исходного файла и обновите подложку.");
            Color color = (doc.GetElement(materialId) as Material)?.Color;
            if (color == null || !color.IsValid)
            {
                color = layerCategory.LineColor;
                fallbackColors++;
            }
            if (color == null || !color.IsValid)
                throw new CadMetadataUnavailableException("Не удалось определить цвет расчётной сетки. Импортируйте файл с сохранением цветов.");
            string key = $"RGB({color.Red}, {color.Green}, {color.Blue})";
            string layer = layerCategory.Name;
            result.Colors[key] = color;
            for (int i = 0; i < mesh.NumTriangles; i++)
            {
                if (sourceFaces.Count >= 1000000) throw new ArgumentException("В подложке больше 1 000 000 треугольников. Разделите файл на части.");
                var triangle = mesh.get_Triangle(i);
                var points = Enumerable.Range(0, 3).Select(v => frame.ToLocal(SlabGeometryReader.ToPoint(triangle.get_Vertex(v)))).ToList();
                sourceFaces.Add(new CadFace
                {
                    Id = (sourceFaces.Count + 1).ToString(CultureInfo.InvariantCulture), ColorKey = key, Layer = layer,
                    Vertices = points
                });
            }
        }
    }
}
