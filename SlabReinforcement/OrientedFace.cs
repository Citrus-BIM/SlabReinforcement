using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using System.Collections.Generic;
using System.Linq;

namespace SlabReinforcement
{
    public class OrientedFace
    {
        public FiniteElementFace Face { get; }
        public List<UV> LocalVertices { get; }
        public double MinU { get; }
        public double MaxU { get; }
        public double MinV { get; }
        public double MaxV { get; }
        public double CenterU { get; }
        public double CenterV { get; }

        public RebarBarType RebarType => Face.RebarType;
        public double Spacing => Face.Spacing;

        public OrientedFace(FiniteElementFace face, LocalCoordinateSystem coordinateSystem)
        {
            Face = face;
            LocalVertices = face.Vertices
                .Select(coordinateSystem.ToLocal)
                .ToList();

            MinU = LocalVertices.Min(v => v.U);
            MaxU = LocalVertices.Max(v => v.U);
            MinV = LocalVertices.Min(v => v.V);
            MaxV = LocalVertices.Max(v => v.V);
            CenterU = (MinU + MaxU) / 2.0;
            CenterV = (MinV + MaxV) / 2.0;
        }
    }
}
