using Autodesk.Revit.DB;
using Autodesk.Revit.UI.Selection;

namespace SlabReinforcement.Integration
{
    /// <summary>Accepts a straight edge belonging to the floor selected for reinforcement.</summary>
    internal sealed class SlabEdgeSelectionFilter : ISelectionFilter
    {
        private readonly Floor floor;

        public SlabEdgeSelectionFilter(Floor floor)
        {
            this.floor = floor;
        }

        public bool AllowElement(Element element)
        {
            return floor != null && element != null && element.Id.Equals(floor.Id);
        }

        public bool AllowReference(Reference reference, XYZ position)
        {
            if (floor == null || reference == null || !reference.ElementId.Equals(floor.Id))
                return false;

            var edge = floor.GetGeometryObjectFromReference(reference) as Edge;
            return edge != null && edge.AsCurve() is Line;
        }
    }
}
