using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Autodesk.Revit.DB.Structure;

namespace SlabReinforcement.UnderlayUiSmoke
{
    public sealed partial class UnderlayApplication
    {
        private void RunLayerAlignmentAudit(List<RebarBarType> types)
        {
            Log("ALIGN entry; sync=" + System.Threading.SynchronizationContext.Current?.GetType().FullName);
            string productPath = File.ReadAllText(Path.Combine(_run, "product-path.txt")).Trim();
            string expectedHash = File.ReadAllText(Path.Combine(_run, "expected-product-sha256.txt")).Trim();
            var assembly = Assembly.LoadFrom(productPath);
            Log("ALIGN loaded " + assembly.Location);
            var windowType = assembly.GetType("SlabReinforcement.SlabReinforcementWPF", true);
            var expectedMvid = Assembly.ReflectionOnlyLoad(File.ReadAllBytes(productPath)).ManifestModule.ModuleVersionId;
            Log("ALIGN MVID " + windowType.Module.ModuleVersionId);
            var loaded = new { requestedPath = productPath, expectedHash, loadedLocation = assembly.Location,
                loadedDiskHash = Hash(assembly.Location), moduleVersionId = windowType.Module.ModuleVersionId.ToString(),
                expectedModuleVersionId = expectedMvid.ToString(), loadedMvidMatchesPinned = windowType.Module.ModuleVersionId == expectedMvid,
                assemblyIdentity = assembly.FullName, framework = Environment.Version.ToString(),
                presentationFramework = typeof(Window).Assembly.FullName, presentationFrameworkLocation = typeof(Window).Assembly.Location };
            _report["product"] = loaded;
            Require(Hash(productPath) == expectedHash && Hash(assembly.Location) == expectedHash, "The loaded product file differs from the pinned product.");
            Require(windowType.Module.ModuleVersionId == expectedMvid, "Already loaded product code differs from the pinned module.");
            var settingsType = assembly.GetType("SlabReinforcement.SlabReinforcementSettings", true);
            string settingsPath = (string)settingsType.GetMethod("GetSettingsFilePath").Invoke(null, null);
            string beforeSettings = Hash(settingsPath);
            var contextType = assembly.GetType("SlabReinforcement.PreviewContext", true);
            var context = Activator.CreateInstance(contextType);
            Set(context, "HostName", "Диагностика выравнивания · копия модели");
            Set(context, "RebarTypeKeys", types.ToDictionary(type => type.Name, type => type.UniqueId));
            Set(context, "Slabs", new List<SlabReinforcement.Core.PolygonInput> { new SlabReinforcement.Core.PolygonInput { Shell = new List<SlabReinforcement.Core.Point2>
                { new SlabReinforcement.Core.Point2(0, 0), new SlabReinforcement.Core.Point2(8000, 0), new SlabReinforcement.Core.Point2(8000, 6000), new SlabReinforcement.Core.Point2(0, 6000) } } });
            var calculation = contextType.GetProperty("Calculate");
            var parameter = System.Linq.Expressions.Expression.Parameter(assembly.GetType("SlabReinforcement.PreviewParameters", true), "parameters");
            var token = System.Linq.Expressions.Expression.Parameter(typeof(System.Threading.CancellationToken), "token");
            calculation.SetValue(context, System.Linq.Expressions.Expression.Lambda(calculation.PropertyType,
                System.Linq.Expressions.Expression.Call(typeof(UnderlayApplication).GetMethod(nameof(AlignmentCalculate), BindingFlags.Static | BindingFlags.NonPublic),
                    System.Linq.Expressions.Expression.Convert(parameter, typeof(object)), token), parameter, token).Compile());
            var window = (Window)Activator.CreateInstance(windowType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { Colors(assembly), types, context, Activator.CreateInstance(settingsType) }, null);
            Log("ALIGN window constructed");
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -32000; window.Top = -32000; window.Width = 1280; window.Height = 850;
            window.ShowActivated = false; window.ShowInTaskbar = false;
            var grid = Control<DataGrid>(window, "BackgroundDataGrid");
            var rows = grid.Items.Cast<object>().ToList();
            foreach (var row in rows) { Set(row, "SelectedRebarType", types[0]); Set(row, "SpacingText", "200"); }
            var evidence = new List<object>();
            var mismatches = new List<string>();
            Exception failure = null;
            bool completed = false;
            window.Loaded += async (sender, args) =>
            {
                Log("ALIGN loaded window; sync=" + System.Threading.SynchronizationContext.Current?.GetType().FullName);
                try
                {
                    foreach (int width in new[] { 1280, 1024 })
                    {
                        Log("ALIGN width " + width);
                        window.Width = width; window.Height = width == 1024 ? 640 : 850;
                        Control<TabControl>(window, "ComponentsTabs").SelectedItem = window.FindName("BackgroundTab");
                        for (int pattern = 0; pattern < 2; pattern++)
                        {
                            for (int i = 0; i < rows.Count; i++) Set(rows[i], "Enabled", i % 2 == pattern);
                            foreach (int selected in new[] { -1, 0, 1, 2, 3 })
                            {
                                grid.SelectedIndex = selected;
                                await Settle();
                                for (int i = 0; i < rows.Count; i++)
                                {
                                    var row = grid.ItemContainerGenerator.ContainerFromIndex(i) as DataGridRow;
                                    Require(row != null, "Background row was not realized.");
                                    var text = grid.Columns[1].GetCellContent(row) as TextBlock;
                                    Require(text != null, "The layer column did not generate its TextBlock.");
                                    var cell = Ancestor<DataGridCell>(text);
                                    var presenter = Ancestor<ContentPresenter>(text);
                                    Require(cell != null, "Cannot locate the text's full DataGridCell.");
                                    Rect textInCell = text.TransformToAncestor(cell).TransformBounds(new Rect(text.RenderSize));
                                    Rect textInRow = text.TransformToAncestor(row).TransformBounds(new Rect(text.RenderSize));
                                    double cellError = textInCell.Top + textInCell.Height / 2 - cell.ActualHeight / 2;
                                    double rowError = textInRow.Top + textInRow.Height / 2 - row.ActualHeight / 2;
                                    Rect character = text.ContentStart.GetInsertionPosition(LogicalDirection.Forward).GetCharacterRect(LogicalDirection.Forward);
                                    Rect characterInRow = text.TransformToAncestor(row).TransformBounds(character);
                                    bool centered = Math.Abs(cellError) <= 1 && Math.Abs(rowError) <= 1;
                                    if (!centered) mismatches.Add(width + "/" + pattern + "/" + selected + "/" + text.Text + ": cell=" + cellError + "; row=" + rowError);
                                    var valueSource = DependencyPropertyHelper.GetValueSource(text, FrameworkElement.VerticalAlignmentProperty);
                                    evidence.Add(new { width, pattern, selectedIndex = selected, index = i, enabled = Get(rows[i], "Enabled"),
                                        rowSelected = row.IsSelected, cellSelected = cell.IsSelected, text = text.Text, centered,
                                        rowHeight = row.ActualHeight, cellHeight = cell.ActualHeight, textHeight = text.ActualHeight,
                                        textInCell = Bounds(textInCell), textInRow = Bounds(textInRow), characterInRow = Bounds(characterInRow),
                                        cellCenterErrorDip = cellError, rowCenterErrorDip = rowError,
                                        characterCenterErrorDip = characterInRow.Top + characterInRow.Height / 2 - row.ActualHeight / 2,
                                        textVerticalAlignment = text.VerticalAlignment.ToString(), valueSource = valueSource.BaseValueSource.ToString(),
                                        cellVerticalAlignment = cell.VerticalAlignment.ToString(), cellVerticalContentAlignment = cell.VerticalContentAlignment.ToString(),
                                        cellPadding = cell.Padding.ToString(), textMargin = text.Margin.ToString(),
                                        presenterHeight = presenter?.ActualHeight, presenterVerticalAlignment = presenter?.VerticalAlignment.ToString(),
                                        cellInRow = Bounds(cell.TransformToAncestor(row).TransformBounds(new Rect(cell.RenderSize))) });
                                }
                                if (selected == -1 || selected == 2) Render(window, "alignment-" + width + "-pattern" + pattern + "-selected" + selected + ".png");
                            }
                        }
                    }
                    var tabs = Control<TabControl>(window, "ComponentsTabs");
                    var canvas = window.FindName("PreviewCanvas");
                    var state = new { activeTab = (tabs.SelectedItem as FrameworkElement)?.Name, canvasScope = canvas.GetType().GetField("_scope", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(canvas)?.ToString(),
                        backgroundLegend = (window.FindName("PreviewLegendBackground") as FrameworkElement)?.Visibility.ToString(),
                        zonesLegend = (window.FindName("PreviewLegendZones") as FrameworkElement)?.Visibility.ToString(),
                        revision = GetField(window, "_revision"), readyRevision = GetField(window, "_readyRevision"), window.IsVisible };
                    var dpi = VisualTreeHelper.GetDpi(window);
                    var output = new { loaded, scope = "Actual production WPF / .NET4.8; mocked calculation; copy-only document; no transactions or settings saves",
                        dpiX = dpi.DpiScaleX, dpiY = dpi.DpiScaleY, state, allCentered = mismatches.Count == 0, mismatches, rows = evidence };
                    File.WriteAllText(Path.Combine(_run, "layer-alignment.json"), new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(output));
                    Check("layer-alignment-measured", new { measurements = evidence.Count, centered = mismatches.Count == 0, mismatchCount = mismatches.Count, loaded });
                    completed = true;
                }
                catch (Exception error) { failure = error; }
                finally { window.Close(); }
            };
            Log("ALIGN showing window");
            try { window.ShowDialog(); }
            finally { if (window.IsVisible) window.Close(); }
            if (failure != null) throw new InvalidOperationException("Layer alignment audit failed.", failure);
            Require(completed, "Layer alignment audit did not complete.");
            Require(Hash(settingsPath) == beforeSettings, "Layer alignment audit changed saved settings.");
            Check("alignment-settings-unchanged", new { saved = false });
            _report["alignment"] = new { allCentered = mismatches.Count == 0, mismatchCount = mismatches.Count, measurements = evidence.Count };

            async Task Settle()
            {
                var idle = window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;
                await Task.WhenAny(idle, Task.Delay(1500));
                Require(idle.IsCompleted, "WPF application idle did not complete within 1.5 seconds.");
                window.UpdateLayout();
            }
        }

        private static SlabReinforcement.Core.ReinforcementPlan AlignmentCalculate(object parameters, System.Threading.CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return new SlabReinforcement.Core.ReinforcementPlan { Bars = new List<SlabReinforcement.Core.PlannedBar> {
                new SlabReinforcement.Core.PlannedBar { Start = new SlabReinforcement.Core.Point2(1000, 1000), End = new SlabReinforcement.Core.Point2(7000, 1000),
                    Placement = new SlabReinforcement.Core.BarPlacement { Component = "Background", ModelDiameterMm = 12 } } } };
        }

        private static T Ancestor<T>(DependencyObject child) where T : DependencyObject
        {
            for (DependencyObject parent = VisualTreeHelper.GetParent(child); parent != null; parent = VisualTreeHelper.GetParent(parent))
                if (parent is T found) return found;
            return null;
        }
        private static object Bounds(Rect bounds) => new { x = bounds.X, y = bounds.Y, width = bounds.Width, height = bounds.Height };
    }
}
