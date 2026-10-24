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
                    await VerifyWindow(window, color, type);
                    await VerifySourceGroupSelection();
                    await VerifyComponentsWithoutCad();
                    await VerifyRealPlannerFixture();
                    await VerifyRealMvpFixture();
                    string passed = "PASS: real WPF XAML/resources loaded; latest calculation wins; invalid/missing inputs disable creation; " +
                        "corrected inputs recalculate; Enter/Space do not accept; 1280/1080/1024 window widths render; " +
                        "10,000 bars use one canvas; closing cancels pending UI updates; source layer/elevation switches faces and colors without stale plans; " +
                        "zone axes and numeric areas are required; background/perimeter work without CAD independently of the visible tab; " +
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
            ((CheckBox)window.FindName("CheckBox_CalculationAxesConfirmed")).IsChecked = true;
            foreach (ColorStat color in colors)
            {
                color.SelectedRebarType = placeholder;
                color.IsSelected = true;
                color.SpacingText = "200";
            }
            ((TextBox)window.FindName("TextBox_EdgeCover")).Text = "25";
            ((TextBox)window.FindName("TextBox_LayerOffset")).Text = "0";
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
                        Start = new Point2(spacing, y), End = new Point2(11000, y), ColorKey = "RGB(220, 120, 80)"
                    });
                result.Groups.Add(new BarGroup { Count = result.Bars.Count });
                return result;
            };
            return context;
        }

        private static async Task VerifyWindow(SlabReinforcementWPF window, ColorStat color,
            Autodesk.Revit.DB.Structure.RebarBarType type)
        {
            var settings = (Expander)window.FindName("SettingsExpander");
            var colorGrid = (DataGrid)window.FindName("ColorStatsDataGrid");
            Expect(settings != null && !settings.IsExpanded, "Settings must start collapsed to leave room for colors.");
            Expect(((CheckBox)window.FindName("CheckBox_CalculationAxesConfirmed")).IsChecked != true,
                "Calculation axis confirmation must never be restored for a new invocation.");
            ResetComponents(window, false);
            // Stable inputs make this smoke independent of the user's saved form values.
            ((TextBox)window.FindName("TextBox_EdgeCover")).Text = "25";
            ((TextBox)window.FindName("TextBox_LayerOffset")).Text = "0";
            ((TextBox)window.FindName("TextBox_ZoneMergeTolerance")).Text = "600";
            ((TextBox)window.FindName("TextBox_RoundIncrement")).Text = "10";
            color.IsSelected = true;
            color.SelectedRebarType = type;
            color.SpacingText = "200";
            await Task.Delay(350);
            Expect(!((Button)window.FindName("Button_Confirm")).IsEnabled,
                "Even manual color assignments require calculation axis confirmation.");
            ((CheckBox)window.FindName("CheckBox_CalculationAxesConfirmed")).IsChecked = true;
            await Task.Delay(370);
            color.SpacingText = "250";
            await Task.Delay(1050);
            var confirm = (Button)window.FindName("Button_Confirm");
            Expect(confirm.IsEnabled, "Valid latest plan must enable creation.");
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
            Expect(!confirm.IsEnabled && ((CheckBox)window.FindName("CheckBox_CalculationAxesConfirmed")).IsChecked == false,
                "Changing the zone direction must reset axis confirmation and invalidate the old plan.");
            await Task.Delay(400);
            Expect(!confirm.IsEnabled, "A direction change must not recalculate zones before new axis confirmation.");
            direction.SelectedItem = originalDirection;
            ((CheckBox)window.FindName("CheckBox_CalculationAxesConfirmed")).IsChecked = true;
            await Task.Delay(500);
            Expect(confirm.IsEnabled, "Confirming the current direction must restore calculation.");

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

            window.Width = 1080;
            window.Height = 700;
            await Task.Delay(100);
            SaveImage(window, "preview-1080.png");
            settings.IsExpanded = true;
            await Task.Delay(100);
            ((RadioButton)window.FindName("RadioButton_RoundCutLengths")).BringIntoView();
            await Task.Delay(100);
            SaveImage(window, "preview-1080-settings.png");

            window.Width = 1024;
            window.Height = 640;
            settings.IsExpanded = false;
            await Task.Delay(100);
            Expect(colorGrid.IsVisible && colorGrid.ActualHeight >= 250 && IsInsideWindow(window, colorGrid),
                "The color table must remain visible and at least 250 px tall at 1024x640 with settings collapsed.");
            Expect(IsInsideWindow(window, confirm), "The create button must be fully accessible at 1024x640.");
            SaveImage(window, "preview-1024.png");
            settings.IsExpanded = true;
            await Task.Delay(100);
            var roundingChoice = (RadioButton)window.FindName("RadioButton_RoundCutLengths");
            roundingChoice.BringIntoView();
            await Task.Delay(100);
            Expect(IsInsideWindow(window, confirm), "The create button must be fully accessible at 1024x640.");
            Expect(colorGrid.IsVisible && colorGrid.ActualHeight > 0 && IsInsideWindow(window, colorGrid),
                "Opening settings must leave the color table visible at 1024x640.");
            Expect(roundingChoice.IsVisible && IsInsideWindow(window, roundingChoice) &&
                IsInsideElement((ScrollViewer)window.FindName("SettingsScrollViewer"), roundingChoice),
                "Rounding must remain accessible inside the expanded settings viewport at 1024x640.");
            SaveImage(window, "preview-1024-settings.png");
            settings.IsExpanded = false;

            var canvas = (SlabPreviewCanvas)window.FindName("PreviewCanvas");
            var manyBars = new ReinforcementPlan();
            for (int index = 0; index < 10000; index++)
                manyBars.Bars.Add(new PlannedBar
                {
                    Start = new Point2(100, index * 0.25), End = new Point2(11000, index * 0.25), ColorKey = "RGB(220, 120, 80)"
                });
            var stopwatch = Stopwatch.StartNew();
            canvas.SetPlan(manyBars);
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
                    End = new Point2(4000, 1000), ColorKey = blue
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
            ((TextBox)window.FindName("TextBox_LayerOffset")).Text = "0";
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
            Expect(((CheckBox)window.FindName("CheckBox_CalculationAxesConfirmed")).IsChecked == false,
                "Choosing a source group must require confirmation of that source's axes.");
            ((CheckBox)window.FindName("CheckBox_CalculationAxesConfirmed")).IsChecked = true;
            await Task.Delay(370);
            groups.SelectedValue = singleColor.Key;
            Expect(!confirm.IsEnabled, "Switching a CAD group must immediately invalidate the previous plan.");
            Expect(context.Faces.SequenceEqual(singleColor.Faces), "Changing elevation/layer must replace visible source faces.");
            Expect(grid.Items.Count == 1 && ((ColorStat)grid.Items[0]).Color == blue,
                "The palette must contain only the selected group's colors.");
            Expect(((CheckBox)window.FindName("CheckBox_CalculationAxesConfirmed")).IsChecked == false,
                "Changing a CAD group must not reuse confirmation from the previous layer/elevation.");
            ((CheckBox)window.FindName("CheckBox_CalculationAxesConfirmed")).IsChecked = true;
            await Task.Delay(1050);
            PreviewParameters[] captured = requests.ToArray();
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
            Expect(grid.IsVisible && grid.ActualHeight >= 250 && IsInsideWindow(window, grid),
                "Source groups must leave at least 250 px for the color table at 1024x640 with settings collapsed.");
            Expect(groups.IsVisible && IsInsideWindow(window, groups), "The source group selector must remain accessible at 1024x640.");
            SaveImage(window, "preview-source-groups-1024-collapsed.png");
            settings.IsExpanded = true;
            await Task.Delay(100);
            var rounding = (RadioButton)window.FindName("RadioButton_RoundCutLengths");
            rounding.BringIntoView();
            await Task.Delay(100);
            Expect(IsInsideWindow(window, groups), "The source group selector must remain accessible at 1024x640.");
            Expect(grid.IsVisible && grid.ActualHeight > 0 && IsInsideWindow(window, grid),
                "The selected group's colors must remain visible with settings expanded at 1024x640.");
            Expect(rounding.IsVisible && IsInsideWindow(window, rounding) && IsInsideElement((ScrollViewer)window.FindName("SettingsScrollViewer"), rounding),
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
            var type = new Autodesk.Revit.DB.Structure.RebarBarType { Name = "ø12 A500C" };
            PreviewContext context = CreateContext(type);
            context.Faces.Clear();
            var requests = new System.Collections.Concurrent.ConcurrentQueue<PreviewParameters>();
            context.Calculate = (parameters, token) =>
            {
                requests.Enqueue(parameters);
                var plan = new ReinforcementPlan();
                plan.Bars.Add(new PlannedBar { Start = new Point2(500, 500), End = new Point2(4000, 500) });
                return plan;
            };
            var window = new SlabReinforcementWPF(new List<ColorStat>(),
                new List<Autodesk.Revit.DB.Structure.RebarBarType> { type }, context)
            {
                ShowInTaskbar = false, ShowActivated = false, Left = -20000, Top = -20000,
                WindowStartupLocation = WindowStartupLocation.Manual
            };
            ResetComponents(window, false);
            ((TextBox)window.FindName("TextBox_EdgeCover")).Text = "25";
            ((TextBox)window.FindName("TextBox_LayerOffset")).Text = "0";
            ((TextBox)window.FindName("TextBox_ZoneMergeTolerance")).Text = "0";
            ((TextBox)window.FindName("TextBox_RoundIncrement")).Text = "10";
            window.Show();
            var zones = (CheckBox)window.FindName("CheckBox_IncludeZones");
            var confirm = (Button)window.FindName("Button_Confirm");
            var tabs = (TabControl)window.FindName("ComponentsTabs");
            var layers = ((DataGrid)window.FindName("BackgroundDataGrid")).Items.Cast<BackgroundLayerStat>().ToList();
            Expect(zones.IsChecked == false && !zones.IsEnabled && layers.Count == 4,
                "A no-CAD invocation must offer four background layers with zones disabled.");
            layers[0].SelectedRebarType = type;
            layers[0].SpacingText = "150";
            layers[0].Enabled = true;
            await Task.Delay(500);
            PreviewParameters captured = requests.Last();
            Expect(confirm.IsEnabled && !captured.IncludeZones && !captured.CalculationAxesConfirmed && captured.Selections.Count == 0 &&
                captured.BackgroundLayers.Single(layer => layer.Enabled).SpecKey == "test" &&
                captured.BackgroundLayers.Single(layer => layer.Enabled).SpacingMm == 150,
                "Background alone must calculate without CAD, selected colors or an axis acknowledgment.");
            SaveImage(window, "preview-background.png");
            int count = requests.Count;
            tabs.SelectedItem = window.FindName("ZonesTab");
            await Task.Delay(400);
            Expect(confirm.IsEnabled && requests.Count == count, "Switching tabs must not enable, disable or recalculate components.");
            tabs.SelectedItem = window.FindName("BackgroundTab");
            ((CheckBox)window.FindName("CheckBox_PerimeterEnabled")).IsChecked = true;
            ((CheckBox)window.FindName("CheckBox_PerimeterOuter")).IsChecked = false;
            ((CheckBox)window.FindName("CheckBox_PerimeterOpenings")).IsChecked = true;
            Expect(window.FindName("CheckBox_PerimeterLongitudinal") == null && window.FindName("CheckBox_PerimeterUbars") == null,
                "The U-only MVP must not expose a perimeter shape selector.");
            ((ComboBox)window.FindName("ComboBox_PerimeterType")).SelectedItem = type;
            ((TextBox)window.FindName("TextBox_PerimeterSpacing")).Text = "175";
            ((TextBox)window.FindName("TextBox_PerimeterLeg")).Text = "600";
            await Task.Delay(500);
            captured = requests.Last();
            Expect(confirm.IsEnabled && captured.BackgroundLayers.Any(layer => layer.Enabled) && captured.Perimeter.Enabled &&
                !captured.Perimeter.Outer && captured.Perimeter.Openings && captured.Perimeter.UseUbars && !captured.Perimeter.UseLongitudinalBars &&
                captured.Perimeter.SpecKey == "test" && captured.Perimeter.SpacingMm == 175 && captured.Perimeter.LegLengthMm == 600,
                "Every enabled component and explicit perimeter option must reach the immutable preview request.");
            layers[0].Enabled = false;
            ((TextBox)window.FindName("TextBox_PerimeterSpacing")).Text = "bad";
            Expect(!confirm.IsEnabled, "Invalid U-bar spacing must immediately block creation.");
            ((TextBox)window.FindName("TextBox_PerimeterSpacing")).Text = "175";
            await Task.Delay(500);
            Expect(confirm.IsEnabled && !requests.Last().BackgroundLayers.Any(layer => layer.Enabled) &&
                !requests.Last().Perimeter.UseLongitudinalBars && requests.Last().Perimeter.UseUbars,
                "Corrected U-bar inputs must permit U-only framing without background.");
            window.Width = 1024;
            window.Height = 640;
            await Task.Delay(100);
            SaveImage(window, "preview-background-1024.png");
            var legEditor = (TextBox)window.FindName("TextBox_PerimeterLeg");
            legEditor.BringIntoView();
            await Task.Delay(100);
            Expect(legEditor.IsVisible && IsInsideWindow(window, legEditor) &&
                IsInsideElement((ScrollViewer)window.FindName("BackgroundScrollViewer"), legEditor),
                "Perimeter inputs must remain accessible by scrolling at 1024x640.");
            SaveImage(window, "preview-background-1024-scrolled.png");
            ((CheckBox)window.FindName("CheckBox_PerimeterEnabled")).IsChecked = false;
            Expect(!confirm.IsEnabled, "An operation with every component disabled must block creation.");
            window.Close();
        }

        private static void ResetComponents(SlabReinforcementWPF window, bool confirmAxes = true)
        {
            foreach (BackgroundLayerStat layer in ((DataGrid)window.FindName("BackgroundDataGrid")).Items) layer.Enabled = false;
            ((CheckBox)window.FindName("CheckBox_PerimeterEnabled")).IsChecked = false;
            ((CheckBox)window.FindName("CheckBox_CalculationAxesConfirmed")).IsChecked = confirmAxes;
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
            context.Calculate = (parameters, token) => ReinforcementPlanner.Build(new PlanRequest
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
            ((TextBox)window.FindName("TextBox_LayerOffset")).Text = "0";
            ((TextBox)window.FindName("TextBox_ZoneMergeTolerance")).Text = "600";
            ((TextBox)window.FindName("TextBox_RoundIncrement")).Text = "10";
            ((RadioButton)window.FindName("RadioButton_RoundIncrement")).IsChecked = true;
            foreach (ColorStat color in colors)
            {
                color.SelectedRebarType = type;
                color.IsSelected = true;
                color.SpacingText = "200";
            }
            ((CheckBox)window.FindName("CheckBox_CalculationAxesConfirmed")).IsChecked = true;
            window.Show();
            var confirm = (Button)window.FindName("Button_Confirm");
            var timeout = Stopwatch.StartNew();
            while (!confirm.IsEnabled && timeout.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(50);
            var plan = (ReinforcementPlan)typeof(SlabReinforcementWPF)
                .GetField("_readyPlan", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window);
            Expect(plan != null && plan.CanCreate && confirm.IsEnabled, "The real planner fixture must produce a creatable plan.");
            Expect(plan.Bars.Any(bar => bar.ColorKey == leftColor) && plan.Bars.Any(bar => bar.ColorKey == rightColor),
                "Both demand colors must be represented by planned bars.");
            SaveImage(window, "preview-real-planner.png");
            DirectoryInfo directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "SlabReinforcement.sln"))) directory = directory.Parent;
            Expect(directory != null, "Cannot locate the repository for the final preview artifact.");
            string artifactDirectory = Path.Combine(directory.FullName, "artifacts", "preview");
            Directory.CreateDirectory(artifactDirectory);
            string artifact = Path.Combine(artifactDirectory, "citrus-slab-preview.png");
            SaveImage(window, artifact);
            Console.WriteLine("Real planner: " + plan.Bars.Count + " bars; " + plan.Groups.Count + " groups. Preview: " + artifact);
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
            context.Calculate = (parameters, token) =>
            {
                var result = new ReinforcementPlan();
                var perimeterRequest = new PerimeterRequest
                {
                    Slabs = context.Slabs, TypeKey = parameters.Perimeter.SpecKey,
                    DiameterMm = 8, ModelDiameterMm = 8, BendDiameterMm = 32,
                    ThicknessMm = 220, TopCoverMm = 25, BottomCoverMm = 25, SideCoverMm = parameters.EdgeCoverMm,
                    Outer = parameters.Perimeter.Outer, Openings = parameters.Perimeter.Openings,
                    UseUbars = parameters.Perimeter.UseUbars, UseLongitudinalBars = parameters.Perimeter.UseLongitudinalBars,
                    SpacingMm = parameters.Perimeter.SpacingMm, LegLengthMm = parameters.Perimeter.LegLengthMm
                };
                if (parameters.Perimeter.Enabled) Append(PerimeterPlanner.Build(perimeterRequest, token));
                var backgroundRequest = new BackgroundRequest
                {
                    Slabs = context.Slabs, ThicknessMm = 220, TopCoverMm = 25, BottomCoverMm = 25,
                    SideCoverMm = parameters.EdgeCoverMm + (parameters.Perimeter.Enabled && parameters.Perimeter.UseUbars ? 8 : 0),
                    OuterLayerOffsetMm = parameters.Perimeter.Enabled ? PerimeterPlanner.ReservedDepth(perimeterRequest) : 0,
                    Layers = parameters.BackgroundLayers.Where(layer => layer.Enabled).Select(layer => new BackgroundLayer
                    {
                        Key = "Background|" + layer.Direction, TypeKey = layer.SpecKey,
                        Top = layer.Direction.StartsWith("Верх", StringComparison.Ordinal),
                        AlongX = layer.Direction.EndsWith("X", StringComparison.Ordinal),
                        DiameterMm = 12, ModelDiameterMm = 12, SpacingMm = layer.SpacingMm
                    }).ToList()
                };
                if (backgroundRequest.Layers.Count > 0) Append(BackgroundPlanner.Build(backgroundRequest, token));
                if (!result.Diagnostics.Any(diagnostic => diagnostic.IsBlocking))
                    result.Diagnostics.AddRange(CompositePlanValidator.Validate(result.Bars, token));
                Interlocked.Exchange(ref calculated, result);
                return result;

                void Append(ReinforcementPlan component)
                {
                    result.Bars.AddRange(component.Bars);
                    result.Groups.AddRange(component.Groups);
                    result.Diagnostics.AddRange(component.Diagnostics);
                }
            };
            var window = new SlabReinforcementWPF(new List<ColorStat>(),
                new List<Autodesk.Revit.DB.Structure.RebarBarType> { backgroundType, perimeterType }, context)
            {
                ShowInTaskbar = false, ShowActivated = false, Left = -20000, Top = -20000,
                WindowStartupLocation = WindowStartupLocation.Manual
            };
            ResetComponents(window, false);
            ((TextBox)window.FindName("TextBox_EdgeCover")).Text = "25";
            ((TextBox)window.FindName("TextBox_LayerOffset")).Text = "0";
            ((TextBox)window.FindName("TextBox_ZoneMergeTolerance")).Text = "0";
            ((TextBox)window.FindName("TextBox_RoundIncrement")).Text = "10";
            foreach (BackgroundLayerStat layer in ((DataGrid)window.FindName("BackgroundDataGrid")).Items)
            {
                layer.SelectedRebarType = backgroundType;
                layer.SpacingText = "200";
                layer.Enabled = true;
            }
            ((CheckBox)window.FindName("CheckBox_PerimeterEnabled")).IsChecked = true;
            ((CheckBox)window.FindName("CheckBox_PerimeterOuter")).IsChecked = true;
            ((CheckBox)window.FindName("CheckBox_PerimeterOpenings")).IsChecked = true;
            ((ComboBox)window.FindName("ComboBox_PerimeterType")).SelectedItem = perimeterType;
            ((TextBox)window.FindName("TextBox_PerimeterSpacing")).Text = "200";
            ((TextBox)window.FindName("TextBox_PerimeterLeg")).Text = "500";
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
                ThicknessMm = 220, CoverMm = 25,
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
            window.Width = 1024;
            window.Height = 640;
            await Task.Delay(100);
            SaveImage(window, "preview-real-mvp-1024.png");
            Console.WriteLine("Actual MVP: " + plan.Bars.Count + " bars; " + plan.Groups.Count + " groups; four background layers and U-only shell/opening reinforcement.");
            window.Close();
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
            return new Rect(parent.RenderSize).Contains(bounds);
        }

        private static void RecordFailure(Exception exception)
        {
            File.WriteAllText(Path.Combine(_outputDirectory, "failure.txt"), exception.ToString());
            Console.Error.WriteLine(exception);
        }
    }
}
