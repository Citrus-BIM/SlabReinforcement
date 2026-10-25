// Compile the real session worker in the portable test assembly. Revit-facing calls
// throw so these tests cannot accidentally exercise an imitation of the document API.
using SlabReinforcement.Core;

namespace Autodesk.Revit.DB
{
    internal sealed class Floor { public string Name => throw new NotSupportedException(); }
    internal enum StorageType { Double, Integer, String }
    internal sealed class Parameter
    {
        public bool HasValue => throw new NotSupportedException();
        public StorageType StorageType => throw new NotSupportedException();
        public double AsDouble() => throw new NotSupportedException();
        public int AsInteger() => throw new NotSupportedException();
        public string AsString() => throw new NotSupportedException();
    }
}

namespace Autodesk.Revit.DB.Structure
{
    internal sealed class RebarBarType
    {
        public string UniqueId => throw new NotSupportedException();
        public string Name => throw new NotSupportedException();
        public double BarNominalDiameter => throw new NotSupportedException();
        public double BarModelDiameter => throw new NotSupportedException();
        public double StandardBendDiameter => throw new NotSupportedException();
        public Autodesk.Revit.DB.Parameter get_Parameter(Guid id) => throw new NotSupportedException();
    }
}

namespace SlabReinforcement.Integration
{
    internal sealed class SlabGeometrySnapshot
    {
        public Autodesk.Revit.DB.Floor Host { get; set; }
        public List<PolygonInput> Slabs { get; set; }
        public double ThicknessMm { get; set; }
        public double TopCoverMm { get; set; }
        public double BottomCoverMm { get; set; }
        public double SideCoverMm { get; set; }
        public string AxisDescription { get; set; }
    }
    internal sealed class ImportedMeshSnapshot
    {
        public List<CadPlaneGroup> Groups { get; } = new();
        public List<string> Warnings { get; } = new();
    }
    internal static class ExistingRebarReader
    {
        public static List<PlannedBar> Read(SlabGeometrySnapshot slab) => throw new NotSupportedException();
    }
    internal static class SlabCoverSettings
    {
        public const string ExistingReinforcementMessage = "Existing reinforcement prevents a cover change.";
        public static bool HasReinforcement(Autodesk.Revit.DB.Floor floor) => throw new NotSupportedException();
        public static SlabCoverValues FromSnapshot(SlabGeometrySnapshot slab) =>
            new(slab.TopCoverMm, slab.BottomCoverMm, slab.SideCoverMm);
        public static SlabCoverValues Resolve(SlabGeometrySnapshot slab, PreviewParameters p) =>
            SlabCoverValues.Resolve(p.TopCoverMm, p.BottomCoverMm, p.EdgeCoverMm, FromSnapshot(slab), slab.ThicknessMm);
    }
}
