using System;
using System.Collections.Generic;
using SlabReinforcement.Core;

namespace SlabReinforcement.Dxf
{
    public sealed class DxfSource
    {
        public string Version { get; set; } = "";
        /// <summary>Raw $INSUNITS: null means absent; zero means unitless. No unit is inferred.</summary>
        public int? InsUnits { get; set; }
        public List<DxfSourceFace> Faces { get; } = new List<DxfSourceFace>();
        public Dictionary<string, int> LayerColors { get; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> LayerColorKeys { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> EntityCounts { get; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public List<string> Warnings { get; } = new List<string>();
        public int IgnoredPaperSpaceFaceCount { get; set; }
        public int BlockFaceCount { get; set; }
    }

    public sealed class DxfSourceFace
    {
        public string Id { get; set; } = "";
        public string Layer { get; set; } = "0";
        /// <summary>TrueColor is #RRGGBB; an indexed color is ACI:n. ACI:0 stays unresolved ByBlock.</summary>
        public string ColorKey { get; set; } = "ACI:256";
        public string OriginalColorKey { get; set; } = "ACI:256";
        public bool IsVisible { get; set; } = true;
        /// <summary>WCS vertices in raw drawing units, deliberately unscaled and unflattened.</summary>
        public List<Point3> Vertices { get; set; } = new List<Point3>();
    }
}
