using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using SlabReinforcement.Core;
using SlabReinforcement.Dxf;

if (args.Length < 1) throw new ArgumentException("Usage: DxfSmoke <folder> [--units-map path.json] [--output directory] [--benchmark] [--benchmark-color 'RGB(r, g, b)']");
string Option(string name) { int index = Array.IndexOf(args, name); return index >= 0 ? args[index + 1] : null; }
var sourceDirectory = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(Option("--output") ?? "artifacts/dxf-validation");
Directory.CreateDirectory(output);
var unitsPath = Option("--units-map");
var units = unitsPath == null ? new Dictionary<string, double>() : JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(unitsPath));
var reports = new List<object>();
var json = new JsonSerializerOptions { WriteIndented = true };
var benchmarkColor = Option("--benchmark-color");
bool benchmark = args.Contains("--benchmark") || benchmarkColor != null;
bool exportWritten = false;
foreach (var path in Directory.GetFiles(sourceDirectory, "*.dxf").OrderBy(Path.GetFileName, StringComparer.Ordinal))
{
    var watch = Stopwatch.StartNew();
    var source = DxfFaceSource.Read(path);
    bool unitsKnown = units.TryGetValue(Path.GetFileName(path), out double scale);
    if (!unitsKnown) scale = 1; // Coordinate-only inventory, explicitly not a millimetre plan.
    if (!double.IsFinite(scale) || scale <= 0) throw new ArgumentException("Invalid explicit unit factor.");
    var grouped = CadPlanarGroups.Group(source.Faces.Where(f => f.IsVisible).Select(f => new CadFace
    {
        Id = f.Id, Layer = f.Layer, ColorKey = Rgb(f.ColorKey),
        Vertices = f.Vertices.Select(p => new Point3(p.X * scale, p.Y * scale, p.Z * scale)).ToList()
    }), unitsKnown ? 1 : 0.000001);
    long readMs = watch.ElapsedMilliseconds;
    object planReport = null;
    var selected = grouped.Groups.OrderByDescending(g => g.Faces.Select(f => f.ColorKey).Distinct().Count())
        .ThenByDescending(g => g.Faces.Count).ThenBy(g => g.Layer, StringComparer.Ordinal).ThenBy(g => g.ElevationMm).FirstOrDefault();
    if (unitsKnown && !exportWritten)
    {
        File.WriteAllText(Path.Combine(output, "1-source.json"), JsonSerializer.Serialize(new
        {
            HostName = "Исходный DXF · " + Path.GetFileName(path),
            AxisDescription = "Координаты DXF, мм · контур плиты Revit не загружен",
            Slabs = Array.Empty<PolygonInput>(), SourceGroups = grouped.Groups,
            Warnings = new[] { "Показана исходная карта DXF. Для рабочего плана нужны контур плиты из Revit и назначения типов/шагов.",
                $"Исходные единицы подтверждены пользователем: {scale:0.###} мм на единицу DXF. Неплоских граней: {grouped.NonPlanarFaceCount}; вырожденных: {grouped.DegenerateFaceCount}." }
        }));
        exportWritten = true;
    }
    if (unitsKnown && benchmark && selected != null)
    {
        watch.Restart();
        var request = BenchmarkRequest(selected, benchmarkColor);
        var plan = ReinforcementPlanner.Build(request);
        long buildMs = watch.ElapsedMilliseconds;
        // Only an algorithm load test: the host and all bar specifications are synthetic.
        // It cannot be used as a reinforcement design for the user's slab.
        var structuralErrors = plan.Diagnostics.Where(d => new[] { "PLAN_TOO_LARGE", "INVALID_FACE", "INVALID_SLAB", "GEOMETRY_OPERATION_FAILED", "UNMAPPED_COLOR" }.Contains(d.Code)).ToList();
        planReport = new
        {
            Purpose = "ALGORITHM_LOAD_TEST_ONLY", Host = "Synthetic enclosing rectangle; not a Revit slab",
            Specification = "Synthetic equal d12/200, zero anchorage; no mapping from the drawing legend",
            selected.Layer, SourceGroupFaceCount = selected.Faces.Count, SelectedFaceCount = request.Faces.Count,
            SelectedColor = benchmarkColor ?? "ALL_COLORS", Milliseconds = buildMs,
            Bars = plan.Bars.Count, Groups = plan.Groups.Count, plan.CanCreate,
            Diagnostics = plan.Diagnostics, InputAccepted = structuralErrors.Count == 0
        };
        if (structuralErrors.Count > 0) throw new InvalidOperationException(Path.GetFileName(path) + ": " + string.Join(";", structuralErrors.Select(d => d.Code + ": " + d.Message)));
        Console.WriteLine($"BENCH {Path.GetFileName(path)} [{selected.Layer}/{benchmarkColor ?? "ALL_COLORS"}]: {request.Faces.Count} faces; {buildMs} ms; {plan.Bars.Count} bars; CanCreate={plan.CanCreate}; {string.Join(',', plan.Diagnostics.Select(d => d.Code).Distinct())}");
    }
    reports.Add(new
    {
        File = Path.GetFileName(path), Bytes = new FileInfo(path).Length, Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(),
        source.Version, source.InsUnits, ExplicitMillimetresPerUnit = unitsKnown ? (double?)scale : null,
        GroupElevationUnits = unitsKnown ? "mm" : "raw drawing units, not millimetres",
        SourceFaces = source.Faces.Count, VisibleFaces = source.Faces.Count(f => f.IsVisible),
        UnresolvedColorFaces = source.Faces.Count(f => !Rgb(f.ColorKey).StartsWith("RGB(", StringComparison.Ordinal)),
        source.IgnoredPaperSpaceFaceCount, source.BlockFaceCount, source.Warnings,
        grouped.NonPlanarFaceCount, grouped.DegenerateFaceCount, ReadAndGroupMilliseconds = readMs,
        Groups = grouped.Groups.Select(g => new { g.Layer, Elevation = g.ElevationMm, Faces = g.Faces.Count, Colors = g.Faces.Select(f => f.ColorKey).Distinct().Count() }),
        Benchmark = planReport
    });
    File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(reports, json));
    Console.WriteLine($"READ {Path.GetFileName(path)}: {source.Faces.Count} faces -> {grouped.Groups.Count} layer/plane groups; skipped nonparallel={grouped.NonPlanarFaceCount}, degenerate={grouped.DegenerateFaceCount}; {readMs} ms");
}
Console.WriteLine("Reports: " + output);

