using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace SlabReinforcement.PluginUi2025
{
    public sealed class BootstrapApplication : IExternalApplication
    {
        internal static readonly string Run = Path.GetDirectoryName(typeof(BootstrapApplication).Assembly.Location);
        public Result OnStartup(UIControlledApplication app)
        {
            app.Idling += Open; app.DialogBoxShowing += Dialog;
            Log("STARTUP " + app.ControlledApplication.VersionNumber + " PID=" + Process.GetCurrentProcess().Id);
            return Result.Succeeded;
        }
        public Result OnShutdown(UIControlledApplication app) { Log("SHUTDOWN"); return Result.Succeeded; }
        private void Dialog(object sender, DialogBoxShowingEventArgs e) => Log("DIALOG " + e.DialogId + " " + (e is TaskDialogShowingEventArgs t ? t.Message : ""));
        private void Open(object sender, IdlingEventArgs e)
        {
            var ui = (UIApplication)sender; ui.Idling -= Open;
            try
            {
                string model = File.ReadAllText(Path.Combine(Run, "model-path.txt")).Trim();
                var doc = ui.OpenAndActivateDocument(model).Document;
                Write("ready.json", new { status = "READY_FOR_REAL_PLUGIN_UI", processId = Process.GetCurrentProcess().Id,
                    model = doc.PathName, activeView = doc.ActiveView.Name, counts = AuditCommand.Counts(doc), modified = doc.IsModified,
                    productExecuted = false, writeTransactions = 0, readyUtc = DateTime.UtcNow.ToString("O") });
                Log("READY; actual product command must be invoked from Revit UI.");
            }
            catch (Exception error) { File.WriteAllText(Path.Combine(Run, "failed.txt"), error.ToString()); Log("FAILED " + error); }
        }
        internal static void Write(string name, object value) => File.WriteAllText(Path.Combine(Run, name), JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        internal static void Log(string value) => File.AppendAllText(Path.Combine(Run, "bootstrap.log"), DateTime.UtcNow.ToString("O") + " " + value + Environment.NewLine);
    }

    [Autodesk.Revit.Attributes.Transaction(Autodesk.Revit.Attributes.TransactionMode.ReadOnly)]
    public sealed class AuditCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var doc = data.Application.ActiveUIDocument?.Document;
            if (doc == null) { message = "No active test model."; return Result.Failed; }
            try
            {
                bool modified = doc.IsModified;
                var bars = new List<object>(); var sets = new List<object>(); var issues = new List<string>();
                var canonical = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                double maxGap = 0;
                foreach (var element in new FilteredElementCollector(doc).WhereElementIsNotElementType().Where(x => x is Rebar || x is RebarInSystem))
                {
                    var rebar = element as Rebar; var ris = element as RebarInSystem;
                    var type = (RebarBarType)doc.GetElement(element.GetTypeId());
                    int positions = rebar?.NumberOfBarPositions ?? ris.NumberOfBarPositions;
                    int quantity = rebar?.Quantity ?? ris.Quantity;
                    string layout = (rebar?.LayoutRule ?? ris.LayoutRule).ToString();
                    XYZ normal = ris?.Normal;
                    if (rebar != null && rebar.IsRebarShapeDriven()) using (var accessor = rebar.GetShapeDrivenAccessor()) normal = accessor.Normal;
                    var stations = new List<double>(); int physical = 0;
                    for (int i = 0; i < positions; i++)
                    {
                        if (!(rebar?.DoesBarExistAtPosition(i) ?? ris.DoesBarExistAtPosition(i))) continue;
                        if (bars.Count >= 200000) throw new InvalidOperationException("Physical bar limit exceeded.");
                        IList<Curve> curves = rebar != null
                            ? rebar.GetTransformedCenterlineCurves(true, false, false, MultiplanarOption.IncludeAllMultiplanarCurves, i)
                            : ris.GetTransformedCenterlineCurves(true, false, false, i);
                        if (curves.Count == 0) throw new InvalidOperationException("Empty bar centreline.");
                        string identity = element.Id.Value + ":" + i;
                        string key = type.BarModelDiameter.ToString("F9", CultureInfo.InvariantCulture) + "|" + string.Join("|", curves.Select(Canonical).OrderBy(x => x, StringComparer.Ordinal));
                        if (!canonical.TryGetValue(key, out var owners)) canonical[key] = owners = new List<string>();
                        owners.Add(identity);
                        if (normal != null) stations.Add(curves[0].GetEndPoint(0).DotProduct(normal) * 304.8);
                        string shape = curves.Count == 1 && curves[0] is Line ? "Straight"
                            : curves.OfType<Line>().Count() == 3 && curves.OfType<Arc>().Count() == 2 ? "ThreeLinesTwoArcs" : "Other";
                        bars.Add(new { elementId = element.Id.Value, position = i, elementClass = element.GetType().Name,
                            systemId = ris?.SystemId.Value, hostId = (rebar?.GetHostId() ?? ris.GetHostId()).Value,
                            typeId = type.Id.Value, typeName = type.Name, nominalDiameterMm = type.BarNominalDiameter * 304.8,
                            modelDiameterMm = type.BarModelDiameter * 304.8, layout, shape, curves = curves.Select(CurveData).ToList() });
                        physical++;
                    }
                    stations.Sort(); double? gap = normal == null ? null : stations.Zip(stations.Skip(1), (a, b) => b - a).DefaultIfEmpty(0).Max();
                    if (gap.HasValue) maxGap = Math.Max(maxGap, gap.Value);
                    if (physical != quantity) issues.Add("Quantity mismatch for " + element.Id.Value);
                    if (layout != "MaximumSpacing" && layout != "Single") issues.Add("Unexpected layout " + layout + " for " + element.Id.Value);
                    if (gap > 200.001) issues.Add("Actual gap exceeds test 200 mm: " + element.Id.Value + " " + gap);
                    sets.Add(new { id = element.Id.Value, elementClass = element.GetType().Name, physical, quantity, positions,
                        layout, maxActualGapMm = gap, distributionDirection = normal == null ? null : new[] { normal.X, normal.Y, normal.Z },
                        partition = element.get_Parameter(BuiltInParameter.NUMBER_PARTITION_PARAM)?.AsString(),
                        comments = element.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() });
                }
                var duplicates = canonical.Values.Where(x => x.Count > 1).ToList();
                if (duplicates.Count != 0) issues.Add("Duplicate complete centreline groups: " + duplicates.Count);
                if (doc.IsModified != modified) throw new InvalidOperationException("Read-only audit changed document modified state.");
                string diskHash = null, diskHashError = null;
                try { using var stream = new FileStream(doc.PathName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); diskHash = Convert.ToHexString(SHA256.HashData(stream)); }
                catch (Exception error) { diskHashError = error.Message; }
                string name = "audit-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + ".json";
                var productAssemblies = AppDomain.CurrentDomain.GetAssemblies().Where(a => new[] { "SlabReinforcement", "SlabReinforcement.Core", "SlabReinforcement.Dxf", "NetTopologySuite" }.Contains(a.GetName().Name))
                    .Select(a => new { name = a.GetName().Name, version = a.GetName().Version.ToString(), location = a.Location, moduleVersionId = a.ManifestModule.ModuleVersionId }).ToList();
                BootstrapApplication.Write(name, new { status = bars.Count == 0 ? "EMPTY_REINFORCEMENT" : issues.Count == 0 ? "PASS_BASIC_READBACK" : "FAIL_BASIC_READBACK",
                    model = doc.PathName, modelModified = modified, diskSha256 = diskHash, diskHashError,
                    revitVersion = data.Application.Application.VersionNumber, revitBuild = data.Application.Application.VersionBuild,
                    counts = Counts(doc), productAssemblies, physicalBars = bars.Count, sets, maxActualSetGapMm = maxGap, duplicateFullCentrelineGroups = duplicates,
                    duplicatePrecisionMm = 0.000001, issues, bars, writeTransactions = 0, saved = false,
                    scope = "Read-only actual product output inventory. No containment, structural capacity, anchorage or lap approval. Duplicate check compares complete axes rounded to 1e-6 mm; intersections are not duplicates.",
                    finishedUtc = DateTime.UtcNow.ToString("O") });
                File.WriteAllText(Path.Combine(BootstrapApplication.Run, "last-audit-path.txt"), Path.Combine(BootstrapApplication.Run, name));
                TaskDialog.Show("Проверка результата", "Прочитано " + bars.Count + " физических стержней в " + sets.Count + " наборах.\n" +
                    "Групп полных дублей: " + duplicates.Count + ". Замечаний: " + issues.Count + ".\nОтчёт: " + name + "\nМодель не изменялась.");
                return Result.Succeeded;
            }
            catch (Exception error) { message = error.Message; BootstrapApplication.Log("AUDIT FAILED " + error); return Result.Failed; }
        }
        internal static object Counts(Document doc) => new {
            floors = new FilteredElementCollector(doc).OfClass(typeof(Floor)).GetElementCount(),
            rebar = new FilteredElementCollector(doc).OfClass(typeof(Rebar)).GetElementCount(),
            rebarInSystem = new FilteredElementCollector(doc).OfClass(typeof(RebarInSystem)).GetElementCount(),
            areas = new FilteredElementCollector(doc).OfClass(typeof(AreaReinforcement)).GetElementCount(),
            paths = new FilteredElementCollector(doc).OfClass(typeof(PathReinforcement)).GetElementCount() };
        private static double[] Mm(XYZ p) => new[] { p.X * 304.8, p.Y * 304.8, p.Z * 304.8 };
        private static object CurveData(Curve c) => new { kind = c.GetType().Name, start = Mm(c.GetEndPoint(0)), end = Mm(c.GetEndPoint(1)), midpoint = Mm(c.Evaluate(.5, true)),
            lengthMm = c.Length * 304.8, radiusMm = c is Arc arc ? (double?)(arc.Radius * 304.8) : null };
        private static string PointKey(XYZ p) => string.Join(",", Mm(p).Select(v => Math.Round(v, 6).ToString("F6", CultureInfo.InvariantCulture)));
        private static string Canonical(Curve c)
        {
            var ends = new[] { PointKey(c.GetEndPoint(0)), PointKey(c.GetEndPoint(1)) }.OrderBy(x => x, StringComparer.Ordinal).ToArray();
            return c.GetType().Name + ":" + ends[0] + ";" + PointKey(c.Evaluate(.5, true)) + ";" + ends[1];
        }
    }
}
