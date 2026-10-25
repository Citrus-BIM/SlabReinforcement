using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SlabReinforcement.Core;

// Test-only UI boundary: this executable never loads Revit or runs a Revit transaction.
namespace Autodesk.Revit.DB.Structure
{
    public sealed class RebarBarType { public string Name { get; set; } }
}

namespace SlabReinforcement.UiSmoke
{
    public static class Program
    {
        private static string _outputDirectory;

        [STAThread]
        public static int Main(string[] args)
        {
            int sourceArgument = Array.IndexOf(args, "--source-json");
            if (sourceArgument >= 0 && sourceArgument + 1 >= args.Length)
                throw new ArgumentException("--source-json requires the path to a source snapshot.");
            string sourceJson = sourceArgument >= 0 && sourceArgument + 1 < args.Length ? args[sourceArgument + 1] : null;
            int zoneArgument = Array.IndexOf(args, "--real-zone-json");
            if (zoneArgument >= 0 && zoneArgument + 1 >= args.Length)
                throw new ArgumentException("--real-zone-json requires a saved B25 request/zone plan.");
            string zoneJson = zoneArgument >= 0 ? args[zoneArgument + 1] : null;
            int messageArgument = Array.IndexOf(args, "--message-review-only");
            if (messageArgument >= 0 && messageArgument + 1 >= args.Length)
                throw new ArgumentException("--message-review-only requires the saved B25 request path.");
            string messageJson = messageArgument >= 0 ? args[messageArgument + 1] : null;
            _outputDirectory = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? Path.GetFullPath(args[0]) :
                sourceJson != null ? Path.GetFullPath(Path.Combine("artifacts", "preview")) :
                Path.Combine(Path.GetTempPath(), "CITRUS", "SlabReinforcement.UiSmoke", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(_outputDirectory);
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            application.DispatcherUnhandledException += (sender, eventArgs) =>
            {
                RecordFailure(eventArgs.Exception);
                eventArgs.Handled = true;
                application.Shutdown(1);
            };
            if (sourceJson != null) return RunSourcePreview(application, sourceJson);
            if (messageJson != null) return RunSavedZonePreview(application, messageJson, false, true);
            if (zoneJson != null) return RunSavedZonePreview(application, zoneJson, args.Contains("--combined-preview"));
            if ((args.Contains("--preview-only") || args.Contains("--schematic-only")))
            {
                try { VerifyPhysicalPreview(); Console.WriteLine("PASS: every physical background/U row rendered; endpoint correspondence and physical plan preserved."); return 0; }
                catch (Exception exception) { RecordFailure(exception); return 1; }
            }
            if (args.Contains("--cover-only"))
            {
                try { VerifyCoverEditors(); Console.WriteLine("PASS: three independent host covers; validation and underlay preservation."); return 0; }
                catch (Exception exception) { RecordFailure(exception); return 1; }
            }

            var type = new Autodesk.Revit.DB.Structure.RebarBarType { Name = "Smoke type" };
            var color = new ColorStat { Color = "RGB(19, 18, 17)", ColorBrush = Brushes.SteelBlue, IsSelected = true };
            var context = CreateContext(type);
            var window = new SlabReinforcementWPF(new List<ColorStat> { color },
                new List<Autodesk.Revit.DB.Structure.RebarBarType> { type }, context)
            {
                ShowInTaskbar = false,
                ShowActivated = false,
                Left = -20000,
                Top = -20000,
                WindowStartupLocation = WindowStartupLocation.Manual
            };
            window.Loaded += async (sender, eventArgs) =>
            {
                try
                {
                    if (args.Contains("--preview-refresh-only"))
                    {
                        window.Close();
                        await VerifyBoundPreviewRefresh();
                        Console.WriteLine("PASS: bound color/type editors, blocked partial plan freshness, tab legends, and cancelled color/type worker results.");
                        application.Shutdown(0);
                        return;
                    }
                    if (args.Contains("--tab-preview-only"))
                    {
                        window.Close();
                        await VerifyTabScopedPreview();
                        Console.WriteLine("PASS: active-tab creation and real OK acceptance; independent settings/validation; tab changes recalculate; latest result wins.");
                        application.Shutdown(0);
                        return;
                    }
                    if (args.Contains("--automatic-u-length-only"))
                    {
                        window.Close();
                        await VerifyAutomaticLengthUi();
                        Console.WriteLine("PASS: automatic U length in zone-only and standalone perimeter modes; no manual leg editors.");
                        application.Shutdown(0);
                        return;
                    }
                    if (args.Contains("--diagnostics-only"))
                    {
                        window.Close();
                        await VerifyDiagnosticLabels();
                        await VerifyDiagnosticPresentation();
                        Console.WriteLine("PASS: essential/unknown/blocking diagnostics remain visible; routine details are collapsed and cleared on edits/tab changes; source warnings and labels are preserved.");
                        application.Shutdown(0);
                        return;
                    }
                    if (args.Contains("--layout-only"))
                    {
                        window.Close();
                        await VerifyPropertyLayout();
                        Console.WriteLine("PASS: common and zone properties, independent automatic zone endings, and constrained tables at 1280/1080/1024.");
                        application.Shutdown(0);
                        return;
                    }
                    if (args.Contains("--cancel-only"))
                    {
                        window.Close();
                        await VerifyCancellation();
                        Console.WriteLine("PASS: Cancel button and Escape close initial, reopened and modeless windows, including invalid and calculating states.");
                        application.Shutdown(0);
                        return;
                    }
                    if (args.Contains("--perimeter-spacing-only"))
                    {
                        window.Close();
                        await VerifyPerimeterSpacingModes();
                        Console.WriteLine("PASS: manual, partial and full lower-background perimeter spacing; existing rows, invalid values and 1024 layout.");
                        application.Shutdown(0);
                        return;
                    }
                    if (args.Contains("--components-only"))
                    {
                        window.Close();
                        await VerifyComponentsWithoutCad();
                        Console.WriteLine("PASS: component controls and manual perimeter spacing; mock calculation only.");
                        application.Shutdown(0);
                        return;
                    }
                    await VerifyWindow(window, color, type);
                    await VerifyTabScopedPreview();
                    await VerifyBoundPreviewRefresh();
                    await VerifyCancellation();
                    await VerifyPropertyLayout();
                    await VerifyAutomaticLengthUi();
                    await VerifyDiagnosticLabels();
                    await VerifyDiagnosticPresentation();
                    await VerifySourceGroupSelection();
                    await VerifyComponentsWithoutCad();
                    await VerifyPerimeterSpacingModes();
                    await VerifyRealPlannerFixture();
                    await VerifyRealMvpFixture();
                    VerifyPhysicalPreview();
                    VerifyCoverEditors();
                    string passed = "PASS: real WPF XAML/resources loaded; latest calculation wins; invalid/missing inputs disable creation; " +
                        "corrected inputs recalculate; Enter/Space do not accept; 1280/1080/1024 window widths render; " +
                        "10,000 bars use one canvas; closing cancels pending UI updates; source layer/elevation switches faces and colors without stale plans; " +
                        "source and direction selection and numeric areas are validated; active-tab creation and independent preferences are verified; background/perimeter work without CAD; " +
                        "real zone and four-layer background/perimeter planner fixtures are creatable and rendered.";
                    File.WriteAllText(Path.Combine(_outputDirectory, "passed.txt"), passed);
                    Console.WriteLine(passed);
                    Console.WriteLine("Artifacts: " + _outputDirectory);
                    application.Shutdown(0);
                }
                catch (Exception exception)
                {
                    RecordFailure(exception);
                    application.Shutdown(1);
                }
            };
            return application.Run(window);
        }

        private static int RunSavedZonePreview(Application application, string savedJson, bool combinedPreview, bool recalculateMessages = false)
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            options.Converters.Add(new Point2JsonConverter());
            options.Converters.Add(new Point3JsonConverter());
            using var document = JsonDocument.Parse(File.ReadAllText(savedJson));
            var request = document.RootElement.GetProperty("request").Deserialize<PlanRequest>(options);
            var calculationTime = Stopwatch.StartNew();
            var plan = recalculateMessages ? ReinforcementPlanner.Build(request)
                : document.RootElement.GetProperty("zone").Deserialize<ReinforcementPlan>(options);
            calculationTime.Stop();
            Expect(request.Faces.Count == 3752 && plan.Bars.Count == 621 && plan.Groups.Count == 67,
                "This diagnostic mode expects the reviewed B25 upper-left slab snapshot.");
            var specifications = request.Specifications.ToDictionary(spec => spec.Key);
            BarPlacement Placement(string key)
            {
                var spec = specifications[key];
                return new BarPlacement { Component = "Zones", TypeKey = spec.TypeKey,
                    ModelDiameterMm = spec.EffectiveModelDiameterMm, ElevationMm = spec.ElevationMm.Value,
                    Distribution = request.Options.AlongX ? new Point3(0, 1, 0) : new Point3(1, 0, 0) };
            }
            foreach (PlannedBar bar in plan.Bars) bar.Placement = Placement(bar.SpecKey);
            foreach (BarGroup group in plan.Groups) group.Placement = Placement(group.SpecKey);
            ReinforcementPlan localEnds = plan.AutomaticZoneEndPlan;
            Expect(localEnds != null && localEnds.Bars.Count == 31 && localEnds.Groups.Count == 6
                && localEnds.Bars.All(bar => bar.Centerline.Any(segment => segment.Start.Z == -159)),
                "Local U paths must retain their physical 3D coordinates when loaded.");
            // The saved artifact predates provenance flags. Mark only this known local-U subplan.
            foreach (PlannedBar bar in localEnds.Bars) bar.PreviewPurpose = PreviewPurpose.Zones;
            foreach (BarGroup group in localEnds.Groups) group.PreviewPurpose = PreviewPurpose.Zones;
            plan.Bars.AddRange(localEnds.Bars); plan.Groups.AddRange(localEnds.Groups);
            plan.PerimeterUEdges.AddRange(localEnds.PerimeterUEdges);
            plan.Diagnostics.AddRange(localEnds.Diagnostics);
            Expect(plan.CanCreate && plan.Bars.Count == 652 && plan.Groups.Sum(group => group.Count) == 652,
                "The saved screenshot plan must contain only 621 zone rows and their 31 local U endings.");
            if (recalculateMessages)
            {
                Expect(plan.Diagnostics.Single(d => d.Code == "SINGLE_ROW_ZONES_SKIPPED").Message.Contains("23")
                    && plan.Diagnostics.Single(d => d.Code == "ZONE_ENDS_IN_PERIMETER_U").Message.Contains("31"),
                    "The current B25 calculation must retain 23 excluded singleton rows and 31 U-connected ends.");
            }
            var zoneReplay = PreviewProjection.Select(plan, PreviewScope.Zones);
            var backgroundReplay = new ReinforcementPlan();
            int backgroundCount = 0, globalPerimeterCount = 0;
            if (combinedPreview)
            {
                var background = BackgroundPlanner.Build(new BackgroundRequest { Slabs = request.Slabs, ThicknessMm = 200,
                    TopCoverMm = 35, BottomCoverMm = 35, SideCoverMm = 35.05,
                    Layers = new[] { "Низ X", "Низ Y", "Верх X", "Верх Y" }.Select(direction => new BackgroundLayer
                    {
                        Key = "Background|" + direction, TypeKey = "12", DiameterMm = 12, ModelDiameterMm = 12, SpacingMm = 200,
                        Top = direction.StartsWith("Верх", StringComparison.Ordinal), AlongX = direction.EndsWith("X", StringComparison.Ordinal)
                    }).ToList() });
                var mainP = document.RootElement.GetProperty("mainP").Deserialize<ReinforcementPlan>(options);
                Expect(background.CanCreate && mainP.CanCreate && background.Bars.Select(bar => bar.SpecKey).Distinct().Count() == 4 && mainP.Bars.Count == 820,
                    "Combined replay must retain the actual host's four background layers and saved global P plan.");
                backgroundCount = background.Bars.Count; globalPerimeterCount = mainP.Bars.Count;
                foreach (ReinforcementPlan component in new[] { background, mainP })
                {
                    foreach (PlannedBar bar in component.Bars) bar.PreviewPurpose = PreviewPurpose.Background;
                    foreach (BarGroup group in component.Groups) group.PreviewPurpose = PreviewPurpose.Background;
                    plan.Bars.AddRange(component.Bars); plan.Groups.AddRange(component.Groups);
                    plan.PerimeterUEdges.AddRange(component.PerimeterUEdges); plan.Diagnostics.AddRange(component.Diagnostics);
                    backgroundReplay.Bars.AddRange(component.Bars); backgroundReplay.Groups.AddRange(component.Groups);
                    backgroundReplay.PerimeterUEdges.AddRange(component.PerimeterUEdges); backgroundReplay.Diagnostics.AddRange(component.Diagnostics);
                }
            }

            DirectoryInfo repository = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(savedJson)));
            while (repository != null && !File.Exists(Path.Combine(repository.FullName, "SlabReinforcement.sln"))) repository = repository.Parent;
            Expect(repository != null, "Cannot locate the source snapshot beside the saved plan.");
            using var source = JsonDocument.Parse(File.ReadAllText(Path.Combine(repository.FullName, "artifacts", "dxf-validation", "1-source.json")));
            JsonElement sourceGroup = source.RootElement.GetProperty("SourceGroups").EnumerateArray()
                .Single(group => group.GetProperty("Layer").GetString() == "KLEENKA");
            var faces = sourceGroup.GetProperty("Faces").EnumerateArray().Where(face =>
            {
                int number = int.Parse(face.GetProperty("Id").GetString().Substring(5));
                return number >= 52299 && number <= 66866;
            }).Select(face => new FaceInput { Id = face.GetProperty("Id").GetString(), ColorKey = face.GetProperty("ColorKey").GetString(),
                Vertices = face.GetProperty("Vertices").EnumerateArray().Select(point => new Point2(
                    5256.6 - point.GetProperty("X").GetDouble(), 18078.3 - point.GetProperty("Y").GetDouble())).ToList() }).ToList();
            Expect(faces.Count == 14568 && faces.Count(face => face.ColorKey == "RGB(255, 255, 255)") == 10816,
                "The visible underlay must be exactly the user's upper-left slab crop, including unselected white faces.");
            var selectedIds = new HashSet<string>(request.Faces.Select(face => face.Id));
            Expect(faces.Where(face => face.ColorKey != "RGB(255, 255, 255)").All(face => selectedIds.Contains(face.Id)),
                "The selected colors must correspond exactly to the saved 3,752-face request.");
            var types = request.Specifications.GroupBy(spec => spec.TypeKey).ToDictionary(group => group.Key,
                group => new Autodesk.Revit.DB.Structure.RebarBarType { Name = "ø" + group.First().DiameterMm + " " + group.First().MaterialKey });
            var colors = faces.Select(face => face.ColorKey).Distinct().OrderBy(key => key, StringComparer.Ordinal)
                .Select(key => new ColorStat { Color = key, ColorBrush = ReadColor(key) }).ToList();
            var context = new PreviewContext { HostName = "1 Слой · верхняя левая плита · 200 мм", TopCoverMm = 35, BottomCoverMm = 35, SideCoverMm = 35,
                Slabs = request.Slabs, SourceGroups = new List<CadPlaneGroup> { new CadPlaneGroup { Key = "upper-left-KLEENKA", Layer = "KLEENKA", ElevationMm = 0, Faces = faces } },
                RebarTypeKeys = types.ToDictionary(pair => pair.Value.Name, pair => pair.Key) };
            PreviewParameters captured = null;
            context.Calculate = (parameters, token) =>
            {
                token.ThrowIfCancellationRequested();
                Volatile.Write(ref captured, parameters);
                return parameters.CreationScope == ReinforcementCreationScope.Zones ? zoneReplay : backgroundReplay;
            };
            var settings = new SlabReinforcementSettings { ConcreteClass = "B25", SelectedReinforcementDirection = "Низ X",
                ZoneMergeTolerance = "600", RoundIncrement = "10", AutomaticZoneEnds = true };
            var window = new SlabReinforcementWPF(colors, types.Values.ToList(), context, settings)
            { ShowInTaskbar = false, ShowActivated = false, Left = -20000, Top = -20000, WindowStartupLocation = WindowStartupLocation.Manual };
            ResetComponents(window);
            foreach (BackgroundLayerStat layer in ((DataGrid)window.FindName("BackgroundDataGrid")).Items) layer.Enabled = combinedPreview;
            ((CheckBox)window.FindName("CheckBox_PerimeterEnabled")).IsChecked = combinedPreview;
            foreach (ColorStat color in colors)
            {
                ReinforcementSpec spec = request.Specifications.SingleOrDefault(item => item.ColorKey == color.Color);
                color.IsSelected = spec != null;
                if (spec != null) { color.SelectedRebarType = types[spec.TypeKey]; color.SpacingText = spec.SpacingMm.ToString(System.Globalization.CultureInfo.InvariantCulture); }
            }
            ((TextBlock)window.FindName("UnderlayName")).Text = "1 Слой — верхняя левая плита.dxf";
            window.Loaded += async (sender, args) =>
            {
                try
                {
                    var confirm = (Button)window.FindName("Button_Confirm");
                    var timeout = Stopwatch.StartNew();
                    while (!confirm.IsEnabled && timeout.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(50);
                    PreviewParameters actual = Volatile.Read(ref captured);
                    Expect(confirm.IsEnabled && actual.IncludeZones && actual.CalculationAxesConfirmed && actual.AutomaticULegLength
                        && actual.CreationScope == ReinforcementCreationScope.Zones && !actual.Perimeter.Enabled
                        && actual.BackgroundLayers.All(layer => !layer.Enabled) && actual.Selections.Count == 4
                        && actual.SourceGroupKey == "upper-left-KLEENKA" && actual.Direction == "Низ X" && actual.ConcreteClass == "B25",
                        "The current bindings must capture the requested components and four selected colors.");
                    var canvas = (SlabPreviewCanvas)window.FindName("PreviewCanvas");
                    var tabs = (TabControl)window.FindName("ComponentsTabs");
                    var messageViews = new List<object>();
                    foreach (int width in new[] { 1280, 1024 })
                    {
                        window.Width = width; window.Height = width == 1024 ? 640 : 850;
                        if (combinedPreview)
                        {
                            tabs.SelectedItem = window.FindName("BackgroundTab");
                            ((ScrollViewer)window.FindName("BackgroundScrollViewer")).ScrollToTop();
                            await WaitForPlan(); window.UpdateLayout();
                            Expect(canvas.DisplayedPlan.Bars.Count == backgroundCount + globalPerimeterCount && canvas.DisplayedPlan.Groups.All(group => group.Placement.Component != "Zones"),
                                "Background tab must show the actual four-layer background and global U, with no zone geometry.");
                            SaveImage(window, "real-background-" + width + ".png");
                        }
                        tabs.SelectedItem = window.FindName("ZonesTab");
                        ((ScrollViewer)window.FindName("ZonesScrollViewer")).ScrollToTop();
                        await WaitForPlan(); window.UpdateLayout();
                        Expect(canvas.DisplayedPlan.Bars.Count == 652 && canvas.DisplayedPlan.Groups.Count == 73,
                            "Zone tab must show only 621 zone rows and 31 local U, independently of enabled background components.");
                        VerifyPreviewNavigationLayout(window);
                        if (recalculateMessages)
                        {
                            var diagnostics = (ItemsControl)window.FindName("DiagnosticsList");
                            string[] renderedMessages = diagnostics.Items.Cast<string>().ToArray();
                            var details = (ItemsControl)window.FindName("ContextWarningsList");
                            string[] renderedDetails = details.Items.Cast<string>().ToArray();
                            Expect(plan.Diagnostics.All(d => renderedMessages.Concat(renderedDetails).Any(text => text.Contains(d.Message, StringComparison.Ordinal))),
                                "The main list and details must retain the current planner's messages without replacing them with snapshot text.");
                            foreach (string code in new[] { "SINGLE_ROW_ZONES_SKIPPED", "ZONE_ENDS_IN_PERIMETER_U" })
                            {
                                var important = plan.Diagnostics.Single(d => d.Code == code);
                                Expect(renderedMessages.Any(text => text.Contains(important.Message, StringComparison.Ordinal)),
                                    "The real 23 skipped singles and 31 local U warnings must remain in the main diagnostic list.");
                            }
                            DependencyObject ancestor = VisualTreeHelper.GetParent(diagnostics);
                            while (ancestor != null && !(ancestor is ScrollViewer)) ancestor = VisualTreeHelper.GetParent(ancestor);
                            var diagnosticScroll = ancestor as ScrollViewer;
                            Expect(diagnosticScroll != null, "The production diagnostic list must retain its scroll viewer.");
                            var notes = (Expander)window.FindName("ContextWarningsExpander");
                            Expect(!notes.IsExpanded, "Additional calculation details must remain collapsed on the real B25 preview.");
                            diagnosticScroll.ScrollToTop();
                            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                            window.UpdateLayout();
                            SaveImage(window, "messages-zones-" + width + "-top.png");
                            double topOffset = diagnosticScroll.VerticalOffset;
                            diagnosticScroll.ScrollToBottom();
                            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                            window.UpdateLayout();
                            SaveImage(window, "messages-zones-" + width + "-bottom.png");
                            messageViews.Add(new { Width = width, Height = window.Height,
                                Status = ((TextBlock)window.FindName("StatusText")).Text,
                                Summary = ((TextBlock)window.FindName("PlanSummary")).Text,
                                RenderedMessages = renderedMessages, RenderedDetails = renderedDetails, TopOffset = topOffset,
                                BottomOffset = diagnosticScroll.VerticalOffset, diagnosticScroll.ViewportHeight, diagnosticScroll.ExtentHeight,
                                ContextWarningCount = context.Warnings.Count, ContextNotesExpanded = notes.IsExpanded });
                            continue;
                        }
                        SaveImage(window, "real-zones-" + width + ".png");
                        ((CheckBox)window.FindName("CheckBox_ZoneEndsInPerimeter")).BringIntoView(); window.UpdateLayout();
                        SaveImage(window, "real-zones-settings-" + width + ".png");
                    }
                    File.WriteAllText(Path.Combine(_outputDirectory, recalculateMessages ? "message-review.json" : "real-zone-preview.json"), JsonSerializer.Serialize(new
                    {
                        Mode = recalculateMessages
                            ? "Current Core recalculation of the saved B25 request, rendered in the unmodified production WPF layout; not a live Revit calculation or transaction"
                            : "Saved B25 zone/P plans rendered by current WPF; optional background from saved host via BackgroundPlanner; not a live Revit calculation or transaction",
                        Input = Path.GetFullPath(savedJson), Faces = faces.Count, SelectedFaces = request.Faces.Count, WhiteFaces = 10816,
                        ZoneBars = 621, ZonePathGroups = 67, LocalUBars = 31, LocalUGroups = 6, TotalBars = plan.Bars.Count,
                        BackgroundBars = backgroundCount, GlobalPerimeterBars = globalPerimeterCount, SkippedSingletonRows = 23,
                        Direction = actual.Direction, Concrete = actual.ConcreteClass, ActualParameters = actual,
                        OriginalSourceElevationMm = sourceGroup.GetProperty("ElevationMm").GetDouble(), DisplayedSlabRelativeElevationMm = 0,
                        CurrentCoreRecalculation = recalculateMessages, CalculationMilliseconds = calculationTime.ElapsedMilliseconds,
                        CurrentDiagnostics = recalculateMessages ? plan.Diagnostics : null, MessageViews = messageViews
                    }, new JsonSerializerOptions { WriteIndented = true }));
                    Console.WriteLine((recalculateMessages ? "PASS: current Core B25 messages and geometry; " : "PASS: real saved B25 plan; ")
                        + "14,568 source faces, 3,752 selected; Zones 652, Background "
                        + (backgroundCount + globalPerimeterCount) + "; each tab creates its own plan; 1280 and 1024 rendered.");
                    window.Close(); application.Shutdown(0);

                    async Task WaitForPlan()
                    {
                        var deadline = Stopwatch.StartNew();
                        while (!confirm.IsEnabled && deadline.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(25);
                        Expect(confirm.IsEnabled, "The selected operation did not become ready.");
                        await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    }
                }
                catch (Exception exception) { RecordFailure(exception); application.Shutdown(1); }
            };
            return application.Run(window);
        }

        private sealed class Point3JsonConverter : JsonConverter<Point3>
        {
            public override Point3 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                using var point = JsonDocument.ParseValue(ref reader);
                return new Point3(point.RootElement.GetProperty("X").GetDouble(), point.RootElement.GetProperty("Y").GetDouble(), point.RootElement.GetProperty("Z").GetDouble());
            }
            public override void Write(Utf8JsonWriter writer, Point3 value, JsonSerializerOptions options) => throw new NotSupportedException();
        }

