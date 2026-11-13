using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using SlabReinforcement.Integration;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Interop;
using System.Windows.Media;

namespace SlabReinforcement
{
    [Autodesk.Revit.Attributes.Transaction(Autodesk.Revit.Attributes.TransactionMode.Manual)]
    public class SlabReinforcementCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try { _ = GetPluginStartInfo(); } catch { }

            var uiDoc = commandData.Application.ActiveUIDocument;
            if (uiDoc == null) { message = "Откройте проект с плитой."; return Result.Failed; }
            try
            {
                var doc = uiDoc.Document;
                var floorReference = uiDoc.Selection.PickObject(ObjectType.Element, new FloorSelectionFilter(), "Выберите плиту для армирования");
                var floor = (Floor)doc.GetElement(floorReference);
                var hostData = RebarHostData.GetRebarHostData(floor);
                if (hostData == null || !hostData.IsValidHost())
                    throw new ArgumentException("Плита не допускает армирование. Проверьте материал и несущие свойства.");
                var edge = uiDoc.Selection.PickObject(ObjectType.Edge, new SlabEdgeSelectionFilter(floor), "Выберите прямое ребро этой плиты — оно задаёт направление X");
                var slab = SlabGeometryReader.Read(doc, floor, edge);
                var types = new FilteredElementCollector(doc).OfClass(typeof(RebarBarType)).Cast<RebarBarType>()
                    .OrderBy(t => t.Name, new AlphanumComparatorFastString()).ToList();
                if (types.Count == 0) throw new ArgumentException("Загрузите типы арматуры и запустите команду снова.");
                var session = new ReinforcementSession(slab, new ImportedMeshSnapshot(), types);
                var window = new SlabReinforcementWPF(new System.Collections.Generic.List<ColorStat>(), types, session.CreateContext());
                IntPtr owner = commandData.Application.MainWindowHandle;
                new WindowInteropHelper(window).Owner = owner;
                try
                {
                    while (true)
                    {
                        window.ResumePreview();
                        bool? accepted = window.ShowDialog();
                        if (window.ConsumeUnderlaySelectionRequest())
                        {
                            // Hide ends the modal loop and re-enables Revit. Pick only here,
                            // in the external command, never from a WPF/worker callback.
                            try
                            {
                                var cadReference = uiDoc.Selection.PickObject(ObjectType.Element, new ImportInstanceSelectionFilter(),
                                    "Выберите расчётную DWG/DXF подложку; Esc — вернуться к настройкам");
                                var import = (ImportInstance)doc.GetElement(cadReference);
                                ImportedMeshSnapshot mesh;
                                try { mesh = ImportedMeshReader.Read(doc, import, slab.Frame); }
                                catch (CadMetadataUnavailableException) { mesh = ReadOriginalDxf(doc, import, slab, owner); }
                                var colors = mesh.Colors.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new ColorStat
                                {
                                    Color = p.Key, ColorBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(p.Value.Red, p.Value.Green, p.Value.Blue)),
                                    RebarTypes = types, Spacing = 200
                                }).ToList();
                                var replacement = new ReinforcementSession(slab, mesh, types);
                                window.ApplyUnderlay(colors, replacement.CreateContext(), import.Name);
                                session = replacement;
                            }
                            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { }
                            catch (System.OperationCanceledException) { }
                            catch (Exception ex) { TaskDialog.Show("Не удалось выбрать подложку", ex.Message); }
                            continue;
                        }
                        if (accepted != true) return Result.Cancelled;
                        var plan = window.AcceptedPlan;
                        int sets = RebarPlanWriter.Create(doc, slab, session, plan, window.AcceptedParameters);
                        int zoneSystems = plan.Groups.Count(group => group.Placement?.Component == "Zones");
                        TaskDialog.Show("Армирование плит", $"Создано стержней: {plan.Bars.Count}.\nЗон: {zoneSystems}.\nНаборов фона и П-стержней: {sets - zoneSystems}.");
                        return Result.Succeeded;
                    }
                }
                finally { window.Close(); }
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
            catch (System.OperationCanceledException) { return Result.Cancelled; }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("Армирование плит", ex.Message);
                return Result.Failed;
            }
        }

        private static ImportedMeshSnapshot ReadOriginalDxf(Document doc, ImportInstance import, SlabGeometrySnapshot slab, IntPtr owner)
        {
            string sourcePath = null;
            try
            {
                var reference = ExternalFileUtils.GetExternalFileReference(doc, import.GetTypeId());
                sourcePath = ModelPathUtils.ConvertModelPathToUserVisiblePath(reference.GetAbsolutePath());
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException) { }
            catch (InvalidOperationException) { }
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath) || !string.Equals(Path.GetExtension(sourcePath), ".dxf", StringComparison.OrdinalIgnoreCase))
            {
                using (var picker = new System.Windows.Forms.OpenFileDialog
                {
                    Title = "Исходный DXF для выбранной подложки", Filter = "Расчётный DXF (*.dxf)|*.dxf", CheckFileExists = true,
                    InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
                })
                {
                    if (picker.ShowDialog(new RevitWindow(owner)) != System.Windows.Forms.DialogResult.OK)
                        throw new System.OperationCanceledException();
                    sourcePath = picker.FileName;
                }
            }
            var units = new TaskDialog("Единицы DXF")
            {
                MainInstruction = "В каких единицах исходный DXF?",
                MainContent = Path.GetFileName(sourcePath) + "\nСлои и цвета будут прочитаны из выбранного DXF.",
                CommonButtons = TaskDialogCommonButtons.Cancel
            };
            units.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Метры");
            units.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Миллиметры");
            units.AddCommandLink(TaskDialogCommandLinkId.CommandLink3, "Сантиметры");
            double factor;
            switch (units.Show())
            {
                case TaskDialogResult.CommandLink1: factor = 1000; break;
                case TaskDialogResult.CommandLink2: factor = 1; break;
                case TaskDialogResult.CommandLink3: factor = 10; break;
                default: throw new System.OperationCanceledException();
            }
            return DxfMeshReader.Read(doc, import, slab.Frame, sourcePath, factor);
        }

        private sealed class RevitWindow : System.Windows.Forms.IWin32Window
        {
            public IntPtr Handle { get; }
            public RevitWindow(IntPtr handle) { Handle = handle; }
        }

        private static async Task GetPluginStartInfo()
        {
            var thisAssembly = Assembly.GetExecutingAssembly();
            const string assemblyName = "SlabReinforcement";
            const string assemblyNameRus = "Зоны усиления плит";

            var location = thisAssembly.Location;
            if (string.IsNullOrEmpty(location))
                return;

            var dirPath = Path.GetDirectoryName(location);
            if (dirPath == null || dirPath.Length == 0)
                return;

            var leafFolder = Path.GetFileName(dirPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var isVersionedBuild = leafFolder.Length == 5 && leafFolder.StartsWith("R20", StringComparison.OrdinalIgnoreCase);

            var dllPath = string.Empty;
            var current = Directory.GetParent(dirPath);
            for (var depth = 0; depth < 4 && current != null; depth++, current = current.Parent)
            {
                if (isVersionedBuild)
                {
                    var versionedCandidate = Path.Combine(current.FullName, "PluginInfoCollector", "PluginInfoCollector", "bin", leafFolder, "PluginInfoCollector.dll");
                    if (File.Exists(versionedCandidate))
                    {
                        dllPath = versionedCandidate;
                        break;
                    }
                }

                var candidate = Path.Combine(current.FullName, "PluginInfoCollector", "PluginInfoCollector.dll");
                if (File.Exists(candidate))
                {
                    dllPath = candidate;
                    break;
                }
            }

            if (string.IsNullOrEmpty(dllPath))
                return;

            var assembly = Assembly.LoadFrom(dllPath);
            var type = assembly.GetType("PluginInfoCollector.InfoCollector");
            if (type == null)
                return;

            var instance = Activator.CreateInstance(type);
            if (instance == null)
                return;

            var method = type.GetMethod("CollectPluginUsageAsync");
            if (method == null)
                return;

            if (method.Invoke(instance, new object[] { assemblyName, assemblyNameRus }) is Task task)
                await task;
        }
    }
}