static string Rgb(string key)
{
    return DxfPalette.RgbKey(key);
}

static PlanRequest BenchmarkRequest(CadPlaneGroup group, string color)
{
    var points = group.Faces.SelectMany(f => f.Vertices).ToList();
    var selectedFaces = group.Faces.Where(f => color == null || f.ColorKey == color).ToList();
    if (selectedFaces.Count == 0) throw new ArgumentException("The selected benchmark color is absent from this source group: " + color);
    double x1 = points.Min(p => p.X) - 1000, x2 = points.Max(p => p.X) + 1000;
    double y1 = points.Min(p => p.Y) - 1000, y2 = points.Max(p => p.Y) + 1000;
    return new PlanRequest
    {
        Slabs = new() { new() { Shell = new() { new(x1, y1), new(x2, y1), new(x2, y2), new(x1, y2) } } },
        Faces = selectedFaces,
        Specifications = selectedFaces.Select(f => f.ColorKey).Distinct().Select(colorKey => new ReinforcementSpec
        {
            Key = colorKey, ColorKey = colorKey, TypeKey = "BENCHMARK_SYNTHETIC_D12", MaterialKey = "BENCHMARK_ONLY", DiameterMm = 12, SpacingMm = 200, AnchorageMm = 0
        }).ToList(),
        Options = new() { EdgeCoverMm = 0, MergeToleranceMm = 0, RoundIncrementMm = 1 }
    };
}
