using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using SlabReinforcement.Core;

namespace SlabReinforcement.Dxf
{
    /// <summary>
    /// Read-only streaming ASCII DXF reader for model-space 3DFACE entities, including R12.
    /// Coordinates, layers, source colors and units are retained for explicit downstream selection.
    /// This reader does not infer units, project elevations, discard auxiliary layers or expand blocks.
    /// </summary>
    public static class DxfFaceSource
    {
        public static DxfSource Read(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Выберите исходный файл DXF.", nameof(path));
            if (new FileInfo(path).Length > 512L * 1024 * 1024) throw new InvalidDataException("Файл DXF больше 512 МиБ. Разделите его на части.");
            var result = new DxfSource();
            var hiddenLayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var blockFaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var unsupportedBlockMeshes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var blockReferences = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var topReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string section = null, block = null;
            bool sawEof = false;
            using (var reader = new StreamReader(path, DetectEncoding(path), true, 65536))
            {
                foreach (var record in Records(reader))
                {
                    string type = Value(record, 0);
                    if (type == "SECTION")
                    {
                        section = Value(record, 2);
                        if (section == "HEADER") ReadHeader(record, result);
                        continue;
                    }
                    if (type == "ENDSEC") { section = null; continue; }
                    if (type == "EOF") { sawEof = true; continue; }
                    if (section == "TABLES" && type == "LAYER")
                    {
                        string name = Value(record, 2) ?? throw Error(record, "В DXF найден слой без имени. Исправьте исходный файл.");
                        int raw = Integer(record, 62, 7);
                        result.LayerColors[name] = Math.Abs(raw);
                        result.LayerColorKeys[name] = Color(record, 7);
                        if (raw < 0 || (Integer(record, 70, 0) & 1) != 0) hiddenLayers.Add(name);
                        continue;
                    }
                    if (section != "ENTITIES" && section != "BLOCKS") continue;
                    if (type == "BLOCK") { block = Value(record, 2); continue; }
                    if (type == "ENDBLK") { block = null; continue; }
                    if (section == "ENTITIES")
                        result.EntityCounts[type] = result.EntityCounts.TryGetValue(type, out int count) ? count + 1 : 1;
                    bool unsupportedMesh = type == "MESH" || type == "POLYLINE" && (Integer(record, 70, 0) & (16 | 64)) != 0;
                    if (unsupportedMesh && Integer(record, 67, 0) == 0)
                    {
                        if (section == "ENTITIES")
                            throw new NotSupportedException("DXF: сетка " + type + " не поддерживается. Преобразуйте сетки в отдельные 3DFACE в пространстве модели.");
                        if (block != null) unsupportedBlockMeshes.Add(block);
                    }
                    if (type == "INSERT" && Integer(record, 67, 0) == 0)
                    {
                        string reference = Value(record, 2) ?? throw Error(record, "Вставка в DXF не содержит имени блока. Исправьте исходный файл.");
                        if (section == "ENTITIES") topReferences.Add(reference);
                        else if (block != null)
                        {
                            if (!blockReferences.TryGetValue(block, out var refs))
                                blockReferences[block] = refs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            refs.Add(reference);
                        }
                    }
                    if (type != "3DFACE") continue;
                    if (section == "BLOCKS")
                    {
                        result.BlockFaceCount++;
                        if (block != null) blockFaces.Add(block);
                        continue;
                    }
                    if (Integer(record, 67, 0) != 0) { result.IgnoredPaperSpaceFaceCount++; continue; }
                    if (result.Faces.Count >= 1000000) throw new InvalidDataException("В DXF больше 1 000 000 поверхностей 3DFACE. Разделите файл на части.");
                    var face = new DxfSourceFace
                    {
                        Id = Value(record, 5) ?? "face-" + (result.Faces.Count + 1).ToString(CultureInfo.InvariantCulture),
                        Layer = Value(record, 8) ?? "0",
                        OriginalColorKey = Color(record, 256),
                        IsVisible = Integer(record, 60, 0) == 0
                    };
                    for (int i = 0; i < 3; i++) face.Vertices.Add(Vertex(record, i));
                    bool fourthPresent = Value(record, 13) != null || Value(record, 23) != null || Value(record, 33) != null;
                    face.Vertices.Add(fourthPresent ? Vertex(record, 3) : face.Vertices[2]);
                    result.Faces.Add(face);
                }
            }
            if (!sawEof) throw new InvalidDataException("Файл DXF повреждён. Экспортируйте его заново.");
            foreach (string reference in topReferences)
            {
                if (ContainsFaces(reference, blockFaces, blockReferences, new HashSet<string>(StringComparer.OrdinalIgnoreCase)))
                    throw new NotSupportedException("Блок «" + reference + "»: разбейте вставки блока с 3DFACE, сохранив положение поверхностей.");
                if (ContainsFaces(reference, unsupportedBlockMeshes, blockReferences, new HashSet<string>(StringComparer.OrdinalIgnoreCase)))
                    throw new NotSupportedException("Блок «" + reference + "»: преобразуйте MESH/POLYLINE в отдельные 3DFACE в пространстве модели.");
            }
            foreach (var face in result.Faces)
            {
                face.ColorKey = face.OriginalColorKey;
                if (face.ColorKey == "ACI:256")
                {
                    if (result.LayerColorKeys.TryGetValue(face.Layer, out string key)) face.ColorKey = key;
                    else if (face.Layer == "0") face.ColorKey = "ACI:7";
                    else throw new InvalidDataException("Поверхность «" + face.Id + "»: отсутствует слой «" + face.Layer + "». Исправьте слой или задайте цвет в DXF.");
                }
                face.IsVisible &= !hiddenLayers.Contains(face.Layer);
            }
            if (!result.InsUnits.HasValue || result.InsUnits.Value == 0)
                result.Warnings.Add("В DXF не указаны единицы длины. Выберите их при загрузке файла.");
            int unresolved = result.Faces.Count(f => f.ColorKey == "ACI:0");
            if (unresolved > 0) result.Warnings.Add(unresolved + " поверхностей: цвет «По блоку» не определён. Задайте цвет в DXF.");
            return result;
        }