        private static int RunSourcePreview(Application application, string sourceJson)
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            options.Converters.Add(new Point2JsonConverter());
            PreviewContext context;
            using (FileStream input = File.OpenRead(sourceJson)) context = JsonSerializer.Deserialize<PreviewContext>(input, options);
            Expect(context != null && context.SourceGroups != null && context.SourceGroups.Count > 0,
                "The source preview needs at least one layer/elevation group.");
            const string explanation = "Показана исходная карта DXF. Контур плиты из Revit и назначения арматуры для этой проверки не заданы.";
            context.SourceElevationLabel = "Z DXF";
            // This isolated renderer uses a label-only Revit stub; no model, type or transaction is available here.
            var placeholder = new Autodesk.Revit.DB.Structure.RebarBarType { Name = "Тип не назначен" };
            context.RebarTypeKeys = new Dictionary<string, string> { { placeholder.Name, "source-preview-only" } };
            context.Calculate = (parameters, token) =>
            {
                var result = new ReinforcementPlan();
                result.Diagnostics.Add(new PlanDiagnostic { Code = "SOURCE_PREVIEW_ONLY", Message = explanation, IsBlocking = true });
                return result;
            };
            var colors = context.SourceGroups.SelectMany(group => group.Faces).Select(face => face.ColorKey)
                .Distinct(StringComparer.Ordinal).OrderBy(key => key, StringComparer.Ordinal)
                .Select(key => new ColorStat { Color = key, ColorBrush = ReadColor(key) }).ToList();
            var window = new SlabReinforcementWPF(colors,
                new List<Autodesk.Revit.DB.Structure.RebarBarType> { placeholder }, context)
            {
                ShowInTaskbar = false, ShowActivated = false, Left = -20000, Top = -20000,
                WindowStartupLocation = WindowStartupLocation.Manual
            };
            ResetComponents(window);
            // This fixture explicitly performs the user's choice; the product must never infer it across groups.
            CadPlaneGroup inspectedGroup = context.SourceGroups.Single(group => string.Equals(group.Layer, "KLEENKA", StringComparison.Ordinal));
            ((ComboBox)window.FindName("ComboBox_SourceGroup")).SelectedValue = inspectedGroup.Key;
            foreach (ColorStat color in colors)
            {
                color.SelectedRebarType = placeholder;
                color.IsSelected = true;
                color.SpacingText = "200";
            }
            ((TextBox)window.FindName("TextBox_EdgeCover")).Text = "25";
            ((TextBox)window.FindName("TextBox_ZoneMergeTolerance")).Text = "0";
            ((TextBox)window.FindName("TextBox_RoundIncrement")).Text = "10";
            window.Loaded += async (sender, args) =>
            {
                try
                {
                    var status = (TextBlock)window.FindName("StatusText");
                    var timeout = Stopwatch.StartNew();
                    while (status.Text != "Создание недоступно" && timeout.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(50);
                    Expect(status.Text == "Создание недоступно" && !((Button)window.FindName("Button_Confirm")).IsEnabled,
                        "Source-only preview must block reinforcement creation.");
                    Expect(context.Faces.Count > 0 && context.Faces.Any(face => face.Vertices.Any(point => point.X != 0 || point.Y != 0)),
                        "Source coordinates were not loaded.");
                    SaveImage(window, "real-dxf-source.png");
                    string result = "PASS: source DXF snapshot rendered; " + context.SourceGroups.Count + " layer/elevation groups; " +
                        context.Faces.Count + " faces in the selected group; no slab or reinforcement plan fabricated. Input: " + Path.GetFullPath(sourceJson);
                    File.WriteAllText(Path.Combine(_outputDirectory, "real-dxf-source.txt"), result);
                    Console.WriteLine(result);
                    window.Close();
                    application.Shutdown(0);
                }
                catch (Exception exception)
                {
                    RecordFailure(exception);
                    application.Shutdown(1);
                }
            };
            return application.Run(window);
        }

        private static SolidColorBrush ReadColor(string key)
        {
            System.Text.RegularExpressions.MatchCollection components = System.Text.RegularExpressions.Regex.Matches(key ?? string.Empty, @"\d+");
            if (components.Count != 3 || !byte.TryParse(components[0].Value, out byte red) ||
                !byte.TryParse(components[1].Value, out byte green) || !byte.TryParse(components[2].Value, out byte blue))
                throw new InvalidDataException("Invalid RGB color in source snapshot: " + key);
            return new SolidColorBrush(Color.FromRgb(red, green, blue));
        }

