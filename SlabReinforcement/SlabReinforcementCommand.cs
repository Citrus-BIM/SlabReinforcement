using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using SlabReinforcement.Integration;
using System;
using System.IO;
using System.Linq;
using System.Windows.Interop;
using System.Windows.Media;

namespace SlabReinforcement
{
    [Autodesk.Revit.Attributes.Transaction(Autodesk.Revit.Attributes.TransactionMode.Manual)]
    public class SlabReinforcementCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var uiDoc = commandData.Application.ActiveUIDocument;
            if (uiDoc == null) { message = "Откройте проект с плитой и расчётной подложкой."; return Result.Failed; }
            try
            {
                var doc = uiDoc.Document;
                var floorReference = uiDoc.Selection.PickObject(ObjectType.Element, new FloorSelectionFilter(), "Выберите плиту для армирования");
                var floor = (Floor)doc.GetElement(floorReference);
                var hostData = RebarHostData.GetRebarHostData(floor);
                if (hostData == null || !hostData.IsValidHost())
                    throw new ArgumentException("Выбранная плита не допускает размещение арматуры. Проверьте материал и несущие свойства плиты.");
                var edge = uiDoc.Selection.PickObject(ObjectType.Edge, new SlabEdgeSelectionFilter(floor), "Выберите прямое ребро этой плиты — оно задаёт направление X");
                var slab = SlabGeometryReader.Read(doc, floor, edge);
                var sourceChoice = new TaskDialog("Армирование плит") { MainInstruction = "Использовать расчётную подложку?" };
                sourceChoice.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Фон, обрамление и зоны по подложке");
                sourceChoice.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Фон и обрамление без подложки");
                sourceChoice.CommonButtons = TaskDialogCommonButtons.Cancel;
                var sourceResult = sourceChoice.Show();
                if (sourceResult == TaskDialogResult.Cancel) return Result.Cancelled;
                var mesh = new ImportedMeshSnapshot();
                if (sourceResult == TaskDialogResult.CommandLink1)
                {
                    var cadReference = uiDoc.Selection.PickObject(ObjectType.Element, new ImportInstanceSelectionFilter(), "Выберите расчётную DWG/DXF подложку с цветными поверхностными элементами");
                    var import = (ImportInstance)doc.GetElement(cadReference);
                    try { mesh = ImportedMeshReader.Read(doc, import, slab.Frame); }
                    catch (CadMetadataUnavailableException) { mesh = ReadOriginalDxf(doc, import, slab); }
                }
                var types = new FilteredElementCollector(doc).OfClass(typeof(RebarBarType)).Cast<RebarBarType>()
                    .OrderBy(t => t.Name, new AlphanumComparatorFastString()).ToList();
                if (types.Count == 0) throw new ArgumentException("В проекте нет типов арматуры. Загрузите их перед расчётом.");
                var colors = mesh.Colors.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new ColorStat
                {
                    Color = p.Key, ColorBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(p.Value.Red, p.Value.Green, p.Value.Blue)),
                    RebarTypes = types, Spacing = 200
                }).ToList();
                var session = new ReinforcementSession(slab, mesh, types);
                var window = new SlabReinforcementWPF(colors, types, session.CreateContext());
                new WindowInteropHelper(window).Owner = commandData.Application.MainWindowHandle;
                if (window.ShowDialog() != true) return Result.Cancelled;
                var plan = window.AcceptedPlan;
                int sets = RebarPlanWriter.Create(doc, slab, session, plan, window.AcceptedParameters);
                TaskDialog.Show("Армирование плит", $"Создано {plan.Bars.Count} стержней в {sets} наборах.");
                return Result.Succeeded;
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

        private static ImportedMeshSnapshot ReadOriginalDxf(Document doc, ImportInstance import, SlabGeometrySnapshot slab)
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
                var picker = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Исходный DXF для выбранной подложки", Filter = "Расчётный DXF (*.dxf)|*.dxf", CheckFileExists = true,
                    InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
                };
                if (picker.ShowDialog() != true) throw new System.OperationCanceledException();
                sourcePath = picker.FileName;
            }
            var units = new TaskDialog("Единицы DXF")
            {
                MainInstruction = "В каких единицах исходный DXF?",
                MainContent = Path.GetFileName(sourcePath) + "\nRevit не передал слои и цвета. Они будут прочитаны из этого файла.",
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
    }
}