        private static bool ContainsFaces(string block, HashSet<string> faces,
            Dictionary<string, HashSet<string>> references, HashSet<string> visited)
        {
            if (!visited.Add(block)) return false;
            if (faces.Contains(block)) return true;
            if (!references.TryGetValue(block, out var children)) return false;
            return children.Any(child => ContainsFaces(child, faces, references, visited));
        }

        private static void ReadHeader(List<Pair> record, DxfSource result)
        {
            string key = null;
            foreach (var pair in record)
            {
                if (pair.Code == 9) key = pair.Value;
                else if (key == "$ACADVER" && pair.Code == 1) result.Version = pair.Value;
                else if (key == "$INSUNITS" && pair.Code == 70)
                {
                    if (!int.TryParse(pair.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int unit) || unit < 0)
                        throw new InvalidDataException("В DXF неверно указаны единицы длины. Строка " + pair.Line + ".");
                    result.InsUnits = unit;
                }
            }
        }

        private static Encoding DetectEncoding(string path)
        {
            using (var stream = File.OpenRead(path))
            {
                var prefix = new byte[22];
                int read = stream.Read(prefix, 0, prefix.Length);
                if (Encoding.ASCII.GetString(prefix, 0, read).StartsWith("AutoCAD Binary DXF", StringComparison.Ordinal))
                    throw new NotSupportedException("Двоичный DXF не поддерживается. Сохраните как текстовый DXF (ASCII).");
            }
            try
            {
                using (var reader = new StreamReader(path, new UTF8Encoding(false, true), true, 65536))
                {
                    var buffer = new char[65536];
                    while (reader.Read(buffer, 0, buffer.Length) > 0) { }
                }
                return new UTF8Encoding(false, true);
            }
            catch (DecoderFallbackException)
            {
                // The supplied R12 calculation exports use legacy Cyrillic code page 1251.
                // Newer UTF-8 DXF is detected first; no drawing geometry is recoded or modified.
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                return Encoding.GetEncoding(1251, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            }
        }

        private static IEnumerable<List<Pair>> Records(TextReader reader)
        {
            var record = new List<Pair>();
            long line = 0;
            string codeText;
            while ((codeText = reader.ReadLine()) != null)
            {
                line++;
                string value = reader.ReadLine();
                line++;
                if (value == null) throw new InvalidDataException("В DXF отсутствует значение. Проверьте строку " + (line - 1) + ".");
                if (!int.TryParse(codeText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int code))
                    throw new InvalidDataException("В DXF повреждён код записи. Проверьте строку " + (line - 1) + ".");
                if (code == 0 && record.Count > 0) { yield return record; record = new List<Pair>(); }
                record.Add(new Pair(code, value.Trim(), line));
            }
            if (record.Count > 0) yield return record;
        }

        private static Point3 Vertex(List<Pair> record, int i)
        {
            return new Point3(Number(record, 10 + i, true), Number(record, 20 + i, true), Number(record, 30 + i, false));
        }

        private static double Number(List<Pair> record, int code, bool required)
        {
            string value = Value(record, code);
            if (value == null && !required) return 0;
            if (value == null || !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double result) || double.IsNaN(result) || double.IsInfinity(result))
                throw Error(record, "В DXF отсутствует или неверно записана координата, код " + code + ".");
            return result;
        }

        private static int Integer(List<Pair> record, int code, int fallback)
        {
            string value = Value(record, code);
            if (value == null) return fallback;
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
                throw Error(record, "В DXF неверно записано целое число, код " + code + ".");
            return number;
        }

        private static string Color(List<Pair> record, int defaultAci)
        {
            if (Value(record, 420) != null)
            {
                int rgb = Integer(record, 420, 0);
                if (rgb < 0 || rgb > 0xffffff) throw Error(record, "В DXF неверно записан цвет RGB. Исправьте цвет в исходном файле.");
                return "#" + rgb.ToString("X6", CultureInfo.InvariantCulture);
            }
            int aci = Math.Abs(Integer(record, 62, defaultAci));
            if (aci > 256) throw Error(record, "В DXF указан недопустимый номер цвета. Исправьте цвет в исходном файле.");
            return "ACI:" + aci.ToString(CultureInfo.InvariantCulture);
        }

        private static string Value(List<Pair> record, int code)
        {
            foreach (var pair in record) if (pair.Code == code) return pair.Value;
            return null;
        }

        private static InvalidDataException Error(List<Pair> record, string message) =>
            new InvalidDataException(message + " Строка DXF: " + (record.Count == 0 ? 0 : record[0].Line) + ".");

        private readonly struct Pair
        {
            public Pair(int code, string value, long line) { Code = code; Value = value; Line = line; }
            public int Code { get; }
            public string Value { get; }
            public long Line { get; }
        }
    }
}