        private sealed class Point2JsonConverter : JsonConverter<Point2>
        {
            public override Point2 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("A point must have X and Y coordinates.");
                double? x = null, y = null;
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    string name = reader.GetString();
                    reader.Read();
                    if (string.Equals(name, "X", StringComparison.OrdinalIgnoreCase)) x = reader.GetDouble();
                    else if (string.Equals(name, "Y", StringComparison.OrdinalIgnoreCase)) y = reader.GetDouble();
                    else reader.Skip();
                }
                if (!x.HasValue || !y.HasValue) throw new JsonException("A point must have X and Y coordinates.");
                return new Point2(x.Value, y.Value);
            }

            public override void Write(Utf8JsonWriter writer, Point2 value, JsonSerializerOptions options) =>
                throw new NotSupportedException("This smoke only reads source snapshots.");
        }

        private static PreviewContext CreateContext(Autodesk.Revit.DB.Structure.RebarBarType type)
        {
            var context = new PreviewContext
            {
                HostName = "Тестовая плита · 12 000 × 8 000 мм",
                AxisDescription = "Локальные оси плиты · мм",
                RebarTypeKeys = new Dictionary<string, string> { { type.Name, "test" } },
                Slabs = new List<PolygonInput>
                {
                    new PolygonInput
                    {
                        Shell = new List<Point2> { new Point2(0, 0), new Point2(12000, 0), new Point2(12000, 8000), new Point2(0, 8000) },
                        Holes = new List<List<Point2>>
                        {
                            new List<Point2> { new Point2(4500, 3000), new Point2(6500, 3000), new Point2(6500, 5000), new Point2(4500, 5000) }
                        }
                    }
                },
                Faces = new List<FaceInput>
                {
                    new FaceInput
                    {
                        ColorKey = "RGB(255, 127, 127)",
                        Vertices = new List<Point2> { new Point2(500, 500), new Point2(11000, 500), new Point2(11000, 7500), new Point2(500, 7500) }
                    }
                }
            };
            context.Calculate = (parameters, token) =>
            {
                double spacing = parameters.Selections[0].SpacingMm;
                // Intentionally ignore cancellation, then finish old work after the newer result.
                Thread.Sleep(spacing == 200 ? 650 : 25);
                var result = new ReinforcementPlan();
                for (double y = 500; y < 3000; y += spacing)
                    result.Bars.Add(new PlannedBar
                    {
                        Start = new Point2(spacing, y), End = new Point2(11000, y), ColorKey = "RGB(220, 120, 80)",
                        Placement = new BarPlacement { Component = "Zones" }
                    });
                result.Groups.Add(new BarGroup { Count = result.Bars.Count, Placement = new BarPlacement { Component = "Zones" } });
                return result;
            };
            return context;
        }

        private static async Task VerifyWindow(SlabReinforcementWPF window, ColorStat color,
            Autodesk.Revit.DB.Structure.RebarBarType type)
        {
            var settings = (Expander)window.FindName("SettingsExpander");
            Expect(window.FindName("CheckBox_AllowBarIntersections") == null,
                "Ordinary intersections must not require a UI permission checkbox.");
            Expect(window.FindName("TextBox_LayerOffset") == null,
                "Zones must not expose a manual displacement from their actual background layer.");
            var savedSettings = (SlabReinforcementSettings)typeof(SlabReinforcementWPF)
                .GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window);
            savedSettings.AllowBarIntersections = false; // Emulate an old persisted option without touching the user's file.
            var colorGrid = (DataGrid)window.FindName("ColorStatsDataGrid");
            Expect(settings != null && !settings.IsExpanded, "Settings must start collapsed to leave room for colors.");
            Expect(window.FindName("CheckBox_CalculationAxesConfirmed") == null && window.FindName("CheckBox_IncludeZones") == null,
                "Selecting a source and direction must not require additional enable/axis checkboxes.");
            ResetComponents(window);
            // Stable inputs make this smoke independent of the user's saved form values.
            ((TextBox)window.FindName("TextBox_EdgeCover")).Text = "25";
            ((TextBox)window.FindName("TextBox_ZoneMergeTolerance")).Text = "600";
            ((TextBox)window.FindName("TextBox_RoundIncrement")).Text = "10";
            color.IsSelected = true;
            color.SelectedRebarType = type;
            color.SpacingText = "200";
            await Task.Delay(370);
            color.SpacingText = "250";
            await Task.Delay(1050);
            var confirm = (Button)window.FindName("Button_Confirm");
            Expect(confirm.IsEnabled, "Valid latest plan must enable creation.");
            var acceptedPolicy = (PreviewParameters)typeof(SlabReinforcementWPF)
                .GetField("_readyParameters", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window);
            Expect(acceptedPolicy.AllowBarIntersections && acceptedPolicy.IncludeZones && acceptedPolicy.CalculationAxesConfirmed,
                "The chosen source and direction must calculate directly and allow ordinary intersections.");
            var plan = (ReinforcementPlan)typeof(SlabReinforcementWPF)
                .GetField("_readyPlan", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window);
            Expect(plan.Bars[0].Start.X == 250, "A stale result replaced the latest result.");

            color.SpacingText = "bad";
            Expect(!confirm.IsEnabled, "Invalid spacing did not immediately disable creation.");
            color.SpacingText = "300";
            color.SelectedRebarType = null;
            Expect(!confirm.IsEnabled, "A missing type must disable creation.");
            color.SelectedRebarType = type;
            await Task.Delay(500);
            Expect(confirm.IsEnabled, "Corrected inputs did not recalculate.");

            var direction = (ComboBox)window.FindName("ComboBox_Direction");
            string originalDirection = (string)direction.SelectedItem;
            direction.SelectedItem = originalDirection == "Низ Y" ? "Низ X" : "Низ Y";
            Expect(!confirm.IsEnabled, "Changing direction must invalidate the old plan immediately.");
            await Task.Delay(500);
            Expect(confirm.IsEnabled, "An explicit direction change must recalculate without another acknowledgment.");
            direction.SelectedItem = null;
            Expect(!confirm.IsEnabled, "A missing direction must block creation.");
            direction.SelectedItem = originalDirection;
            await Task.Delay(500);
            Expect(confirm.IsEnabled, "Restoring the current direction must restore calculation.");

            var meaning = (ComboBox)window.FindName("ComboBox_DemandMeaning");
            meaning.SelectedValue = "TotalArea";
            Expect(!confirm.IsEnabled, "A numeric demand mode requires a value for each selected color.");
            Expect(((DataGridColumn)window.FindName("RequiredAreaColumn")).Visibility == Visibility.Visible,
                "Numeric modes must show the area editor.");
            color.RequiredAreaText = "625,5";
            await Task.Delay(500);
            var numericParameters = (PreviewParameters)typeof(SlabReinforcementWPF)
                .GetField("_readyParameters", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window);
            Expect(confirm.IsEnabled && numericParameters.DemandMeaning == "TotalArea" &&
                numericParameters.Selections.Single().RequiredAreaMm2PerM == 625.5,
                "The numeric area must be passed in mm²/m without unit inference.");
            SaveImage(window, "preview-zone-area.png");
            color.RequiredAreaText = "bad";
            Expect(!confirm.IsEnabled, "Invalid numeric demand must immediately invalidate the plan.");
            meaning.SelectedValue = "Manual";
            await Task.Delay(500);
            Expect(confirm.IsEnabled && ((DataGridColumn)window.FindName("RequiredAreaColumn")).Visibility == Visibility.Collapsed,
                "Manual mode must not require an unused numeric area.");

            settings.IsExpanded = true;
            await Task.Delay(100);
            var editor = (TextBox)window.FindName("TextBox_EdgeCover");
            editor.BringIntoView();
            editor.Focus();
            PresentationSource source = PresentationSource.FromVisual(window);
            editor.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
            editor.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Space) { RoutedEvent = Keyboard.KeyDownEvent });
            Expect(window.DialogResult == null, "Editor Enter/Space accepted the dialog.");
            await Task.Delay(500);
            settings.IsExpanded = false;
            await Task.Delay(100);
            SaveImage(window, "preview-1280.png");
            VerifyPreviewNavigationLayout(window);

            window.Width = 1080;
            window.Height = 700;
            await Task.Delay(100);
            SaveImage(window, "preview-1080.png");
            VerifyPreviewNavigationLayout(window);
            settings.IsExpanded = true;
            await Task.Delay(100);
            ((RadioButton)window.FindName("RadioButton_RoundCutLengths")).BringIntoView();
            await Task.Delay(100);
            SaveImage(window, "preview-1080-settings.png");

            window.Width = 1024;
            window.Height = 640;
            settings.IsExpanded = false;
            await Task.Delay(100);
            colorGrid.BringIntoView(); window.UpdateLayout();
            Expect(colorGrid.IsVisible && colorGrid.ActualHeight >= 250 && IsInsideWindow(window, colorGrid),
                "The color table must remain visible and at least 250 px tall at 1024x640 with settings collapsed.");
            Expect(IsInsideWindow(window, confirm), "The create button must be fully accessible at 1024x640.");
            SaveImage(window, "preview-1024.png");
            VerifyPreviewNavigationLayout(window);
            settings.IsExpanded = true;
            await Task.Delay(100);
            colorGrid.BringIntoView(); window.UpdateLayout();
            Expect(colorGrid.IsVisible && IsInsideElement((ScrollViewer)window.FindName("ZonesScrollViewer"), colorGrid),
                "Opening common settings must leave the color table reachable at 1024x640.");
            var roundingChoice = (RadioButton)window.FindName("RadioButton_RoundCutLengths");
            roundingChoice.BringIntoView();
            await Task.Delay(100);
            Expect(IsInsideWindow(window, confirm), "The create button must be fully accessible at 1024x640.");
            SaveImage(window, "preview-1024-settings.png");
            VerifyPreviewNavigationLayout(window);
            File.WriteAllText(Path.Combine(_outputDirectory, "settings-viewport.txt"),
                "Rounding: " + roundingChoice.TransformToAncestor((ScrollViewer)window.FindName("ZonesScrollViewer")).TransformBounds(new Rect(roundingChoice.RenderSize)) +
                "; viewport: " + ((ScrollViewer)window.FindName("ZonesScrollViewer")).RenderSize);
            Expect(roundingChoice.IsVisible && IsInsideWindow(window, roundingChoice) &&
                IsInsideElement((ScrollViewer)window.FindName("ZonesScrollViewer"), roundingChoice),
                "Rounding must remain accessible inside the expanded settings viewport at 1024x640.");
            SaveImage(window, "preview-1024-settings.png");
            settings.IsExpanded = false;

            var canvas = (SlabPreviewCanvas)window.FindName("PreviewCanvas");
            var manyBars = new ReinforcementPlan();
            for (int index = 0; index < 10000; index++)
                manyBars.Bars.Add(new PlannedBar
                {
                    Start = new Point2(100, index * 0.25), End = new Point2(11000, index * 0.25), ColorKey = "RGB(220, 120, 80)",
                    PreviewPurpose = PreviewPurpose.Zones
                });
            var stopwatch = Stopwatch.StartNew();
            canvas.SetPlan(manyBars);
            Expect(canvas.DisplayedPlan.Bars.Count == 10000, "The performance fixture must actually render all 10,000 zone bars in its current scope.");
            canvas.Zoom(1.25);
            canvas.Fit();
            window.UpdateLayout();
            Expect(VisualTreeHelper.GetChildrenCount(canvas) == 0, "The renderer must not allocate a UI child per bar.");
            SaveImage(window, "preview-10000-bars.png");
            File.WriteAllText(Path.Combine(_outputDirectory, "render-performance.txt"),
                "10,000 bars, zoom, fit and PNG rendering: " + stopwatch.ElapsedMilliseconds + " ms");

            color.SpacingText = "200";
            await Task.Delay(350);
            window.Close();
            await Task.Delay(800);
        }

        private static async Task VerifySourceGroupSelection()
        {
            const string blue = "RGB(0, 0, 255)";
            const string red = "RGB(255, 0, 0)";
            var type = new Autodesk.Revit.DB.Structure.RebarBarType { Name = "Source group smoke type" };
            var colors = new List<ColorStat>
            {
                new ColorStat { Color = blue, ColorBrush = Brushes.Blue },
                new ColorStat { Color = red, ColorBrush = Brushes.Red }
            };
            PreviewContext context = CreateContext(type);
            FaceInput Face(string id, string colorKey) => new FaceInput
            {
                Id = id, ColorKey = colorKey,
                Vertices = new List<Point2> { new Point2(500, 500), new Point2(1500, 500), new Point2(1500, 1500), new Point2(500, 1500) }
            };
            var singleColor = new CadPlaneGroup
            {
                Key = "PLAST|549.45", Layer = "PLAST", ElevationMm = 549.45,
                Faces = new List<FaceInput> { Face("background", blue) }
            };
            var demand = new CadPlaneGroup
            {
                Key = "KLEENKA|-0.55", Layer = "KLEENKA", ElevationMm = -0.55,
                Faces = new List<FaceInput> { Face("blue-demand", blue), Face("red-demand", red) }
            };
            var otherStorey = new CadPlaneGroup
            {
                Key = "KLEENKA|1149.45", Layer = "KLEENKA", ElevationMm = 1149.45,
                Faces = new List<FaceInput> { Face("other-blue", blue), Face("other-red", red), Face("other-blue-2", blue) }
            };
            context.SourceGroups = new List<CadPlaneGroup> { singleColor, demand, otherStorey };
            context.Faces = context.SourceGroups.SelectMany(group => group.Faces).ToList();
            var requests = new System.Collections.Concurrent.ConcurrentQueue<PreviewParameters>();
            context.Calculate = (parameters, token) =>
            {
                requests.Enqueue(parameters);
                // A finished calculation from the previous elevation must never replace the selected group.
                Thread.Sleep(parameters.SourceGroupKey == demand.Key ? 650 : 25);
                var plan = new ReinforcementPlan();
                plan.Bars.Add(new PlannedBar
                {
                    Start = new Point2(parameters.SourceGroupKey == demand.Key ? 1000 : 2000, 1000),
                    End = new Point2(4000, 1000), ColorKey = blue, Placement = new BarPlacement { Component = "Zones" }
                });
                return plan;
            };
            var window = new SlabReinforcementWPF(colors,
                new List<Autodesk.Revit.DB.Structure.RebarBarType> { type }, context)
            {
                ShowInTaskbar = false, ShowActivated = false, Left = -20000, Top = -20000,
                WindowStartupLocation = WindowStartupLocation.Manual
            };
            ResetComponents(window);
            foreach (ColorStat color in colors)
            {
                color.SelectedRebarType = type;
                color.IsSelected = true;
                color.SpacingText = "200";
            }
            ((TextBox)window.FindName("TextBox_EdgeCover")).Text = "25";
            ((TextBox)window.FindName("TextBox_ZoneMergeTolerance")).Text = "0";
            ((TextBox)window.FindName("TextBox_RoundIncrement")).Text = "10";
            window.Show();
            var groups = (ComboBox)window.FindName("ComboBox_SourceGroup");
            var grid = (DataGrid)window.FindName("ColorStatsDataGrid");
            var confirm = (Button)window.FindName("Button_Confirm");
            Expect(groups.Visibility == Visibility.Visible && groups.Items.Count == 3, "CAD layers and multiple elevations of one layer must be available in the form.");
            Expect(groups.SelectedIndex == -1 && context.Faces.Count == 0 && grid.Items.Count == 0,
                "Multiple CAD layers/elevations must require an explicit choice, regardless of color diversity or source order.");
            Expect(((TextBlock)window.FindName("SourceGroupPlaceholder")).IsVisible,
                "The source selector must explain that a layer/elevation choice is required.");
            await Task.Delay(400);
            Expect(requests.IsEmpty && !confirm.IsEnabled,
                "Previously selected colors must not calculate or enable creation before a source group is explicitly chosen.");
            SaveImage(window, "preview-source-choice-required.png");
            groups.SelectedValue = demand.Key;
            Expect(context.Faces.SequenceEqual(demand.Faces), "An explicit source selection must load exactly that group's faces.");
            Expect(grid.Items.Count == 2, "The initial group must show its two demand colors.");
            await Task.Delay(370);
            groups.SelectedValue = singleColor.Key;
            Expect(!confirm.IsEnabled, "Switching a CAD group must immediately invalidate the previous plan.");
            Expect(context.Faces.SequenceEqual(singleColor.Faces), "Changing elevation/layer must replace visible source faces.");
            Expect(grid.Items.Count == 1 && ((ColorStat)grid.Items[0]).Color == blue,
                "The palette must contain only the selected group's colors.");
            await Task.Delay(1050);
            PreviewParameters[] captured = requests.ToArray();
            Expect(captured.All(request => request.IncludeZones && request.CalculationAxesConfirmed),
                "Explicit source-group selection must provide the UI's valid source/axes contract without a second checkbox.");
            Expect(captured.Any(request => request.SourceGroupKey == demand.Key), "The first request did not capture its source key.");
            Expect(captured.Any(request => request.SourceGroupKey == singleColor.Key && request.Selections.Count == 1 &&
                request.Selections[0].ColorKey == blue), "The changed source request must exclude the hidden color.");
            var latestPlan = (ReinforcementPlan)typeof(SlabReinforcementWPF)
                .GetField("_readyPlan", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window);
            Expect(confirm.IsEnabled && latestPlan.Bars[0].Start.X == 2000,
                "An old source group replaced the latest plan or creation remained disabled.");
            SaveImage(window, "preview-source-groups.png");
            window.Width = 1024;
            window.Height = 640;
            var settings = (Expander)window.FindName("SettingsExpander");
            Expect(settings != null && !settings.IsExpanded, "Source selection must not expand settings automatically.");
            await Task.Delay(100);
            grid.BringIntoView(); window.UpdateLayout();
            SaveImage(window, "preview-source-groups-1024-collapsed.png");
            File.WriteAllText(Path.Combine(_outputDirectory, "source-table-bounds.txt"),
                "Visible=" + grid.IsVisible + "; height=" + grid.ActualHeight + "; inside=" + IsInsideWindow(window, grid));
            // The separate underlay-selection row adds about 40 px when the group selector is visible.
            Expect(grid.IsVisible && grid.ActualHeight >= 200 && IsInsideWindow(window, grid),
                "Source and group selection must leave at least 200 px for the color table at 1024x640 with settings collapsed.");
            groups.BringIntoView(); window.UpdateLayout();
            Expect(groups.IsVisible && IsInsideElement((ScrollViewer)window.FindName("ZonesScrollViewer"), groups), "The source group selector must remain accessible at 1024x640.");
            SaveImage(window, "preview-source-groups-1024-collapsed.png");
            settings.IsExpanded = true;
            await Task.Delay(100);
            grid.BringIntoView(); window.UpdateLayout();
            Expect(IsInsideElement((ScrollViewer)window.FindName("ZonesScrollViewer"), grid),
                "The selected group's colors must remain reachable with common settings expanded.");
            var rounding = (RadioButton)window.FindName("RadioButton_RoundCutLengths");
            rounding.BringIntoView();
            await Task.Delay(100);
            Expect(rounding.IsVisible && IsInsideWindow(window, rounding) && IsInsideElement((ScrollViewer)window.FindName("ZonesScrollViewer"), rounding),
                "Source groups must not push settings outside their scroll viewport at the minimum window size.");
            SaveImage(window, "preview-source-groups-1024.png");
            window.Close();

            PreviewContext oneGroupContext = CreateContext(type);
            oneGroupContext.SourceGroups = new List<CadPlaneGroup> { singleColor };
            var oneGroupWindow = new SlabReinforcementWPF(colors,
                new List<Autodesk.Revit.DB.Structure.RebarBarType> { type }, oneGroupContext)
            {
                ShowInTaskbar = false, ShowActivated = false, Left = -20000, Top = -20000,
                WindowStartupLocation = WindowStartupLocation.Manual
            };
            oneGroupWindow.Show();
            Expect(((ComboBox)oneGroupWindow.FindName("ComboBox_SourceGroup")).SelectedIndex == 0 &&
                oneGroupContext.Faces.SequenceEqual(singleColor.Faces), "A single unambiguous source group may be selected automatically.");
            oneGroupWindow.Close();
        }

        private static async Task VerifyComponentsWithoutCad()
        {
            // This fixture tests controls with a stub calculation, not physical U-only reinforcement.
            var type = new Autodesk.Revit.DB.Structure.RebarBarType { Name = "ø12 A500C" };
            PreviewContext context = CreateContext(type);
            context.Faces.Clear();
            var requests = new System.Collections.Concurrent.ConcurrentQueue<PreviewParameters>();
            context.Calculate = (parameters, token) =>
            {
                requests.Enqueue(parameters);
                var plan = new ReinforcementPlan();
                plan.Bars.Add(new PlannedBar { Start = new Point2(500, 500), End = new Point2(4000, 500),
                    PreviewPurpose = PreviewPurpose.Background, Placement = new BarPlacement { Component = parameters.Perimeter.Enabled ? "PerimeterU" : "Background" } });
                return plan;
            };
            var window = new SlabReinforcementWPF(new List<ColorStat>(),
                new List<Autodesk.Revit.DB.Structure.RebarBarType> { type }, context)
            {
                ShowInTaskbar = false, ShowActivated = false, Left = -20000, Top = -20000,
                WindowStartupLocation = WindowStartupLocation.Manual
            };
            ResetComponents(window);
            ((TextBox)window.FindName("TextBox_EdgeCover")).Text = "25";
            ((TextBox)window.FindName("TextBox_ZoneMergeTolerance")).Text = "0";
            ((TextBox)window.FindName("TextBox_RoundIncrement")).Text = "10";
            window.Show();
            var confirm = (Button)window.FindName("Button_Confirm");
            var tabs = (TabControl)window.FindName("ComponentsTabs");
            var layers = ((DataGrid)window.FindName("BackgroundDataGrid")).Items.Cast<BackgroundLayerStat>().ToList();
            Expect(!((FrameworkElement)window.FindName("ZoneSettingsPanel")).IsEnabled && layers.Count == 4,
                "A no-CAD invocation must offer four background layers with zones disabled.");
            layers[0].SelectedRebarType = type;
            layers[0].SpacingText = "150";
            ((TextBox)window.FindName("TextBox_ZoneMergeTolerance")).Text = "bad";
            ((TextBox)window.FindName("TextBox_RoundIncrement")).Text = "bad";
            ((ComboBox)window.FindName("ComboBox_Direction")).SelectedItem = null;
            layers[0].Enabled = true;
            await Task.Delay(500);
            Expect(requests.Count > 0 && confirm.IsEnabled,
                "Without an underlay, disabled zone-only inputs must not block background calculation.");
            PreviewParameters captured = requests.Last();
            Expect(confirm.IsEnabled && !captured.IncludeZones && !captured.CalculationAxesConfirmed && captured.Selections.Count == 0 &&
                captured.BackgroundLayers.Single(layer => layer.Enabled).SpecKey == "test" &&
                captured.BackgroundLayers.Single(layer => layer.Enabled).SpacingMm == 150,
                "Background alone must calculate without CAD, selected colors or an axis acknowledgment.");
            SaveImage(window, "preview-background.png");
            int count = requests.Count;
            tabs.SelectedItem = window.FindName("ZonesTab");
            await Task.Delay(400);
            Expect(!confirm.IsEnabled && requests.Count == count, "Zones without a source must reject creation, without falling back to background preferences.");
            tabs.SelectedItem = window.FindName("BackgroundTab");
            ((CheckBox)window.FindName("CheckBox_PerimeterEnabled")).IsChecked = true;
            ((CheckBox)window.FindName("CheckBox_PerimeterOuter")).IsChecked = false;
            ((CheckBox)window.FindName("CheckBox_PerimeterOpenings")).IsChecked = true;
            Expect(window.FindName("CheckBox_PerimeterLongitudinal") == null && window.FindName("CheckBox_PerimeterUbars") == null,
                "The U-only MVP must not expose a perimeter shape selector.");
            ((ComboBox)window.FindName("ComboBox_PerimeterType")).SelectedItem = type;
            var manualSpacing = (TextBox)window.FindName("TextBox_PerimeterSpacing");
            window.UpdateLayout();
            Expect(manualSpacing.IsVisible && manualSpacing.IsEnabled,
                "Perimeter spacing must be editable without a complete actual lower X/Y background.");
            manualSpacing.Text = "175";
            await Task.Delay(500);
            captured = requests.Last();
            Expect(confirm.IsEnabled && captured.BackgroundLayers.Any(layer => layer.Enabled) && captured.Perimeter.Enabled &&
                !captured.Perimeter.Outer && captured.Perimeter.Openings && captured.Perimeter.UseUbars && !captured.Perimeter.UseLongitudinalBars &&
                captured.Perimeter.SpecKey == "test" && captured.Perimeter.SpacingMm == 175 && captured.AutomaticULegLength,
                "Every enabled component and explicit perimeter option must reach the immutable preview request.");
            layers[0].Enabled = false;
            foreach (string invalidValue in new[] { "bad", "", "-1", "NaN" })
            {
                manualSpacing.Text = invalidValue;
                await Task.Delay(500);
                Expect(!confirm.IsEnabled, "Manual perimeter spacing must reject invalid values: " + invalidValue);
            }
            manualSpacing.Text = "175";
            await Task.Delay(500);
            Expect(confirm.IsEnabled && !requests.Last().BackgroundLayers.Any(layer => layer.Enabled) &&
                !requests.Last().Perimeter.UseLongitudinalBars && requests.Last().Perimeter.UseUbars,
                "Corrected manual spacing must permit automatic-length U framing without background.");
            window.Width = 1024;
            window.Height = 640;
            await Task.Delay(100);
            SaveImage(window, "preview-background-1024.png");
            var lengthPolicy = (TextBlock)window.FindName("TextBlock_PerimeterLengthPolicy");
            lengthPolicy.BringIntoView();
            await Task.Delay(100);
            Expect(lengthPolicy.IsVisible && IsInsideWindow(window, lengthPolicy) &&
                IsInsideElement((ScrollViewer)window.FindName("BackgroundScrollViewer"), lengthPolicy),
                "Automatic perimeter length policy must remain accessible at 1024x640.");
            SaveImage(window, "preview-background-1024-scrolled.png");
            ((CheckBox)window.FindName("CheckBox_PerimeterEnabled")).IsChecked = false;
            Expect(!confirm.IsEnabled, "An operation with every component disabled must block creation.");
            window.Close();
        }

        private static async Task VerifyBoundPreviewRefresh()
        {
            var type12=new Autodesk.Revit.DB.Structure.RebarBarType{Name="ø12 A500C"};
            var type16=new Autodesk.Revit.DB.Structure.RebarBarType{Name="ø16 A500C"};
            var colors=new List<ColorStat>
            {
                new ColorStat{Color="RGB(0, 255, 0)",ColorBrush=Brushes.Green,IsSelected=true},
                new ColorStat{Color="RGB(0, 0, 255)",ColorBrush=Brushes.Blue,IsSelected=true}
            };
            var context=CreateContext(type12);
            context.RebarTypeKeys[type16.Name]="type16";
            using var oldStarted=new ManualResetEventSlim();
            using var releaseOld=new ManualResetEventSlim();
            using var oldCompleted=new ManualResetEventSlim();
            int holdOld=0;
            var requests=new System.Collections.Concurrent.ConcurrentQueue<PreviewParameters>();
            context.Calculate=(parameters,token)=>
            {
                requests.Enqueue(parameters);
                bool zones=parameters.CreationScope==ReinforcementCreationScope.Zones;
                bool invalidType=zones&&parameters.Selections.Any(s=>s.SpecKey=="type16");
                bool held=invalidType&&Volatile.Read(ref holdOld)!=0;
                if(held){oldStarted.Set();releaseOld.Wait(TimeSpan.FromSeconds(8));}
                var plan=new ReinforcementPlan();
                if(zones)
                {
                    foreach(var selection in parameters.Selections.Where(s=>s.SpecKey!="type16"))Add(selection.ColorKey,"Zones",PreviewPurpose.Zones);
                    if(invalidType)plan.Diagnostics.Add(new PlanDiagnostic{Code="TYPE16_BLOCKED",Message="Для диаметра 16 не помещается выбранный участок.",IsBlocking=true});
                }
                else Add("background","Background",PreviewPurpose.Background);
                if(held)oldCompleted.Set();
                return plan;

                void Add(string key,string component,PreviewPurpose purpose)
                {
                    var placement=new BarPlacement{Component=component,TypeKey="test",ModelDiameterMm=12,ElevationMm=-41,Distribution=new Point3(0,1,0)};
                    int offset=plan.Bars.Count;
                    for(int i=0;i<2;i++)plan.Bars.Add(new PlannedBar{SpecKey=key,ColorKey=key,Start=new Point2(500,1000+(offset+i)*200),End=new Point2(10000,1000+(offset+i)*200),PreviewPurpose=purpose,Placement=placement});
                    plan.Groups.Add(new BarGroup{SpecKey=key,Start=plan.Bars[offset].Start,End=plan.Bars[offset].End,Count=2,SpacingMm=200,PreviewPurpose=purpose,Placement=placement});
                }
            };
            var window=new SlabReinforcementWPF(colors,new List<Autodesk.Revit.DB.Structure.RebarBarType>{type12,type16},context,new SlabReinforcementSettings())
                {ShowInTaskbar=false,ShowActivated=false,Left=-20000,Top=-20000,WindowStartupLocation=WindowStartupLocation.Manual};
            ResetComponents(window);
            var lower=(BackgroundLayerStat)((DataGrid)window.FindName("BackgroundDataGrid")).Items[0];lower.Enabled=true;
            var canvas=(SlabPreviewCanvas)window.FindName("PreviewCanvas");
            var confirm=(Button)window.FindName("Button_Confirm");
            var status=(TextBlock)window.FindName("StatusText");
            var summary=(TextBlock)window.FindName("PlanSummary");
            var grid=(DataGrid)window.FindName("ColorStatsDataGrid");
            var evidence=new List<object>();
            try
            {
                window.Show();await Ready();await window.Dispatcher.InvokeAsync(()=>{},System.Windows.Threading.DispatcherPriority.ApplicationIdle);window.UpdateLayout();
                Check(true,4);Record("initial");
                var checkboxes=colors.Select(c=>Visuals<CheckBox>(grid).Single(control=>ReferenceEquals(control.DataContext,c))).ToArray();
                var typeEditors=colors.Select(c=>Visuals<ComboBox>(grid).Single(control=>ReferenceEquals(control.DataContext,c))).ToArray();
                Toggle(checkboxes[1]);
                Expect(!colors[1].IsSelected,"The real checkbox binding must update ColorStat.IsSelected.");
                Cleared();await Ready();Check(true,2);Record("color-off");
                Toggle(checkboxes[1]);Cleared();await Ready();Check(true,4);Record("color-on");
                typeEditors[0].SelectedItem=type16;
                Expect(ReferenceEquals(colors[0].SelectedRebarType,type16),"The real ComboBox binding must update the selected type.");
                Cleared();await Until(()=>status.Text=="Создание недоступно","Blocked type result did not arrive.");
                Expect(!confirm.IsEnabled&&ReadyPlan()==null,"A blocked calculation must never become the ready/accepted plan.");
                Expect(canvas.DisplayedPlan!=null&&canvas.DisplayedPlan.Bars.Count==2
                    &&canvas.DisplayedPlan.Bars.All(b=>b.ColorKey==colors[1].Color&&b.PreviewPurpose==PreviewPurpose.Zones),
                    "A blocked partial preview must contain only the current request's surviving zone rows, never old rows/background.");
                Expect(((TextBlock)window.FindName("PreviewLegendZones")).IsVisible&&!((TextBlock)window.FindName("PreviewLegendBackground")).IsVisible,
                    "Zone calculation errors must not change the active legend to background.");
                Record("type16-blocked-current-partial");SaveImage(window,"preview-type16-blocked.png");
                typeEditors[0].SelectedItem=type12;Cleared();await Ready();Check(true,4);Record("type12-restored");
                ((TabItem)window.FindName("BackgroundTab")).IsSelected=true;Cleared();await Ready();Check(false,2);Record("background-tab");
                ((TabItem)window.FindName("ZonesTab")).IsSelected=true;Cleared();await Ready();Check(true,4);Record("zones-tab");
                Volatile.Write(ref holdOld,1);typeEditors[0].SelectedItem=type16;
                await Until(()=>oldStarted.IsSet,"The obsolete type worker did not start.");
                Toggle(checkboxes[0]);Cleared();await Ready();Check(true,2);
                releaseOld.Set();await Until(()=>oldCompleted.IsSet,"The cancelled type worker did not complete.");await Task.Delay(80);
                Check(true,2);
                Expect(canvas.DisplayedPlan.Bars.All(b=>b.ColorKey==colors[1].Color)&&!canvas.DisplayedPlan.Diagnostics.Any(d=>d.Code=="TYPE16_BLOCKED"),
                    "A cancelled type result must not restore deselected color rows or obsolete diagnostics.");
                Record("cancelled-old-type-result-rejected");
                Toggle(checkboxes[1]);Cleared();
                Expect(status.Text=="Проверьте параметры","Deselecting every color must show current validation.");Record("all-colors-off");
                typeEditors[0].SelectedItem=type12;Toggle(checkboxes[0]);await Ready();Check(true,2);Record("reselected-current-color");
                File.WriteAllText(Path.Combine(_outputDirectory,"preview-refresh.json"),JsonSerializer.Serialize(new{requests=requests.Count,evidence},new JsonSerializerOptions{WriteIndented=true}));
            }
            finally{releaseOld.Set();window.Close();}

            void Check(bool zones,int count)
            {
                Expect(confirm.IsEnabled&&ReadyPlan()!=null&&canvas.DisplayedPlan?.Bars.Count==count,"Latest preview bar count does not match current editors.");
                Expect(canvas.DisplayedPlan.Bars.All(b=>b.PreviewPurpose==(zones?PreviewPurpose.Zones:PreviewPurpose.Background)),"Wrong-tab geometry reached current preview.");
                Expect(((TextBlock)window.FindName(zones?"PreviewLegendZones":"PreviewLegendBackground")).IsVisible
                    &&!((TextBlock)window.FindName(zones?"PreviewLegendBackground":"PreviewLegendZones")).IsVisible,"Legend does not match selected tab.");
            }
            void Cleared()=>Expect(!confirm.IsEnabled&&ReadyPlan()==null&&canvas.DisplayedPlan==null&&summary.Text.Length==0,"An editor change must immediately discard old geometry/counts/acceptance.");
            void Record(string step)=>evidence.Add(new{step,selectedColors=colors.Count(c=>c.IsSelected),barCount=canvas.DisplayedPlan?.Bars.Count??0,
                status=status.Text,canCreate=confirm.IsEnabled,scope=typeof(SlabPreviewCanvas).GetField("_scope",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(canvas).ToString()});
            ReinforcementPlan ReadyPlan()=>(ReinforcementPlan)typeof(SlabReinforcementWPF).GetField("_readyPlan",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(window);
            Task Ready()=>Until(()=>confirm.IsEnabled,"Preview did not become ready after bound editor change.");
            static void Toggle(CheckBox control)=>((System.Windows.Automation.Provider.IToggleProvider)new System.Windows.Automation.Peers.CheckBoxAutomationPeer(control)
                .GetPattern(System.Windows.Automation.Peers.PatternInterface.Toggle)).Toggle();
            static IEnumerable<T> Visuals<T>(DependencyObject parent) where T:DependencyObject
            {
                for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
                {var child=VisualTreeHelper.GetChild(parent,i);if(child is T match)yield return match;foreach(var nested in Visuals<T>(child))yield return nested;}
            }
            static async Task Until(Func<bool> condition,string message)
            {var timer=Stopwatch.StartNew();while(!condition()&&timer.Elapsed<TimeSpan.FromSeconds(6))await Task.Delay(25);Expect(condition(),message);}
        }

        private static async Task VerifyTabScopedPreview()
        {
            var type = new Autodesk.Revit.DB.Structure.RebarBarType { Name = "ø12 A500C" };
            var color = new ColorStat { Color = "RGB(0, 255, 0)", ColorBrush = Brushes.Lime, IsSelected = true };
            var context = CreateContext(type);
            using var oldStarted = new ManualResetEventSlim();
            using var releaseOld = new ManualResetEventSlim();
            using var oldCompleted = new ManualResetEventSlim();
            var requests = new System.Collections.Concurrent.ConcurrentQueue<PreviewParameters>();
            context.Calculate = (parameters, token) =>
            {
                requests.Enqueue(parameters);
                bool zones = parameters.IncludeZones;
                double step = zones ? parameters.Selections.Single().SpacingMm
                    : parameters.BackgroundLayers.Single(layer => layer.Enabled).SpacingMm;
                bool held = !zones && step == 250;
                if (held) { oldStarted.Set(); releaseOld.Wait(TimeSpan.FromSeconds(8)); }
                var plan = new ReinforcementPlan();
                Add(zones ? "zone" : "background", zones ? "Zones" : "Background", 2, 1000);
                Add(zones ? "local-u" : "main-u", "PerimeterU", 1, 2000);
                plan.ReferenceBars.Add(Bar(zones ? "existing-local-u" : "existing-background", zones ? "PerimeterU" : "Background", 3000));
                if (held) oldCompleted.Set();
                return plan;

                void Add(string key, string component, int count, double y)
                {
                    for (int index = 0; index < count; index++) plan.Bars.Add(Bar(key + index, component, y + index * 200));
                    var first = plan.Bars[plan.Bars.Count - count];
                    plan.Groups.Add(new BarGroup { SpecKey = first.SpecKey, ColorKey = first.ColorKey, Start = first.Start, End = first.End,
                        Placement = first.Placement, Count = count, SpacingMm = 200, PreviewPurpose = first.PreviewPurpose });
                }
                PlannedBar Bar(string key, string component, double y) => new PlannedBar
                {
                    SpecKey = key, ColorKey = color.Color, Start = new Point2(step, y), End = new Point2(11000, y),
                    PreviewPurpose = zones ? PreviewPurpose.Zones : PreviewPurpose.Background,
                    Placement = new BarPlacement { TypeKey = "test", Component = component, ModelDiameterMm = 12,
                        ElevationMm = -(parameters.TopCoverMm.Value + 6), Distribution = new Point3(0, 1, 0) }
                };
            };
            var settings = new SlabReinforcementSettings();
            var window = MakeWindow(settings);
            ResetComponents(window);
            var lower = (BackgroundLayerStat)((DataGrid)window.FindName("BackgroundDataGrid")).Items[0];
            lower.Enabled = true; lower.SpacingText = "bad";
            var perimeter = (CheckBox)window.FindName("CheckBox_PerimeterEnabled");
            perimeter.IsChecked = true;
            var perimeterType = (ComboBox)window.FindName("ComboBox_PerimeterType");
            perimeterType.SelectedItem = null;
            var outer = (CheckBox)window.FindName("CheckBox_PerimeterOuter");
            var openings = (CheckBox)window.FindName("CheckBox_PerimeterOpenings");
            outer.IsChecked = false; openings.IsChecked = false;
            var tabs = (TabControl)window.FindName("ComponentsTabs");
            var canvas = (SlabPreviewCanvas)window.FindName("PreviewCanvas");
            var confirm = (Button)window.FindName("Button_Confirm");
            var summary = (TextBlock)window.FindName("PlanSummary");
            window.Show();
            await Ready(); CheckScope(true);
            PreviewParameters initialScope = requests.Last();
            Expect(initialScope.IncludeZones && !initialScope.BackgroundLayers.Any(layer => layer.Enabled) && !initialScope.Perimeter.Enabled,
                "Zones must not create enabled background/global U preferences or validate their unrelated errors.");
            Expect(initialScope.BackgroundLayers.Count == 4 && initialScope.BackgroundLayers[0].SpecKey == "test"
                && double.IsNaN(initialScope.BackgroundLayers[0].SpacingMm),
                "Disabled background descriptors retain type-only placement data, and invalid spacing must never become a fake 200 mm grid.");
            tabs.SelectedItem = window.FindName("BackgroundTab");
            Expect(!confirm.IsEnabled && canvas.DisplayedPlan == null && ReadyPlan() == null,
                "Switching tabs must immediately reject the previous ready plan before validation/calculation.");
            lower.SpacingText = "200"; perimeterType.SelectedItem = type; outer.IsChecked = true; openings.IsChecked = true;
            var direction = (ComboBox)window.FindName("ComboBox_Direction");
            var merge = (TextBox)window.FindName("TextBox_ZoneMergeTolerance");
            var round = (TextBox)window.FindName("TextBox_RoundIncrement");
            direction.SelectedItem = null; merge.Text = "bad"; round.Text = "bad"; color.SpacingText = "bad";
            await Ready(); CheckScope(false);
            Expect(!requests.Last().IncludeZones && requests.Last().Selections.Count == 0,
                "Background must ignore missing zone direction and invalid selected zone spacing/rounding/merge.");
            tabs.SelectedItem = window.FindName("ZonesTab");
            Expect(!confirm.IsEnabled && canvas.DisplayedPlan == null, "Zone validation must become active when its tab is selected.");
            direction.SelectedItem = "Низ X"; merge.Text = "600"; round.Text = "10"; color.SpacingText = "200";
            await Ready(); CheckScope(true);
            var columnEvidence = new List<object>();
            foreach (int width in new[] { 1280, 1024 })
            {
                window.Width = width; window.Height = width == 1024 ? 640 : 850;
                tabs.SelectedItem = window.FindName("BackgroundTab"); await Ready();
                await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                window.UpdateLayout(); CheckScope(false);
                var grid = (DataGrid)window.FindName("BackgroundDataGrid");
                double[] widths = grid.Columns.Select(column => column.ActualWidth).ToArray();
                columnEvidence.Add(new { Width = width, ColumnWidths = widths, GridWidth = grid.ActualWidth });
                Expect(Math.Abs(widths[0] - 28) < .1 && Math.Abs(widths[1] - 64) < .1 && Math.Abs(widths[3] - 94) < .1,
                    "Settled background columns must retain 28/64/94 px after switching tabs.");
                SaveImage(window, "tab-background-" + width + ".png");
                tabs.SelectedItem = window.FindName("ZonesTab"); await Ready();
                await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                window.UpdateLayout(); CheckScope(true); SaveImage(window, "tab-zones-" + width + ".png");
            }
            File.WriteAllText(Path.Combine(_outputDirectory, "tab-column-layout.json"), JsonSerializer.Serialize(columnEvidence, new JsonSerializerOptions { WriteIndented = true }));
            tabs.SelectedItem = window.FindName("BackgroundTab");
            lower.SpacingText = "250";
            await Until(() => oldStarted.IsSet, "Old background calculation did not start.");
            tabs.SelectedItem = window.FindName("ZonesTab");
            Expect(!confirm.IsEnabled && canvas.DisplayedPlan == null && summary.Text.Length == 0,
                "A tab change during calculation must clear stale geometry and acceptance immediately.");
            color.SpacingText = "175";
            await Ready(); CheckScope(true);
            releaseOld.Set(); await Until(() => oldCompleted.IsSet, "Old background calculation did not finish."); await Task.Delay(80);
            CheckScope(true);
            Expect(ReadyPlan().Bars.All(bar => bar.Start.X == 175), "Cancelled background work must not replace the newest zone plan.");
            direction.SelectedItem = "Низ Y"; await Ready(); CheckScope(true);
            Expect(requests.Last().Direction == "Низ Y" && ReferenceEquals(tabs.SelectedItem, window.FindName("ZonesTab")),
                "A child ComboBox selection must preserve its active creation tab.");
            var cover = (TextBox)window.FindName("TextBox_TopCover");
            cover.Text = "bad";
            Expect(!confirm.IsEnabled && canvas.DisplayedPlan == null && ReadyPlan() == null, "Invalid common inputs clear the ready plan.");
            tabs.SelectedItem = window.FindName("BackgroundTab");
            Expect(!confirm.IsEnabled && canvas.DisplayedPlan == null, "A tab change must not bypass invalid common covers.");
            lower.SpacingText = "300"; cover.Text = "30"; await Ready(); CheckScope(false);
            Expect(ReadyPlan().Bars.All(bar => bar.Start.X == 300 && bar.Placement.ElevationMm == -36),
                "Current background settings must reach the recalculated plan.");
            ((TextBox)window.FindName("TextBox_PerimeterSpacing")).Text = "275";
            tabs.SelectedItem = window.FindName("ZonesTab"); await Ready(); CheckScope(true);

            string settingsPath = SlabReinforcementSettings.GetSettingsFilePath();
            byte[] savedSettings = File.Exists(settingsPath) ? File.ReadAllBytes(settingsPath) : null;
            try
            {
                ReinforcementPlan zonePlan = ReadyPlan();
                window.Hide();
                _ = window.Dispatcher.BeginInvoke(new Action(() => confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));
                Expect(window.ShowDialog() == true && ReferenceEquals(window.AcceptedPlan, zonePlan)
                    && window.AcceptedParameters.CreationScope == ReinforcementCreationScope.Zones,
                    "The real OK button must accept only the current zones plan.");
                Expect(window.AcceptedPlan.Bars.All(bar => bar.PreviewPurpose == PreviewPurpose.Zones), "Zones OK must contain no new background/main U.");
                var restored = SlabReinforcementSettings.LoadSettings();
                Expect(restored.BackgroundLayers[0].Enabled && restored.BackgroundLayers[0].SpacingMm == "300"
                    && restored.Perimeter.Enabled && restored.Perimeter.Outer && restored.Perimeter.Openings && restored.Perimeter.SpacingMm == "275",
                    "Accepting zones must preserve enabled background and perimeter preferences from their editors.");
                var backgroundWindow = MakeWindow(restored);
                var backgroundTabs = (TabControl)backgroundWindow.FindName("ComponentsTabs");
                backgroundTabs.SelectedItem = backgroundWindow.FindName("BackgroundTab");
                var backgroundConfirm = (Button)backgroundWindow.FindName("Button_Confirm");
                backgroundWindow.Show(); await Until(() => backgroundConfirm.IsEnabled, "Restored background preferences did not calculate.");
                backgroundWindow.Hide();
                _ = backgroundWindow.Dispatcher.BeginInvoke(new Action(() => backgroundConfirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));
                Expect(backgroundWindow.ShowDialog() == true && backgroundWindow.AcceptedParameters.CreationScope == ReinforcementCreationScope.Background
                    && !backgroundWindow.AcceptedParameters.IncludeZones
                    && backgroundWindow.AcceptedPlan.Bars.All(bar => bar.PreviewPurpose == PreviewPurpose.Background),
                    "The real OK button must accept only background/main U on its tab.");
                var restoredAgain = SlabReinforcementSettings.LoadSettings();
                Expect(restoredAgain.SelectedReinforcementDirection == "Низ Y" && restoredAgain.ColorSettings.Single().Spacing == 175
                    && restoredAgain.AutomaticZoneEnds, "Background acceptance must preserve the independent zone preferences.");
            }
            finally
            {
                if (savedSettings == null) File.Delete(settingsPath); else File.WriteAllBytes(settingsPath, savedSettings);
                releaseOld.Set(); if (window.IsVisible) window.Close();
            }
            File.WriteAllText(Path.Combine(_outputDirectory, "tab-preview.txt"),
                "PASS: active-tab requests and real OK acceptance; inactive validation ignored; settings retained; tab switching recalculates; stale background result rejected after zone switch; latest edits and common errors checked; 1280/1024 columns settled.");

            SlabReinforcementWPF MakeWindow(SlabReinforcementSettings preference) => new SlabReinforcementWPF(new List<ColorStat> { color },
                new List<Autodesk.Revit.DB.Structure.RebarBarType> { type }, context, preference)
                { ShowInTaskbar = false, ShowActivated = false, Left = -20000, Top = -20000, WindowStartupLocation = WindowStartupLocation.Manual };
            void CheckScope(bool zones)
            {
                var ready = ReadyPlan(); var visible = canvas.DisplayedPlan;
                PreviewParameters parameters = (PreviewParameters)typeof(SlabReinforcementWPF).GetField("_readyParameters", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window);
                Expect(parameters.CreationScope == (zones ? ReinforcementCreationScope.Zones : ReinforcementCreationScope.Background), "UI requests must always carry an explicit creation scope.");
                Expect(ready != null && visible != null && ready.Bars.Count == 3 && visible.Bars.Count == 3 && visible.ReferenceBars.Count == 1
                    && ready.Bars.All(bar => bar.PreviewPurpose == (zones ? PreviewPurpose.Zones : PreviewPurpose.Background)),
                    "The full ready/accepted plan must itself contain only the active tab's new bars, rather than hiding unwanted creation.");
                Expect(summary.Text.Contains("Новых стержней: 3") && summary.Text.Contains("Учтено существующих: 1")
                    && summary.Text.Contains("Зон:") == zones, "Counts must describe the active operation.");
                Expect(((TextBlock)window.FindName(zones ? "PreviewLegendZones" : "PreviewLegendBackground")).IsVisible
                    && !((TextBlock)window.FindName(zones ? "PreviewLegendBackground" : "PreviewLegendZones")).IsVisible,
                    "Only the active operation's legend may be visible.");
            }
            ReinforcementPlan ReadyPlan() => (ReinforcementPlan)typeof(SlabReinforcementWPF).GetField("_readyPlan", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window);
            Task Ready() => Until(() => confirm.IsEnabled, "Preview did not become ready.");
            static async Task Until(Func<bool> predicate, string failure)
            {
                var timer = Stopwatch.StartNew();
                while (!predicate() && timer.Elapsed < TimeSpan.FromSeconds(6)) await Task.Delay(25);
                Expect(predicate(), failure);
            }
        }

        private static async Task VerifyAutomaticLengthUi()
        {
            foreach (bool hasSource in new[] { true, false })
            {
                var type = new Autodesk.Revit.DB.Structure.RebarBarType { Name = "ø12 A500C" };
                var context = CreateContext(type);
                if (!hasSource) context.Faces.Clear();
                var requests = new System.Collections.Concurrent.ConcurrentQueue<PreviewParameters>();
                context.Calculate = (parameters, token) =>
                {
                    requests.Enqueue(parameters);
                    return new ReinforcementPlan { Bars = new List<PlannedBar> {
                        new PlannedBar { Start = new Point2(500, 500), End = new Point2(1500, 500),
                            PreviewPurpose = hasSource ? PreviewPurpose.Zones : PreviewPurpose.Background } } };
                };
                var settings = new SlabReinforcementSettings { Perimeter = new PerimeterReinforcementSettings { LegLengthMm = "bad" } };
                var window = new SlabReinforcementWPF(hasSource ? new List<ColorStat> {
                    new ColorStat { Color = "RGB(1, 2, 3)", IsSelected = true } } : new List<ColorStat>(),
                    new List<Autodesk.Revit.DB.Structure.RebarBarType> { type }, context, settings)
                { ShowInTaskbar = false, ShowActivated = false, Left = -20000, Top = -20000, WindowStartupLocation = WindowStartupLocation.Manual };
                Expect(window.FindName("TextBox_ZoneEndLeg") == null && window.FindName("TextBox_PerimeterLeg") == null,
                    "Zone and global P length must be automatic; neither manual leg editor may remain.");
                Expect(typeof(SlabReinforcementSettings).GetProperty("ZoneEndLegLength") == null,
                    "Automatic zone length must not persist a new manual-leg setting.");
                ResetComponents(window);
                ((CheckBox)window.FindName("CheckBox_PerimeterEnabled")).IsChecked = !hasSource;
                foreach (string name in new[] { "TextBlock_ZoneLengthPolicy", "TextBlock_PerimeterLengthPolicy" })
                    Expect(window.FindName(name) is TextBlock policy && policy.Text.Contains("100%")
                        && policy.Text.Contains("растянутых") && policy.Text.Contains("в одном сечении")
                        && policy.ToolTip is string explanation && explanation.Contains("бетону")
                        && explanation.Contains("классу") && explanation.Contains("диаметру"),
                        "Both P modes must explain the automatic lap policy: " + name);
                window.Show();
                await Task.Delay(500);
                Expect(requests.Count > 0 && ((Button)window.FindName("Button_Confirm")).IsEnabled,
                    "Old manual length settings must not block automatic length calculation, source=" + hasSource);
                PreviewParameters captured = requests.Last();
                var automatic = typeof(PreviewParameters).GetProperty("AutomaticULegLength");
                Expect(automatic != null && (bool)automatic.GetValue(captured)
                    && captured.IncludeZones == hasSource && captured.Perimeter.Enabled == !hasSource,
                    "The UI must explicitly request automatic length for zone-only and standalone global P operations.");
                Expect(!(bool)automatic.GetValue(new PreviewParameters()), "Legacy non-UI parameter callers must retain their opt-in contract.");
                ((ComboBox)window.FindName("ComboBox_ConcreteClass")).SelectedItem = null;
                Expect(!((Button)window.FindName("Button_Confirm")).IsEnabled,
                    "Both automatic length modes must require an explicit concrete class, source=" + hasSource);
                window.Close();
            }
        }

        private static async Task VerifyPropertyLayout()
        {
            var type = new Autodesk.Revit.DB.Structure.RebarBarType { Name = "ø16 A500C · длинное наименование типа арматуры для проверки ширины таблицы" };
            var color = new ColorStat { Color = "RGB(1, 2, 3)", IsSelected = true, ColorBrush = Brushes.SteelBlue };
            var context = CreateContext(type);
            var settings = new SlabReinforcementSettings();
            var window = new SlabReinforcementWPF(new List<ColorStat> { color },
                new List<Autodesk.Revit.DB.Structure.RebarBarType> { type }, context, settings)
            { ShowInTaskbar = false, ShowActivated = false, Left = -20000, Top = -20000, WindowStartupLocation = WindowStartupLocation.Manual };
            Expect(window.FindName("CheckBox_ZoneCreatePerimeter") == null && window.FindName("CheckBox_IncludeZones") == null
                && window.FindName("CheckBox_CalculationAxesConfirmed") == null && window.FindName("ComboBox_ZoneEndType") == null,
                "Zones must not expose duplicate global perimeter, enable/axes acknowledgments or an independent P type.");
            var zoneEnds = (CheckBox)window.FindName("CheckBox_ZoneEndsInPerimeter");
            var lengthPolicy = (TextBlock)window.FindName("TextBlock_ZoneLengthPolicy");
            Expect(zoneEnds.IsChecked == true, "Manual zones must default to automatic P endings.");
            ResetComponents(window);
            color.SelectedRebarType = type; color.SpacingText = "200"; color.IsSelected = true;
            ((ComboBox)window.FindName("ComboBox_PerimeterType")).SelectedItem = null;
            ((TextBox)window.FindName("TextBox_PerimeterSpacing")).Text = "bad";
            var captured = Capture(window);
            Expect(captured.Valid && captured.Parameters.UsePerimeterUForZoneEnds && captured.Parameters.IncludeZones
                && captured.Parameters.CalculationAxesConfirmed && !captured.Parameters.Perimeter.Enabled && captured.Parameters.AutomaticULegLength
                && captured.Parameters.Selections.Single().SpecKey == "test" && captured.Parameters.Selections.Single().SpacingMm == 200,
                "Zone endings must use zone assignments and automatic length without global perimeter settings or extra acknowledgments.");
            color.IsSelected = false;
            Expect(!Capture(window).Valid, "An attached underlay with no selected colors must remain an explicit validation error.");
            color.IsSelected = true;
            zoneEnds.IsChecked = false;
            Expect(Capture(window).Valid && !Capture(window).Parameters.UsePerimeterUForZoneEnds,
                "Straight-only zones must not require unused automatic-ending parameters.");
            zoneEnds.IsChecked = true;
            color.RequiredAreaText = "100";
            ((ComboBox)window.FindName("ComboBox_DemandMeaning")).SelectedValue = "AdditionalArea";
            Expect(!Capture(window).Parameters.UsePerimeterUForZoneEnds && !zoneEnds.IsEnabled,
                "Numeric demand modes must not apply manual automatic endings.");
            ((ComboBox)window.FindName("ComboBox_DemandMeaning")).SelectedValue = "Manual";
            Expect(zoneEnds.IsChecked == true && zoneEnds.IsEnabled, "Manual mode must restore the automatic-ending preference.");
            window.Show();
            await Task.Delay(1100);
            var tabs = (TabControl)window.FindName("ComponentsTabs");
            var commonViewport = (ScrollViewer)window.FindName("SettingsScrollViewer");
            var zoneViewport = (ScrollViewer)window.FindName("ZonesScrollViewer");
            ((Expander)window.FindName("SettingsExpander")).IsExpanded = true;
            foreach (int width in new[] { 1280, 1080, 1024 })
            {
                window.Width = width; window.Height = width == 1024 ? 640 : 850;
                tabs.SelectedItem = window.FindName("BackgroundTab");
                await Task.Delay(60); window.UpdateLayout();
                Expect(!((FrameworkElement)window.FindName("ZoneSettingsPanel")).IsVisible,
                    "Zone settings must belong only to the Zones tab.");
                var background = (DataGrid)window.FindName("BackgroundDataGrid");
                Expect(background.Columns.Sum(column => column.ActualWidth) <= background.ActualWidth + 0.1,
                    "Background table columns must fit long type names at " + width);
                foreach (string name in new[] { "ComboBox_ConcreteClass", "TextBox_TopCover", "TextBox_BottomCover", "TextBox_EdgeCover" })
                    Reach(name, commonViewport);
                SaveImage(window, "properties-common-" + width + ".png");
                tabs.SelectedItem = window.FindName("ZonesTab");
                await Task.Delay(60); window.UpdateLayout();
                var colors = (DataGrid)window.FindName("ColorStatsDataGrid");
                colors.BringIntoView(); window.UpdateLayout();
                Expect(colors.Columns.Where(column => column.Visibility == Visibility.Visible).Sum(column => column.ActualWidth) <= colors.ActualWidth + 0.1,
                    "Zone table columns must fit long type names at " + width);
                Expect(IsInsideElement(zoneViewport, colors), "Zone color table must remain fully reachable at " + width);
                SaveImage(window, "properties-color-table-" + width + ".png");
                foreach (string name in new[] { "ComboBox_Direction", "TextBox_ZoneMergeTolerance", "TextBox_RoundIncrement", "RadioButton_RoundCutLengths", "CheckBox_ZoneEndsInPerimeter", "TextBlock_ZoneLengthPolicy" })
                    Reach(name, zoneViewport);
                var canvas = (FrameworkElement)window.FindName("PreviewCanvas");
                double expandedHeight = canvas.ActualHeight;
                ((Expander)window.FindName("ZoneSettingsExpander")).IsExpanded = false; window.UpdateLayout();
                Expect(Math.Abs(canvas.ActualHeight - expandedHeight) < .1 && expandedHeight >= 150,
                    "Zone settings must not shrink the preview canvas at " + width + ": " + expandedHeight);
                ((Expander)window.FindName("ZoneSettingsExpander")).IsExpanded = true;
                lengthPolicy.BringIntoView(); window.UpdateLayout();
                SaveImage(window, "properties-zones-" + width + ".png");
            }
            window.Close();

            var serializer = new System.Xml.Serialization.XmlSerializer(typeof(SlabReinforcementSettings));
            using var legacyReader = new StringReader("<SlabReinforcementSettings />");
            var legacy = (SlabReinforcementSettings)serializer.Deserialize(legacyReader);
            Expect(legacy.AutomaticZoneEnds, "Existing settings must load automatic zone endings by default.");
            var saved = new SlabReinforcementSettings { AutomaticZoneEnds = false,
                Perimeter = new PerimeterReinforcementSettings { LegLengthMm = "600" } };
            using var writer = new StringWriter(); serializer.Serialize(writer, saved);
            using var reader = new StringReader(writer.ToString());
            var restored = (SlabReinforcementSettings)serializer.Deserialize(reader);
            var restoredWindow = new SlabReinforcementWPF(new List<ColorStat>(),
                new List<Autodesk.Revit.DB.Structure.RebarBarType> { type }, context, restored);
            Expect(((CheckBox)restoredWindow.FindName("CheckBox_ZoneEndsInPerimeter")).IsChecked == false && restored.Perimeter.LegLengthMm == "600",
                "Zone-ending preference must restore independently while old perimeter settings remain compatible.");
            restoredWindow.ApplyUnderlay(new List<ColorStat> { new ColorStat { Color = color.Color } }, context, "replacement.dxf");
            Expect(((CheckBox)restoredWindow.FindName("CheckBox_ZoneEndsInPerimeter")).IsChecked == false,
                "Replacing the underlay must preserve the user's zone-ending preference.");
            restoredWindow.Close();

            void Reach(string name, ScrollViewer viewport)
            {
                var control = (FrameworkElement)window.FindName(name);
                control.BringIntoView(); window.UpdateLayout();
                Expect(IsInsideWindow(window, control) && IsInsideElement(viewport, control), "Control must remain reachable in its own viewport: " + name);
            }
            static (bool Valid, PreviewParameters Parameters) Capture(SlabReinforcementWPF target)
            {
                object[] args = { null, null };
                bool valid = (bool)typeof(SlabReinforcementWPF).GetMethod("TryCaptureParameters", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, args);
                return (valid, (PreviewParameters)args[0]);
            }
        }
        private static async Task VerifyDiagnosticLabels()
        {
            const string identity = "c3de1f63-9bd7-4a68-8aa4-e65872e1a200";
            var type = new Autodesk.Revit.DB.Structure.RebarBarType { Name = "ø12 A500C" };
            PreviewContext context = CreateContext(type);
            context.Calculate = (parameters, token) => new ReinforcementPlan { Diagnostics = new List<PlanDiagnostic>
            {
                new PlanDiagnostic { Code = "FILE_SOURCE", SourceId = "Расчёт плиты.dxf", Message = "Проверьте слой расчётной подложки." },
                new PlanDiagnostic { Code = "ZONE_U_END_FIT", IsBlocking = true, SourceId = identity + "|RGB(12, 34, 56)",
                    Message = "В точке X=1200, Y=2400 мм не помещается П-окончание зоны: полка короче требуемой анкеровки." },
                new PlanDiagnostic { Code = "INTERNAL_SOURCE", SourceId = identity, Message = "Проверьте выбранную зону." }
            } };
            var window = new SlabReinforcementWPF(new List<ColorStat> { new ColorStat { Color = "RGB(12, 34, 56)", IsSelected = true } },
                new List<Autodesk.Revit.DB.Structure.RebarBarType> { type }, context, new SlabReinforcementSettings())
            { ShowInTaskbar = false, ShowActivated = false, Left = -20000, Top = -20000,
                WindowStartupLocation = WindowStartupLocation.Manual, Width = 1024, Height = 640 };
            ResetComponents(window);
            window.Show();
            var diagnostics = (ItemsControl)window.FindName("DiagnosticsList");
            var timeout = Stopwatch.StartNew();
            while (!diagnostics.Items.Cast<string>().Any(item => item.Contains("X=1200")) && timeout.Elapsed < TimeSpan.FromSeconds(5))
                await Task.Delay(30);
            string[] rendered = diagnostics.Items.Cast<string>().ToArray();
            File.WriteAllLines(Path.Combine(_outputDirectory, "diagnostic-labels.txt"), rendered);
            SaveImage(window, "diagnostics-1024.png");
            Expect(rendered.FirstOrDefault()?.Contains("X=1200") == true,
                "Blocking diagnostics must appear before earlier informational messages from the planner.");
            Expect(rendered.Any(item => item.Contains("X=1200") && item.Contains("RGB(12, 34, 56)")),
                "Zone diagnostics must keep the actionable message, row coordinates and color.");
            Expect(rendered.All(item => !item.Contains(identity)), "Internal source GUIDs must not leak into user-facing diagnostic labels.");
            Expect(rendered.Any(item => item.Contains("Расчёт плиты.dxf")), "Meaningful source filenames must remain visible.");
            window.Close();
        }

        private static async Task VerifyDiagnosticPresentation()
        {
            const string inputWarning = "Подложка содержит незамкнутые контуры: 2.";
            var essential = new[]
            {
                ("SINGLE_ROW_ZONES_SKIPPED", "Одиночные ряды пропущены: 23."),
                ("ZONE_ENDS_IN_PERIMETER_U", "Концы зон с П-стержнями: 31. Прочность соединения нужно проверить отдельно."),
                ("MANUAL_PLACEMENT", "Площадь арматуры при ручном назначении не проверяется."),
                ("BACKGROUND_DETAILING", "Анкеровку фона и стыки длинных стержней нужно задать отдельно."),
                ("COMPOSITE_BAR_COLLISION", "Пересекающиеся стержни: 2 пары."),
                ("FINAL_BAR_COLLISION", "Пересечение рядов в выбранных зонах: 1 пара."),
                ("ZONE_PATH_BAND_UNAVAILABLE", "Границы части зон не удалось состыковать. Положения стержней сохранены."),
                ("PERIMETER_SPLICES", "Места стыков продольного обрамления нужно задать отдельно."),
                ("BACKGROUND_SUFFICIENT", "Фона достаточно по площади. Анкеровку нужно проверить отдельно.")
            };
            var routine = new[]
            {
                "BACKGROUND_REUSED", "PERIMETER_U_LENGTH", "ZONE_U_LENGTH", "ZONE_ENDS_REUSED", "PERIMETER_REUSED",
                "PERIMETER_MANUAL_COMPLETION", "ZONE_EDGE_EXACT_LENGTH", "SUPPLEMENTAL_ROWS_CREATED", "ROWS_EXTENDED_FOR_COVERAGE"
            };
            var type = new Autodesk.Revit.DB.Structure.RebarBarType { Name = "ø12 A500C" };
            var context = CreateContext(type);
            context.Warnings.Add(inputWarning);
            using var oldStarted = new ManualResetEventSlim();
            using var releaseOld = new ManualResetEventSlim();
            using var oldCompleted = new ManualResetEventSlim();
            context.Calculate = (parameters, token) =>
            {
                bool zones = parameters.CreationScope == ReinforcementCreationScope.Zones;
                bool held = !zones && parameters.TopCoverMm == 45;
                if (held) { oldStarted.Set(); releaseOld.Wait(TimeSpan.FromSeconds(8)); }
                var result = new ReinforcementPlan();
                var placement = new BarPlacement { Component = zones ? "Zones" : "Background", TypeKey = "test", ModelDiameterMm = 12, ElevationMm = -41 };
                result.Bars.Add(new PlannedBar { SpecKey = "current", Start = new Point2(500, 1000), End = new Point2(10000, 1000), Placement = placement });
                if (parameters.TopCoverMm < 30)
                {
                    result.Diagnostics.AddRange(routine.Select((code, index) => new PlanDiagnostic
                    { Code = code, Message = "Расчётные сведения " + (index + 1) + ": длина 1030 мм.", SourceId = code + ".dxf" }));
                    result.Diagnostics.AddRange(essential.Select(item => new PlanDiagnostic
                    { Code = item.Item1, Message = item.Item2, SourceId = item.Item1 + ".dxf" }));
                    result.Diagnostics.Add(new PlanDiagnostic { Code = "UNRECOGNIZED_WARNING", Message = "Проверьте дополнительное условие из подложки.", SourceId = "unknown.dxf" });
                    result.Diagnostics.Add(new PlanDiagnostic { Code = "ZONE_U_LENGTH", IsBlocking = true,
                        Message = "Не указан класс арматуры для расчёта длины П.", SourceId = "blocking-length.dxf" });
                }
                else
                {
                    result.Diagnostics.Add(new PlanDiagnostic { Code = zones ? "PERIMETER_REUSED" : "BACKGROUND_REUSED",
                        Message = held ? "Устаревшие сведения о фоне." : zones ? "Сведения о зонах после правки." : "Сведения о фоне после смены вкладки.",
                        SourceId = held ? "obsolete.dxf" : zones ? "current-zones.dxf" : "current-background.dxf" });
                    result.Diagnostics.Add(new PlanDiagnostic { Code = zones ? "ZONE_ENDS_IN_PERIMETER_U" : "BACKGROUND_DETAILING",
                        Message = zones ? "Концы зон с П-стержнями: 31. Прочность соединения нужно проверить отдельно."
                            : "Анкеровку и стыки фона нужно задать отдельно." });
                }
                if (held) oldCompleted.Set();
                return result;
            };
            var window = new SlabReinforcementWPF(new List<ColorStat> { new ColorStat { Color = "RGB(12, 34, 56)", IsSelected = true } },
                new List<Autodesk.Revit.DB.Structure.RebarBarType> { type }, context, new SlabReinforcementSettings())
            { ShowInTaskbar = false, ShowActivated = false, Left = -20000, Top = -20000,
                WindowStartupLocation = WindowStartupLocation.Manual, Width = 1024, Height = 640 };
            ResetComponents(window);
            ((BackgroundLayerStat)((DataGrid)window.FindName("BackgroundDataGrid")).Items[0]).Enabled = true;
            var main = (ItemsControl)window.FindName("DiagnosticsList");
            var details = (ItemsControl)window.FindName("ContextWarningsList");
            var expander = (Expander)window.FindName("ContextWarningsExpander");
            var status = (TextBlock)window.FindName("StatusText");
            var confirm = (Button)window.FindName("Button_Confirm");
            var cover = (TextBox)window.FindName("TextBox_TopCover");
            var tabs = (TabControl)window.FindName("ComponentsTabs");
            var evidence = new List<object>();
            try
            {
                window.Show();
                await Until(() => Main().Any(text => text.Contains("blocking-length.dxf")), "The mixed diagnostic fixture did not arrive.");
                foreach (var item in essential)
                    Expect(Main().Any(text => text.Contains(item.Item1 + ".dxf")), "Essential warning was hidden: " + item.Item1);
                foreach (string code in routine)
                {
                    Expect(Details().Any(text => text.Contains(code + ".dxf")), "Routine calculation information was lost: " + code);
                    Expect(!Main().Any(text => text.Contains("[" + code + ".dxf]")), "Routine calculation information remained in the main list: " + code);
                }
                Expect(Main().Any(text => text.Contains("unknown.dxf")), "Unknown diagnostic codes must remain visible by default.");
                Expect(Main().First().Contains("blocking-length.dxf"), "Blocking errors must remain first, including a code normally used for details.");
                Expect(Main().All(text => !text.StartsWith("Ошибка: ", StringComparison.Ordinal)), "The error status must not be repeated as a prefix on each diagnostic.");
                Expect(status.Text == "Создание недоступно" && !confirm.IsEnabled, "A blocking length diagnostic must keep creation unavailable.");
                Expect(Equals(expander.Header, "Подробности") && !expander.IsExpanded, "Additional information must start collapsed under the plain Details label.");
                Expect(Details().Contains(inputWarning), "The source warning must be preserved alongside calculation details.");
                Record("mixed-blocking-and-informational");
                SaveImage(window, "diagnostic-presentation-1024.png");

                expander.IsExpanded = true;
                cover.Text = "bad";
                Expect(status.Text == "Проверьте параметры" && !confirm.IsEnabled, "Invalid edited cover must invalidate the accepted calculation.");
                Cleared("invalid-input");
                cover.Text = "40";
                Cleared("valid-input-calculating");
                await Until(() => confirm.IsEnabled && Details().Any(text => text.Contains("current-zones.dxf")), "Corrected inputs did not replace zone details.");
                Expect(status.Text == "Готово к созданию", "Ready status must stay plain even with zone P endings.");
                Expect(!expander.IsExpanded && Details().Contains(inputWarning), "Fresh details must remain collapsed and preserve source warnings.");
                Record("corrected-zone-parameters");

                expander.IsExpanded = true;
                tabs.SelectedItem = window.FindName("BackgroundTab");
                Cleared("background-tab-calculating");
                await Until(() => confirm.IsEnabled && Details().Any(text => text.Contains("current-background.dxf")), "Background tab did not publish its current details.");
                Expect(!Details().Any(text => text.Contains("current-zones.dxf")), "Zone details must not survive a background calculation.");
                Record("background-current");

                cover.Text = "45";
                Cleared("held-background-calculating");
                await Until(() => oldStarted.IsSet, "The obsolete background calculation did not start.");
                tabs.SelectedItem = window.FindName("ZonesTab");
                Cleared("zones-tab-calculating");
                await Until(() => confirm.IsEnabled && Details().Any(text => text.Contains("current-zones.dxf")), "Current zone details did not replace the held background calculation.");
                releaseOld.Set();
                await Until(() => oldCompleted.IsSet, "The obsolete background worker did not complete.");
                await Task.Delay(100);
                Expect(!Details().Any(text => text.Contains("obsolete.dxf") || text.Contains("current-background.dxf")),
                    "A cancelled worker must not restore details from a previous tab.");
                Expect(Details().Contains(inputWarning) && status.Text == "Готово к созданию", "Current zone state and original source warnings must survive a late worker.");
                Record("obsolete-details-rejected");
                File.WriteAllText(Path.Combine(_outputDirectory, "diagnostic-presentation.json"), JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
            }
            finally { releaseOld.Set(); window.Close(); }

            string[] Main() => main.Items.Cast<string>().ToArray();
            string[] Details() => details.Items.Cast<string>().ToArray();
            void Cleared(string step)
            {
                Expect(!expander.IsExpanded, "Invalidation must close previously expanded details.");
                Expect(Details().SequenceEqual(new[] { inputWarning }), "Invalidation must immediately clear calculated details while keeping input warnings.");
                Expect(!confirm.IsEnabled, "Creation must wait for the current calculation.");
                Record(step);
            }
            void Record(string step) => evidence.Add(new { step, status = status.Text, main = Main(), details = Details(), expanded = expander.IsExpanded });
            static async Task Until(Func<bool> condition, string message)
            { var timer = Stopwatch.StartNew(); while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(6)) await Task.Delay(25); Expect(condition(), message); }
        }

        private static async Task VerifyCancellation()
        {
            var evidence = new List<object>();
            var failures = new List<string>();
            foreach (string mode in new[] { "initial-modal", "reopened-modal", "modeless" })
            foreach (string state in new[] { "invalid-input", "blocking-plan", "calculating" })
            foreach (bool escape in new[] { false, true })
            {
                string name = mode + "/" + state + "/" + (escape ? "Escape" : "button");
                var type = new Autodesk.Revit.DB.Structure.RebarBarType { Name = "cancel-test" };
                var started = new ManualResetEventSlim();
                var release = new ManualResetEventSlim();
                var completed = new ManualResetEventSlim();
                bool workerCancelled = false;
                PreviewContext context = CreateContext(type);
                context.Faces.Clear();
                context.Calculate = (parameters, token) =>
                {
                    using (token.Register(() => workerCancelled = true))
                    {
                        started.Set();
                        if (state == "calculating") release.Wait(TimeSpan.FromSeconds(10));
                        completed.Set();
                        var plan = new ReinforcementPlan();
                        plan.Diagnostics.Add(new PlanDiagnostic { Code = "CANCEL_FIXTURE", IsBlocking = true, Message = "Проверка отмены при недоступном создании." });
                        return plan;
                    }
                };
                var settings = new SlabReinforcementSettings();
                settings.Perimeter.Enabled = false;
                var window = new SlabReinforcementWPF(new List<ColorStat>(),
                    new List<Autodesk.Revit.DB.Structure.RebarBarType> { type }, context, settings)
                {
                    ShowInTaskbar = false, ShowActivated = false, Left = -20000, Top = -20000,
                    WindowStartupLocation = WindowStartupLocation.Manual
                };
                ResetComponents(window);
                var layer = ((DataGrid)window.FindName("BackgroundDataGrid")).Items.Cast<BackgroundLayerStat>().First();
                layer.SelectedRebarType = type; layer.SpacingText = state == "invalid-input" ? "bad" : "200"; layer.Enabled = true;
                ((TextBox)window.FindName("TextBox_ZoneMergeTolerance")).Text = "0";
                ((TextBox)window.FindName("TextBox_RoundIncrement")).Text = "10";
                bool closed = false, wasVisibleAfterCancel = false;
                string error = null;
                bool? beforeResult = null, afterResult = null;
                window.Closed += (sender, args) => closed = true;
                try
                {
                    if (mode == "reopened-modal")
                    {
                        _ = window.Dispatcher.BeginInvoke(new Action(window.RequestUnderlaySelection), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                        window.ShowDialog();
                        Expect(!closed && window.ConsumeUnderlaySelectionRequest(), "Underlay selection must hide and preserve the reusable test window.");
                        window.ResumePreview();
                    }
                    bool Ready() => state == "invalid-input" || state == "calculating" && started.IsSet
                        || state == "blocking-plan" && ((TextBlock)window.FindName("StatusText")).Text == "Создание недоступно";
                    void Cancel()
                    {
                        try
                        {
                            Expect(!((Button)window.FindName("Button_Confirm")).IsEnabled, "Cancel fixture must have creation disabled.");
                            beforeResult = window.DialogResult;
                            if (escape)
                            {
                                var scope = PresentationSource.FromVisual(window);
                                Expect(AccessKeyManager.IsKeyRegistered(scope, "\u001b"), "The current window must register its Escape cancellation access key.");
                                AccessKeyManager.ProcessKey(scope, "\u001b", false);
                            }
                            else
                            {
                                var cancel = VisualButtons(window).Single(button => Equals(button.Content, "Отмена"));
                                typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(cancel, null);
                            }
                        }
                        catch (Exception exception) { error = (exception.InnerException ?? exception).Message; }
                        wasVisibleAfterCancel = window.IsVisible;
                        afterResult = window.DialogResult;
                        if (window.IsVisible) window.Close();
                    }
                    if (mode == "modeless")
                    {
                        window.Show();
                        var timer = Stopwatch.StartNew();
                        while (!Ready() && timer.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(20);
                        Expect(Ready(), "Cancel fixture did not reach " + state + ".");
                        Cancel();
                    }
                    else
                    {
                        var stopwatch = Stopwatch.StartNew();
                        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
                        timer.Tick += (sender, args) =>
                        {
                            if (!Ready() && stopwatch.Elapsed < TimeSpan.FromSeconds(10)) return;
                            timer.Stop();
                            if (!Ready()) { error = "Cancel fixture timed out."; window.Close(); return; }
                            Cancel();
                        };
                        timer.Start();
                        try { window.ShowDialog(); }
                        finally { timer.Stop(); }
                    }
                    release.Set();
                    var finish = Stopwatch.StartNew();
                    while (started.IsSet && !completed.IsSet && finish.Elapsed < TimeSpan.FromSeconds(2)) await Task.Delay(20);
                    await Task.Delay(40);
                    bool passed = error == null && closed && !wasVisibleAfterCancel && window.AcceptedPlan == null && window.AcceptedParameters == null
                        && (state != "calculating" || workerCancelled);
                    evidence.Add(new { Case = name, Passed = passed, BeforeDialogResult = beforeResult, AfterDialogResult = afterResult,
                        Error = error, WasVisibleAfterCancel = wasVisibleAfterCancel, Closed = closed, WorkerCancelled = workerCancelled,
                        AcceptedPlan = window.AcceptedPlan != null, AcceptedParameters = window.AcceptedParameters != null });
                    if (!passed) failures.Add(name + ": " + (error ?? "window stayed visible") + "; before DialogResult=" + beforeResult);
                }
                finally { release.Set(); if (window.IsVisible) window.Close(); }
            }
            File.WriteAllText(Path.Combine(_outputDirectory, "cancel-cases.json"), JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
            Expect(failures.Count == 0, "Cancellation failures: " + string.Join(" | ", failures));

            static IEnumerable<Button> VisualButtons(DependencyObject parent)
            {
                for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
                {
                    DependencyObject child = VisualTreeHelper.GetChild(parent, index);
                    if (child is Button button) yield return button;
                    foreach (Button nested in VisualButtons(child)) yield return nested;
                }
            }
        }

        private static async Task VerifyPerimeterSpacingModes()
        {
            var type = new Autodesk.Revit.DB.Structure.RebarBarType { Name = "ø12 A500C" };
            int existingDirections = 0;
            int edgeEvidenceMode = 0;
            var requests = new System.Collections.Concurrent.ConcurrentQueue<PreviewParameters>();
            PreviewContext context = CreateContext(type);
            context.Faces.Clear();
            context.Calculate = (parameters, token) =>
            {
                requests.Enqueue(parameters);
                var plan = new ReinforcementPlan();
                plan.Bars.Add(new PlannedBar { Start = new Point2(500, 500), End = new Point2(1000, 500),
                    Placement = new BarPlacement { Component = "PerimeterU" } });
                foreach (PreviewBackgroundLayer layer in parameters.BackgroundLayers)
                {
                    bool lowerX = layer.Direction == "Низ X", lowerY = layer.Direction == "Низ Y";
                    bool existing = lowerX && existingDirections >= 1 || lowerY && existingDirections >= 2;
                    if (!existing && !layer.Enabled) continue;
                    var bars = existing ? plan.ReferenceBars : plan.Bars;
                    for (int index = 0; index < 2; index++)
                        bars.Add(new PlannedBar { SpecKey = "Background|" + layer.Direction,
                            Start = new Point2(500, 800 + index * layer.SpacingMm), End = new Point2(4000, 800 + index * layer.SpacingMm),
                            Placement = new BarPlacement { Component = "Background" } });
                }
                if (edgeEvidenceMode > 0) plan.PerimeterUEdges.Add(new PerimeterUEdge { FollowsBackgroundRows = true });
                if (edgeEvidenceMode > 1) plan.PerimeterUEdges.Add(new PerimeterUEdge { FollowsBackgroundRows = false });
                return plan;
            };
            var settings = new SlabReinforcementSettings { Perimeter = new PerimeterReinforcementSettings { SpacingMm = "275" } };
            var window = new SlabReinforcementWPF(new List<ColorStat>(),
                new List<Autodesk.Revit.DB.Structure.RebarBarType> { type }, context, settings)
            {
                ShowInTaskbar = false, ShowActivated = false, Left = -20000, Top = -20000,
                WindowStartupLocation = WindowStartupLocation.Manual
            };
            var spacing = (TextBox)window.FindName("TextBox_PerimeterSpacing");
            var hint = (TextBlock)window.FindName("TextBlock_PerimeterDistribution");
            var confirm = (Button)window.FindName("Button_Confirm");
            var layers = ((DataGrid)window.FindName("BackgroundDataGrid")).Items.Cast<BackgroundLayerStat>().ToList();
            ResetComponents(window);
            foreach (BackgroundLayerStat layer in layers) { layer.SelectedRebarType = type; layer.SpacingText = "200"; }
            ((ComboBox)window.FindName("ComboBox_ConcreteClass")).SelectedItem = "B20";
            ((TextBox)window.FindName("TextBox_ZoneMergeTolerance")).Text = "0";
            ((TextBox)window.FindName("TextBox_RoundIncrement")).Text = "10";
            ((ComboBox)window.FindName("ComboBox_PerimeterType")).SelectedItem = type;
            ((CheckBox)window.FindName("CheckBox_PerimeterEnabled")).IsChecked = true;
            window.Show();
            await Wait();
            Expect(spacing.IsVisible && spacing.IsEnabled && spacing.Text == "275" && confirm.IsEnabled,
                "A no-background operation must restore the saved manual perimeter spacing in a visible editor.");
            spacing.Text = "175";
            await Wait();
            Expect(requests.Last().Perimeter.SpacingMm == 175, "The request must capture the user's manual perimeter spacing.");
            layers.Single(layer => layer.Direction == "Низ X").Enabled = true;
            await Wait();
            Expect(spacing.IsEnabled && spacing.Text == "175" && hint.Text.Contains("X") && hint.Text.Contains("вручную"),
                "One actual lower direction must keep manual spacing for the remaining edges.");
            var lowerY = layers.Single(layer => layer.Direction == "Низ Y");
            lowerY.SpacingText = "150"; lowerY.Enabled = true;
            await Wait();
            Expect(!spacing.IsEnabled && spacing.Text.Contains("X 200") && spacing.Text.Contains("Y 150") && hint.Text == "По нижнему фону",
                "Both actual lower directions must disable the editor and show their different nominal maximum spacings.");
            Expect(requests.Last().Perimeter.SpacingMm == 175, "Automatic display must not overwrite the captured manual value.");
            int stableRevision = (int)typeof(SlabReinforcementWPF).GetField("_revision", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window);
            await Task.Delay(400);
            Expect(stableRevision == (int)typeof(SlabReinforcementWPF).GetField("_revision", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window),
                "Displaying automatic spacing must not trigger an invalidation loop.");
            lowerY.SpacingText = "125";
            await Wait();
            Expect(!spacing.IsEnabled && spacing.Text.Contains("Y 125"), "Automatic display must follow a changed background spacing.");
            lowerY.Enabled = false;
            await Wait();
            Expect(spacing.IsEnabled && spacing.Text == "175", "Leaving automatic mode must restore the previous manual value.");
            foreach (BackgroundLayerStat layer in layers) layer.Enabled = layer.Direction.StartsWith("Верх", StringComparison.Ordinal);
            await Wait();
            Expect(spacing.IsEnabled && !hint.Text.Contains("По нижнему фону"), "Upper-only background must not lock perimeter spacing.");
            foreach (string invalid in new[] { "bad", "0", "-1", "NaN" })
            {
                spacing.Text = invalid;
                await Wait();
                Expect(!confirm.IsEnabled && spacing.IsEnabled, "Invalid manual spacing must block manual/partial framing: " + invalid);
            }
            existingDirections = 2;
            ((ComboBox)window.FindName("ComboBox_ConcreteClass")).SelectedItem = "B25";
            await Wait();
            Expect(confirm.IsEnabled && !spacing.IsEnabled && spacing.Text.Contains("Y 125") && double.IsNaN(requests.Last().Perimeter.SpacingMm),
                "Complete verified existing lower rows must enable automatic framing even when the stored manual value is invalid.");
            existingDirections = 1;
            ((ComboBox)window.FindName("ComboBox_ConcreteClass")).SelectedItem = "B30";
            Expect(spacing.IsEnabled && spacing.Text == "NaN", "Parameter changes must discard obsolete existing-background proof immediately.");
            await Wait();
            Expect(!confirm.IsEnabled && spacing.IsEnabled && hint.Text.Contains("X") && hint.Text.Contains("вручную"),
                "A partial existing lower background must require the manual value for remaining edges.");
            edgeEvidenceMode = 1;
            ((ComboBox)window.FindName("ComboBox_ConcreteClass")).SelectedItem = "B20";
            await Wait();
            Expect(confirm.IsEnabled && spacing.IsEnabled && spacing.Text == "NaN",
                "One actual lower direction may serve every selected oblique edge; unused invalid manual spacing must not block that valid plan.");
            edgeEvidenceMode = 2;
            ((ComboBox)window.FindName("ComboBox_ConcreteClass")).SelectedItem = "B15";
            await Wait();
            Expect(!confirm.IsEnabled && spacing.IsEnabled,
                "Actual manual-edge evidence must require valid manual spacing even beside automatically framed edges.");
            edgeEvidenceMode = 0;
            spacing.Text = "225";
            await Wait();
            Expect(confirm.IsEnabled && requests.Last().Perimeter.SpacingMm == 225, "A corrected partial-layout manual value must restore creation.");
            window.Width = 1024; window.Height = 640;
            window.UpdateLayout(); hint.BringIntoView(); window.UpdateLayout();
            Expect(IsInsideElement((ScrollViewer)window.FindName("BackgroundScrollViewer"), hint)
                && IsInsideElement((ScrollViewer)window.FindName("BackgroundScrollViewer"), spacing),
                "The partial-background spacing editor and explanation must both remain reachable at 1024x640.");
            SaveImage(window, "preview-perimeter-partial-1024.png");
            existingDirections = 0;
            ((ComboBox)window.FindName("ComboBox_ConcreteClass")).SelectedItem = "B25";
            await Wait();
            Expect(spacing.IsEnabled && spacing.Text == "225", "Removing actual lower rows must restore manual mode.");
            hint.BringIntoView(); window.UpdateLayout();
            Expect(IsInsideWindow(window, spacing) && IsInsideElement((ScrollViewer)window.FindName("BackgroundScrollViewer"), spacing)
                && IsInsideElement((ScrollViewer)window.FindName("BackgroundScrollViewer"), hint),
                "The manual perimeter spacing editor must remain reachable at 1024x640.");
            SaveImage(window, "preview-perimeter-manual-1024.png");
            existingDirections = 2;
            ((ComboBox)window.FindName("ComboBox_ConcreteClass")).SelectedItem = "B30";
            await Wait();
            hint.BringIntoView(); window.UpdateLayout();
            Expect(IsInsideWindow(window, spacing) && IsInsideElement((ScrollViewer)window.FindName("BackgroundScrollViewer"), hint),
                "The automatic perimeter spacing display and explanation must fit at 1024x640.");
            SaveImage(window, "preview-perimeter-automatic-1024.png");
            window.Close();

            // Discovery must also work on first open, before any successful preview exists.
            settings.Perimeter.SpacingMm = "bad";
            settings.Perimeter.Enabled = true;
            var initial = new SlabReinforcementWPF(new List<ColorStat>(),
                new List<Autodesk.Revit.DB.Structure.RebarBarType> { type }, context, settings)
            { ShowInTaskbar = false, ShowActivated = false, Left = -20000, Top = -20000, WindowStartupLocation = WindowStartupLocation.Manual };
            initial.Show();
            var timer = Stopwatch.StartNew();
            while (((ProgressBar)initial.FindName("CalculationProgress")).Visibility == Visibility.Visible && timer.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(30);
            Expect(((Button)initial.FindName("Button_Confirm")).IsEnabled && !((TextBox)initial.FindName("TextBox_PerimeterSpacing")).IsEnabled,
                "Initial complete-existing-background discovery must not deadlock behind an invalid manual field.");
            initial.Close();

            async Task Wait()
            {
                var timer = Stopwatch.StartNew();
                while (((ProgressBar)window.FindName("CalculationProgress")).Visibility == Visibility.Visible && timer.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(30);
                Expect(timer.Elapsed < TimeSpan.FromSeconds(10), "Perimeter mode calculation did not settle.");
                window.UpdateLayout();
            }
        }

        private static void ResetComponents(SlabReinforcementWPF window)
        {
            foreach (BackgroundLayerStat layer in ((DataGrid)window.FindName("BackgroundDataGrid")).Items) layer.Enabled = false;
            ((CheckBox)window.FindName("CheckBox_PerimeterEnabled")).IsChecked = false;
            ((ComboBox)window.FindName("ComboBox_DemandMeaning")).SelectedValue = "Manual";
        }

        private static async Task VerifyRealPlannerFixture()
        {
            const string leftColor = "RGB(255, 127, 127)";
            const string rightColor = "RGB(255, 191, 0)";
            var type = new Autodesk.Revit.DB.Structure.RebarBarType { Name = "ø16 A500C" };
            var colors = new List<ColorStat>
            {
                new ColorStat { Color = leftColor, ColorBrush = new SolidColorBrush(Color.FromRgb(255, 127, 127)) },
                new ColorStat { Color = rightColor, ColorBrush = new SolidColorBrush(Color.FromRgb(255, 191, 0)) }
            };
            PreviewContext context = CreateContext(type);
            context.HostName = "Расчётный пример · плита 12 000 × 8 000 мм · проём 2 000 × 2 000 мм";
            context.AxisDescription = "Локальные оси плиты · размеры в миллиметрах";
            context.Faces = new List<FaceInput>
            {
                new FaceInput
                {
                    Id = "left-demand", ColorKey = leftColor,
                    Vertices = new List<Point2> { new Point2(1500, 1800), new Point2(3500, 1800), new Point2(3500, 6200), new Point2(1500, 6200) }
                },
                new FaceInput
                {
                    Id = "right-demand", ColorKey = rightColor,
                    Vertices = new List<Point2> { new Point2(7500, 1800), new Point2(10000, 1800), new Point2(10000, 6200), new Point2(7500, 6200) }
                }
            };
            context.Calculate = (parameters, token) =>
            {
                var result = ReinforcementPlanner.Build(new PlanRequest
                {
                Slabs = context.Slabs,
                Faces = context.Faces.Where(face => parameters.Selections.Any(selection => selection.ColorKey == face.ColorKey)).ToList(),
                Specifications = parameters.Selections.Select(selection => new ReinforcementSpec
                {
                    Key = selection.SpecKey + "|" + selection.ColorKey,
                    ColorKey = selection.ColorKey,
                    MaterialKey = "A500C",
                    DiameterMm = 16,
                    SpacingMm = selection.SpacingMm,
                    AnchorageMm = 300
                }).ToList(),
                Options = new PlanOptions
                {
                    AlongX = parameters.Direction.EndsWith("X", StringComparison.Ordinal),
                    EdgeCoverMm = parameters.EdgeCoverMm,
                    MergeToleranceMm = parameters.MergeToleranceMm,
                    RoundIncrementMm = parameters.RoundIncrementMm,
                    UseCutLengths = parameters.UseCutLengths
                }
                }, token);
                // The production session tags the pure Core result before handing it to the UI.
                var placement = new BarPlacement { Component = "Zones", TypeKey = "test", ModelDiameterMm = 16 };
                foreach (PlannedBar bar in result.Bars) bar.Placement = placement;
                foreach (BarGroup group in result.Groups) group.Placement = placement;
                return result;
            };

            var window = new SlabReinforcementWPF(colors,
                new List<Autodesk.Revit.DB.Structure.RebarBarType> { type }, context)
            {
                ShowInTaskbar = false, ShowActivated = false, Left = -20000, Top = -20000,
                WindowStartupLocation = WindowStartupLocation.Manual
            };
            ResetComponents(window);
            ((ComboBox)window.FindName("ComboBox_Direction")).SelectedItem = "Низ X";
            ((ComboBox)window.FindName("ComboBox_ConcreteClass")).SelectedItem = "B25";
            ((TextBox)window.FindName("TextBox_EdgeCover")).Text = "25";
            ((TextBox)window.FindName("TextBox_ZoneMergeTolerance")).Text = "600";
            ((TextBox)window.FindName("TextBox_RoundIncrement")).Text = "10";
            ((RadioButton)window.FindName("RadioButton_RoundIncrement")).IsChecked = true;
            foreach (ColorStat color in colors)
            {
                color.SelectedRebarType = type;
                color.IsSelected = true;
                color.SpacingText = "200";
            }
            window.Show();
            var confirm = (Button)window.FindName("Button_Confirm");
            var timeout = Stopwatch.StartNew();
            while (!confirm.IsEnabled && timeout.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(50);
            var plan = (ReinforcementPlan)typeof(SlabReinforcementWPF)
                .GetField("_readyPlan", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window);
            Expect(plan != null && plan.CanCreate && confirm.IsEnabled, "The real planner fixture must produce a creatable plan.");
            Expect(plan.Bars.Any(bar => bar.ColorKey == leftColor) && plan.Bars.Any(bar => bar.ColorKey == rightColor),
                "Both demand colors must be represented by planned bars.");
            var summary = (TextBlock)window.FindName("PlanSummary");
            Expect(summary.Text.Contains("Зон: " + plan.Groups.Count) && summary.Text.Contains("Наборов: 0"),
                "Zone Path systems must have a separate summary count instead of being counted as ordinary bar sets.");
            SaveImage(window, "preview-real-planner.png");
            DirectoryInfo directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "SlabReinforcement.sln"))) directory = directory.Parent;
            Expect(directory != null, "Cannot locate the repository for the final preview artifact.");
            string artifactDirectory = Path.Combine(directory.FullName, "artifacts", "preview");
            Directory.CreateDirectory(artifactDirectory);
            string artifact = Path.Combine(artifactDirectory, "citrus-slab-preview.png");
            SaveImage(window, artifact);
            Console.WriteLine("Real planner: " + plan.Bars.Count + " bars; " + plan.Groups.Count + " groups. Preview: " + artifact);
            window.Width = 1024;
            window.Height = 640;
            window.UpdateLayout();
            VerifyPreviewNavigationLayout(window);
            Expect(IsInsideWindow(window, summary) && IsInsideElement((FrameworkElement)summary.Parent, summary),
                "The zone system summary must fit inside the preview footer at 1024x640.");
            SaveImage(window, "preview-real-planner-1024.png");
            window.Close();
        }

        private static async Task VerifyRealMvpFixture()
        {
            var backgroundType = new Autodesk.Revit.DB.Structure.RebarBarType { Name = "ø12 A500C" };
            var perimeterType = new Autodesk.Revit.DB.Structure.RebarBarType { Name = "ø8 A500C" };
            PreviewContext context = CreateContext(backgroundType);
            context.HostName = "Пример · 12 000 × 8 000 × 220 мм · проём 2 000 × 2 000 мм";
            context.Faces.Clear();
            context.RebarTypeKeys = new Dictionary<string, string>
            {
                { backgroundType.Name, "mvp-12" }, { perimeterType.Name, "mvp-8" }
            };
            ReinforcementPlan calculated = null;
            PreviewParameters calculatedParameters = null;
            context.Calculate = (parameters, token) =>
            {
                var result = new ReinforcementPlan();
                var configuredLayers = parameters.BackgroundLayers.Select(layer => new BackgroundLayer
                {
                    Key = "Background|" + layer.Direction, TypeKey = layer.SpecKey,
                    Top = layer.Direction.StartsWith("Верх", StringComparison.Ordinal),
                    AlongX = layer.Direction.EndsWith("X", StringComparison.Ordinal),
                    DiameterMm = 12, ModelDiameterMm = 12, SpacingMm = layer.SpacingMm
                }).ToList();
                var enabledKeys = new HashSet<string>(parameters.BackgroundLayers.Where(layer => layer.Enabled)
                    .Select(layer => "Background|" + layer.Direction));
                double topCover = parameters.TopCoverMm ?? context.TopCoverMm;
                double bottomCover = parameters.BottomCoverMm ?? context.BottomCoverMm;
                var backgroundRequest = new BackgroundRequest
                {
                    Slabs = context.Slabs, ThicknessMm = 220, TopCoverMm = topCover, BottomCoverMm = bottomCover,
                    SideCoverMm = parameters.EdgeCoverMm + 0.05,
                    Layers = configuredLayers.Where(layer => enabledKeys.Contains(layer.Key)).ToList(),
                    PlacementLayers = configuredLayers
                };
                var backgroundPlan = BackgroundPlanner.Build(backgroundRequest, token);
                Append(backgroundPlan);
                var perimeterRequest = new PerimeterRequest
                {
                    Slabs = context.Slabs, TypeKey = parameters.Perimeter.SpecKey,
                    DiameterMm = 8, ModelDiameterMm = 8, BendDiameterMm = 32,
                    ThicknessMm = 220, TopCoverMm = topCover, BottomCoverMm = bottomCover, SideCoverMm = parameters.EdgeCoverMm + 0.05,
                    Outer = parameters.Perimeter.Outer, Openings = parameters.Perimeter.Openings,
                    UseUbars = parameters.Perimeter.UseUbars, UseLongitudinalBars = parameters.Perimeter.UseLongitudinalBars,
                    LegLengthMm = parameters.Perimeter.LegLengthMm,
                    BackgroundPlan = backgroundPlan, BackgroundLayers = configuredLayers
                };
                if (parameters.Perimeter.Enabled) Append(PerimeterPlanner.Build(perimeterRequest, token));
                if (!result.Diagnostics.Any(diagnostic => diagnostic.IsBlocking))
                    result.Diagnostics.AddRange(CompositePlanValidator.Validate(result.Bars, token, allowBarIntersections: true));
                Interlocked.Exchange(ref calculatedParameters, parameters);
                Interlocked.Exchange(ref calculated, result);
                return result;

                void Append(ReinforcementPlan component)
                {
                    result.Bars.AddRange(component.Bars);
                    result.Groups.AddRange(component.Groups);
                    result.Diagnostics.AddRange(component.Diagnostics);
                    result.PerimeterUEdges.AddRange(component.PerimeterUEdges);
                }
            };
            var window = new SlabReinforcementWPF(new List<ColorStat>(),
                new List<Autodesk.Revit.DB.Structure.RebarBarType> { backgroundType, perimeterType }, context)
            {
                ShowInTaskbar = false, ShowActivated = false, Left = -20000, Top = -20000,
                WindowStartupLocation = WindowStartupLocation.Manual
            };
            ResetComponents(window);
            ((TextBox)window.FindName("TextBox_TopCover")).Text = "30";
            ((TextBox)window.FindName("TextBox_BottomCover")).Text = "40";
            ((TextBox)window.FindName("TextBox_EdgeCover")).Text = "32";
            ((TextBox)window.FindName("TextBox_ZoneMergeTolerance")).Text = "0";
            ((TextBox)window.FindName("TextBox_RoundIncrement")).Text = "10";
            foreach (BackgroundLayerStat layer in ((DataGrid)window.FindName("BackgroundDataGrid")).Items)
            {
                layer.SelectedRebarType = backgroundType;
                layer.SpacingText = layer.Direction == "Верх X" ? "150" : "200";
                layer.Enabled = true;
            }
            ((CheckBox)window.FindName("CheckBox_PerimeterEnabled")).IsChecked = true;
            ((CheckBox)window.FindName("CheckBox_PerimeterOuter")).IsChecked = true;
            ((CheckBox)window.FindName("CheckBox_PerimeterOpenings")).IsChecked = true;
            ((ComboBox)window.FindName("ComboBox_PerimeterType")).SelectedItem = perimeterType;
            ((TextBox)window.FindName("TextBox_PerimeterSpacing")).Text = "200";
            window.Show();
            var timeout = Stopwatch.StartNew();
            while (Volatile.Read(ref calculated) == null && timeout.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(50);
            await Task.Delay(200);
            ReinforcementPlan plan = Volatile.Read(ref calculated);
            Expect(plan != null, "The actual background/perimeter planners did not complete.");
            SaveImage(window, "preview-real-mvp.png");
            File.WriteAllText(Path.Combine(_outputDirectory, "real-mvp-plan.json"), JsonSerializer.Serialize(new
            {
                Source = "Synthetic geometry; actual BackgroundPlanner, PerimeterPlanner and CompositePlanValidator",
                ThicknessMm = 220, TopCoverMm = calculatedParameters.TopCoverMm,
                BottomCoverMm = calculatedParameters.BottomCoverMm, SideCoverMm = calculatedParameters.EdgeCoverMm,
                BackgroundLayers = plan.Bars.Where(bar => bar.Placement.Component == "Background")
                    .GroupBy(bar => bar.SpecKey).Select(group => new { Layer = group.Key, Bars = group.Count(), ElevationMm = group.First().Placement.ElevationMm }),
                Components = plan.Bars.GroupBy(bar => bar.Placement.Component).Select(group => new { Component = group.Key, Bars = group.Count() }),
                Groups = plan.Groups.Count, plan.CanCreate, plan.Diagnostics
            }, new JsonSerializerOptions { WriteIndented = true }));
            Expect(plan.Bars.Where(bar => bar.Placement.Component == "Background").Select(bar => bar.SpecKey).Distinct().Count() == 4,
                "The actual MVP fixture must contain four complete background layers.");
            Expect(plan.Bars.Any(bar => bar.Placement.Component == "PerimeterU" && bar.Centerline.Count(segment => segment.Midpoint.HasValue) == 2) &&
                !plan.Bars.Any(bar => bar.Placement.Component == "PerimeterLongitudinal"),
                "The actual MVP fixture must contain rounded U-bars and no longitudinal framing paths.");
            Expect(plan.CanCreate && ((Button)window.FindName("Button_Confirm")).IsEnabled,
                "The actual MVP fixture must pass the combined geometry validator: " + string.Join("; ", plan.Diagnostics.Where(d => d.IsBlocking).Select(d => d.Message)));
            var expectedHeights = new Dictionary<string, double>
            {
                { "Background|Верх X", -36 }, { "Background|Верх Y", -48 },
                { "Background|Низ X", -174 }, { "Background|Низ Y", -162 }
            };
            Expect(plan.Bars.Where(bar => bar.Placement.Component == "Background")
                .All(bar => Math.Abs(bar.Placement.ElevationMm - expectedHeights[bar.SpecKey]) < 1e-6),
                "The real planner fixture must use the edited 30/40 covers without shifting the background for U-bars.");
            int lowerRows = plan.Bars.Count(bar => bar.Placement.Component == "Background" && bar.SpecKey.StartsWith("Background|Низ", StringComparison.Ordinal));
            Expect(plan.Bars.Count(bar => bar.Placement.Component == "PerimeterU") == lowerRows * 2 &&
                plan.PerimeterUEdges.Count > 0 && plan.PerimeterUEdges.All(edge => edge.FollowsBackgroundRows),
                "Every lower row end must get a U-bar even when the upper-X spacing differs.");
            window.Width = 1024;
            window.Height = 640;
            await Task.Delay(100);
            SaveImage(window, "preview-real-mvp-1024.png");
            Console.WriteLine("Actual MVP: " + plan.Bars.Count + " bars; " + plan.Groups.Count + " groups; four background layers and U-only shell/opening reinforcement.");
            window.Close();
        }

        private static void VerifyCoverEditors()
        {
            var type = new Autodesk.Revit.DB.Structure.RebarBarType { Name = "cover-test" };
            var types = new List<Autodesk.Revit.DB.Structure.RebarBarType> { type };
            // Deserialize an old profile entirely in memory. Never replace the user's settings file.
            using var reader = new StringReader("<SlabReinforcementSettings><EdgeCoverMm>999</EdgeCoverMm></SlabReinforcementSettings>");
            var oldSettings = (SlabReinforcementSettings)new System.Xml.Serialization.XmlSerializer(typeof(SlabReinforcementSettings)).Deserialize(reader);
            PreviewContext Context(double top, double bottom, double side) => new PreviewContext
            {
                TopCoverMm = top, BottomCoverMm = bottom, SideCoverMm = side,
                RebarTypeKeys = new Dictionary<string, string> { { type.Name, "cover-type" } },
                Calculate = (parameters, token) => new ReinforcementPlan()
            };
            SlabReinforcementWPF WindowFor(PreviewContext context)
            {
                var window = new SlabReinforcementWPF(new List<ColorStat>(), types, context, oldSettings);
                window.ShowInTaskbar = false; window.ShowActivated = false; window.Left = -20000; window.Top = -20000;
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                ((BackgroundLayerStat)((DataGrid)window.FindName("BackgroundDataGrid")).Items[0]).Enabled = true;
                return window;
            }
            bool Capture(SlabReinforcementWPF window, out PreviewParameters parameters)
            {
                object[] arguments = { null, null };
                bool valid = (bool)typeof(SlabReinforcementWPF).GetMethod("TryCaptureParameters", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(window, arguments);
                parameters = (PreviewParameters)arguments[0];
                return valid;
            }
            double? ReadCover(PreviewParameters parameters, string name) =>
                name == "TopCoverMm" ? parameters.TopCoverMm : parameters.BottomCoverMm;
            var first = WindowFor(Context(31, 42, 53));
            var topEditor = first.FindName("TextBox_TopCover") as TextBox;
            var bottomEditor = first.FindName("TextBox_BottomCover") as TextBox;
            var sideEditor = (TextBox)first.FindName("TextBox_EdgeCover");
            Expect(topEditor != null && bottomEditor != null, "The selected slab needs separate top and bottom cover editors.");
            Expect(Capture(first, out var initial) && ReadCover(initial, "TopCoverMm") == 31 &&
                ReadCover(initial, "BottomCoverMm") == 42 && initial.EdgeCoverMm == 53,
                "All three initial covers must come from the current host, ignoring the old saved side cover 999.");
            topEditor.Text = "17,5"; bottomEditor.Text = "26"; sideEditor.Text = "38";
            Expect(Capture(first, out var edited) && ReadCover(edited, "TopCoverMm") == 17.5 &&
                ReadCover(edited, "BottomCoverMm") == 26 && edited.EdgeCoverMm == 38,
                "Each cover must be captured independently in millimetres.");
            foreach (var editor in new[] { topEditor, bottomEditor, sideEditor })
            {
                string previous = editor.Text;
                foreach (string invalid in new[] { "-1", "NaN", "Infinity", "bad", "" })
                {
                    editor.Text = invalid;
                    Expect(!Capture(first, out _), "Every cover must reject negative, nonfinite and missing values: " + editor.Name + "=" + invalid);
                }
                editor.Text = "0";
                Expect(Capture(first, out _), "Zero is an explicit valid cover input.");
                editor.Text = previous;
            }
            var underlay = Context(91, 92, 93);
            underlay.Faces.Add(new FaceInput { ColorKey = "RGB(1, 2, 3)", Vertices = new List<Point2> { new Point2(0, 0), new Point2(100, 0), new Point2(0, 100) } });
            first.ApplyUnderlay(new List<ColorStat> { new ColorStat { Color = "RGB(1, 2, 3)" } }, underlay, "cover-test.dxf");
            ((ColorStat)((DataGrid)first.FindName("ColorStatsDataGrid")).Items[0]).IsSelected = true;
            Expect(Capture(first, out var replaced) && ReadCover(replaced, "TopCoverMm") == 17.5 &&
                ReadCover(replaced, "BottomCoverMm") == 26 && replaced.EdgeCoverMm == 38,
                "Selecting or replacing an underlay must preserve the edited host covers.");
            var second = WindowFor(Context(25, 35, 45));
            Expect(Capture(second, out var next) && ReadCover(next, "TopCoverMm") == 25 &&
                ReadCover(next, "BottomCoverMm") == 35 && next.EdgeCoverMm == 45,
                "A new slab window must take its own covers, not the previous slab's edits or saved defaults.");
            Expect(ReadCover(new PreviewParameters(), "TopCoverMm") == null && ReadCover(new PreviewParameters(), "BottomCoverMm") == null,
                "Legacy parameter callers must retain null covers for the integration's host fallback.");
            first.Show();
            first.Width = 1024; first.Height = 640;
            ((Expander)first.FindName("SettingsExpander")).IsExpanded = true;
            topEditor.BringIntoView();
            first.UpdateLayout();
            SaveImage(first, "preview-three-covers-1024.png");
            first.Close(); second.Close();
            File.WriteAllText(Path.Combine(_outputDirectory, "cover-editors.txt"),
                "PASS: current host 31/42/53; edited 17.5/26/38; underlay preserves edits; new host 25/35/45 ignores saved side 999; all covers validate finite >=0; legacy null preserved.");
        }

        private static void VerifyPhysicalPreview()
        {
            DirectoryInfo directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "SlabReinforcement.sln"))) directory = directory.Parent;
            Expect(directory != null, "Cannot find the real 1-layer footprint.");
            using var footprint = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.FullName, "artifacts", "single-slab", "footprint.json")));
            var slab = new PolygonInput
            {
                Shell = footprint.RootElement.GetProperty("shell").EnumerateArray().Select(point =>
                    new Point2(point[0].GetDouble(), point[1].GetDouble())).ToList()
            };
            var context = new PreviewContext { Slabs = new List<PolygonInput> { slab } };
            var configuredLayers = new[] { "Низ X", "Низ Y", "Верх X", "Верх Y" }.Select(layer => new BackgroundLayer
            {
                Key = layer, TypeKey = "12", DiameterMm = 12, ModelDiameterMm = 12,
                SpacingMm = layer == "Верх X" ? 150 : 200,
                Top = layer.StartsWith("Верх", StringComparison.Ordinal), AlongX = layer.EndsWith("X", StringComparison.Ordinal)
            }).ToList();
            var plan = BackgroundPlanner.Build(new BackgroundRequest
            {
                Slabs = context.Slabs, ThicknessMm = 200, TopCoverMm = 35, BottomCoverMm = 35, SideCoverMm = 35.05,
                Layers = configuredLayers, PlacementLayers = configuredLayers
            });
            var perimeterRequest = new PerimeterRequest
            {
                Slabs = context.Slabs, TypeKey = "12", DiameterMm = 12, ModelDiameterMm = 12,
                BendDiameterMm = 48, ThicknessMm = 200, TopCoverMm = 35, BottomCoverMm = 35,
                SideCoverMm = 35.05, Outer = true, UseUbars = true, LegLengthMm = 500,
                BackgroundPlan = plan, BackgroundLayers = configuredLayers
            };
            var perimeter = PerimeterPlanner.Build(perimeterRequest);
            int backgroundBars = plan.Bars.Count;
            int lowerRows = plan.Bars.Count(bar => bar.SpecKey.StartsWith("Низ", StringComparison.Ordinal));
            var backgroundHeights = plan.Bars.Select(bar => bar.Placement.ElevationMm).Distinct().OrderBy(z => z).ToArray();
            Expect(backgroundHeights.SequenceEqual(new[] { -159.0, -147.0, -53.0, -41.0 }),
                "The actual 200 mm slab with 35 mm covers and 12 mm bars must retain Z=-41/-53/-147/-159.");
            Expect(perimeter.CanCreate && perimeter.Bars.Count == lowerRows * 2 && perimeter.PerimeterUEdges.All(edge =>
                edge.FollowsBackgroundRows && edge.TopZ == (edge.AlongX ? -41 : -53) && edge.BottomZ == (edge.AlongX ? -159 : -147)),
                "The actual-footprint U-bars must follow all lower row ends at the matching X/Y heights: " +
                string.Join("; ", perimeter.Diagnostics.Where(d => d.IsBlocking).Select(d => d.Message)));
            plan.Bars.AddRange(perimeter.Bars);
            plan.Groups.AddRange(perimeter.Groups);
            plan.PerimeterUEdges.AddRange(perimeter.PerimeterUEdges);
            Expect(!plan.Diagnostics.Concat(perimeter.Diagnostics).Any(d => d.IsBlocking), "The real-footprint fixture must be geometrically valid.");
            int physicalBars = plan.Bars.Count, groups = plan.Groups.Count;
            Expect(physicalBars > 1500, "The preview regression needs the dense actual slab plan.");
            var visibleBars = SchematicPreviewBars.Select(plan.Bars);
            Expect(visibleBars.Count == physicalBars && plan.Bars.All(visibleBars.Contains),
                "Preview must retain every actual background row and every U-bar, including a denser upper layer: " +
                visibleBars.Count + " visible of " + physicalBars + " physical bars.");
            VerifyVisiblePerimeterMatchesLowerRows(visibleBars);
            string before = JsonSerializer.Serialize(plan);
            var canvas = new SlabPreviewCanvas();
            var window = new Window
            {
                Content = canvas, Width = 1000, Height = 680, ShowInTaskbar = false, ShowActivated = false,
                Left = -20000, Top = -20000, WindowStartupLocation = WindowStartupLocation.Manual
            };
            window.Show();
            canvas.SetContext(context);
            canvas.SetPlan(plan);
            window.UpdateLayout();
            SaveImage(window, "preview-1-layer-actual.png");
            int figures = PreviewFigureCount(canvas);
            Expect(figures == physicalBars,
                "Preview must draw each physical bar at its actual position: rendered " + figures + " paths for " + physicalBars + " physical bars.");
            canvas.Zoom(2);
            canvas.Fit();
            Expect(JsonSerializer.Serialize(plan) == before && plan.Bars.Count == physicalBars && plan.Groups.Count == groups,
                "Preview rendering must never change the physical plan, its counts or grouping.");
            var existingBackgroundPlan = new ReinforcementPlan
            {
                ReferenceBars = plan.Bars.Where(bar => bar.Placement?.Component == "Background").ToList(),
                Bars = plan.Bars.Where(bar => bar.Placement?.Component != "Background").ToList()
            };
            canvas.SetPlan(existingBackgroundPlan);
            Expect(PreviewFigureCount(canvas) == physicalBars,
                "Reused existing background must retain all its rows beside the newly planned U-bars.");
            VerifyVisiblePerimeterMatchesLowerRows(SchematicPreviewBars.Select(
                existingBackgroundPlan.ReferenceBars.Concat(existingBackgroundPlan.Bars)));
            canvas.SetPlan(plan);
            var zone = new PlannedBar { Start = new Point2(-25000, 25000), End = new Point2(-20000, 25000), SpecKey = "zone", ColorKey = "RGB(200, 0, 0)" };
            plan.Bars.Add(zone);
            canvas.SetPlan(plan);
            Expect(PreviewFigureCount(canvas) == figures + 1, "Zone bars must remain detailed alongside the complete physical background.");
            var openingContext = CreateContext(new Autodesk.Revit.DB.Structure.RebarBarType { Name = "preview" });
            openingContext.Faces.Clear();
            var openingPlan = BackgroundPlanner.Build(new BackgroundRequest
            {
                Slabs = openingContext.Slabs, ThicknessMm = 200, TopCoverMm = 35, BottomCoverMm = 35, SideCoverMm = 35,
                Layers = new List<BackgroundLayer> { new BackgroundLayer
                {
                    Key = "upper-y", TypeKey = "12", Top = true, AlongX = false,
                    DiameterMm = 12, ModelDiameterMm = 12, SpacingMm = 100
                } }
            });
            var openingBars = SchematicPreviewBars.Select(openingPlan.Bars);
            Expect(openingBars.Count == openingPlan.Bars.Count && openingBars.All(bar => bar.SpecKey == "upper-y"),
                "A single enabled upper-Y layer must be shown without fabricating the X or lower directions.");
            var splitRow = openingBars.GroupBy(bar => bar.Start.X).FirstOrDefault(row => row.Key > 4500 && row.Key < 6500);
            Expect(splitRow != null && splitRow.Count() == 2 &&
                splitRow.All(bar => Math.Max(bar.Start.Y, bar.End.Y) <= 3000 || Math.Min(bar.Start.Y, bar.End.Y) >= 5000),
                "The preview must retain every separate piece around an opening, never bridging across it.");
            canvas.SetContext(openingContext);
            canvas.SetPlan(openingPlan);
            window.UpdateLayout();
            SaveImage(window, "preview-actual-upper-y-opening.png");
            File.WriteAllText(Path.Combine(_outputDirectory, "physical-preview.json"), JsonSerializer.Serialize(new
            {
                Source = "Actual 1-layer DXF footprint; real BackgroundPlanner and PerimeterPlanner; WPF renderer without Revit",
                PhysicalBars = physicalBars, Groups = groups, RenderedPaths = figures,
                BackgroundBars = backgroundBars, PerimeterBars = perimeter.Bars.Count, BackgroundZMm = backgroundHeights,
                PerimeterFollowsLowerRows = true,
                EveryPhysicalBarRendered = true, ExistingBackgroundRowsPreserved = true,
                FourLayersRetained = plan.Bars.Where(b => b.Placement?.Component == "Background").Select(b => b.SpecKey).Distinct().Count() == 4,
                PlanUnchangedByRendering = true, ZoneRenderedInFull = true,
                SingleDirectionPreserved = true, HolePiecesPreserved = true
            }, new JsonSerializerOptions { WriteIndented = true }));
            window.Close();
        }

        private static void VerifyVisiblePerimeterMatchesLowerRows(IEnumerable<PlannedBar> visibleBars)
        {
            var visible = visibleBars.ToList();
            var lower = visible.Where(bar => bar.Placement?.Component == "Background" &&
                bar.SpecKey.StartsWith("Низ", StringComparison.Ordinal)).ToList();
            var perimeter = visible.Where(bar => bar.Placement?.Component == "PerimeterU").ToList();
            Expect(perimeter.Count == lower.Count * 2, "Every displayed lower row must keep both actual perimeter U-bars.");
            foreach (PlannedBar row in lower)
            {
                bool alongX = Math.Abs(row.End.X - row.Start.X) >= Math.Abs(row.End.Y - row.Start.Y);
                double station = alongX ? row.Start.Y : row.Start.X;
                foreach (Point2 end in new[] { row.Start, row.End })
                    Expect(perimeter.Any(bar =>
                    {
                        CenterlineSegment web = bar.Centerline[2];
                        return Math.Abs((alongX ? web.Start.Y : web.Start.X) - station) < 1e-6 &&
                            Math.Abs((alongX ? web.Start.X : web.Start.Y) - (alongX ? end.X : end.Y)) < 1e-6 &&
                            Math.Abs(bar.Centerline[0].Start.Z - row.Placement.ElevationMm) < 1e-6;
                    }), "A displayed U-bar is missing at actual lower-background endpoint " + row.SpecKey + " (" + end.X + ", " + end.Y + "), Z=" + row.Placement.ElevationMm + ". Nearby U: " +
                        string.Join("; ", perimeter.Where(bar => Math.Abs((alongX ? bar.Centerline[2].Start.Y : bar.Centerline[2].Start.X) - station) < 0.01 &&
                            Math.Abs(bar.Centerline[0].Start.Z - row.Placement.ElevationMm) < 1e-6)
                            .Select(bar => bar.Centerline[2].Start.X + ", " + bar.Centerline[2].Start.Y + ", Z=" + bar.Centerline[0].Start.Z)));
            }
        }

        private static int PreviewFigureCount(SlabPreviewCanvas canvas)
        {
            var batches = (System.Collections.IEnumerable)typeof(SlabPreviewCanvas)
                .GetField("_bars", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(canvas);
            return batches.Cast<object>().Sum(batch => PathGeometry.CreateFromGeometry(
                (StreamGeometry)batch.GetType().GetField("Geometry").GetValue(batch)).Figures.Count);
        }

        private static void VerifyPreviewNavigationLayout(Window window)
        {
            var canvas = (SlabPreviewCanvas)window.FindName("PreviewCanvas");
            var hint = (TextBlock)window.FindName("PreviewNavigationHint");
            var content = (FrameworkElement)window.Content;
            Expect(canvas.ToolTip == null, "A navigation tooltip must not cover the plan while hovering or dragging.");
            Expect(hint != null && hint.IsVisible && IsInsideWindow(window, hint),
                "Preview navigation help must remain visible inside the window when resized.");
            Rect canvasBounds = canvas.TransformToAncestor(content).TransformBounds(new Rect(canvas.RenderSize));
            Rect hintBounds = hint.TransformToAncestor(content).TransformBounds(new Rect(hint.RenderSize));
            Expect(hintBounds.Top >= canvasBounds.Bottom - 0.01,
                "Preview navigation help must have its own layout row outside the drawing.");
        }

        private static void SaveImage(Window window, string filename)
        {
            window.UpdateLayout();
            var content = (FrameworkElement)window.Content;
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth),
                (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            var visual = new DrawingVisual();
            using (DrawingContext drawing = visual.RenderOpen())
            {
                var area = new Rect(0, 0, content.ActualWidth, content.ActualHeight);
                drawing.DrawRectangle(window.Background, null, area);
            }
            bitmap.Render(visual);
            bitmap.Render(content);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (FileStream stream = File.Create(Path.Combine(_outputDirectory, filename))) encoder.Save(stream);
        }

        private static void Expect(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static bool IsInsideWindow(Window window, FrameworkElement element)
        {
            var content = (FrameworkElement)window.Content;
            return IsInsideElement(content, element);
        }

        private static bool IsInsideElement(FrameworkElement parent, FrameworkElement element)
        {
            Rect bounds = element.TransformToAncestor(parent).TransformBounds(new Rect(element.RenderSize));
            var viewport = new Rect(parent.RenderSize);
            // WPF's fractional layout can sum to 106.00000000000001 at an exact 106 px edge.
            // A hundredth of a pixel allows that arithmetic noise, not clipped controls.
            viewport.Inflate(0.01, 0.01);
            return viewport.Contains(bounds);
        }

        private static void RecordFailure(Exception exception)
        {
            File.WriteAllText(Path.Combine(_outputDirectory, "failure.txt"), exception.ToString());
            Console.Error.WriteLine(exception);
        }
    }
}
