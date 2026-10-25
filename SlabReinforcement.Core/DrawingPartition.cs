using System;
using System.Linq;

namespace SlabReinforcement.Core
{
    /// <summary>Native partition tokens consumed by the Weandrevit drawing filters.</summary>
    public static class DrawingPartition
    {
        private const double DirectionTolerance = 1e-8;

        public static string ForBackground(bool top, bool alongX) => Layer(top, alongX) + " фон";
        public static string ForZone(bool top, bool alongX) => Layer(top, alongX) + " доп";

        public static string ForPerimeter(Point3 legDirection)
        {
            double scale = Math.Max(Math.Abs(legDirection.X), Math.Abs(legDirection.Y));
            if (!Finite(scale) || !Finite(legDirection.Z) || scale <= 0
                || Math.Abs(legDirection.Z) / scale > DirectionTolerance)
                throw new ArgumentException("Не удалось определить направление П-стержня для оформления чертежей.");
            bool alongX = Math.Abs(legDirection.X) / scale > DirectionTolerance;
            bool alongY = Math.Abs(legDirection.Y) / scale > DirectionTolerance;
            // One U-bar has both upper and lower legs. Tokens describe that same
            // physical element on both faces; they do not create duplicate bars.
            // Deliberately omit "доп": the template hides individual zone Rebar
            // by that token while keeping the Path representation visible.
            string layers = alongX ? "низ X верх X" : "низ Y верх Y";
            if (alongX && alongY) layers += " низ Y верх Y";
            return layers + " П-обрамление";
        }

        public static string ForGroup(BarGroup group, string selectedZoneDirection)
        {
            if (group == null) throw new ArgumentNullException(nameof(group));
            switch (group.Placement?.Component)
            {
                case "Background":
                    const string prefix = "Background|";
                    if (group.SpecKey == null || !group.SpecKey.StartsWith(prefix, StringComparison.Ordinal))
                        throw new ArgumentException("Не удалось определить слой фона для оформления чертежей.");
                    ReadLayer(group.SpecKey.Substring(prefix.Length), out bool backgroundTop, out bool backgroundX);
                    return ForBackground(backgroundTop, backgroundX);
                case "Zones":
                    ReadLayer(selectedZoneDirection, out bool zoneTop, out bool zoneX);
                    return ForZone(zoneTop, zoneX);
                case "PerimeterU":
                    var leg = group.Centerline?.FirstOrDefault(segment => !segment.Midpoint.HasValue
                        && Math.Abs(segment.End.Z - segment.Start.Z) <= DirectionTolerance
                        && (Math.Abs(segment.End.X - segment.Start.X) > DirectionTolerance
                            || Math.Abs(segment.End.Y - segment.Start.Y) > DirectionTolerance));
                    if (leg == null) throw new ArgumentException("Не удалось определить полку П-стержня для оформления чертежей.");
                    return ForPerimeter(new Point3(leg.End.X - leg.Start.X, leg.End.Y - leg.Start.Y, leg.End.Z - leg.Start.Z));
                default:
                    return null;
            }
        }

        private static string Layer(bool top, bool alongX) => (top ? "верх " : "низ ") + (alongX ? "X" : "Y");
        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        private static void ReadLayer(string direction, out bool top, out bool alongX)
        {
            switch (direction)
            {
                case "Низ X": top = false; alongX = true; return;
                case "Низ Y": top = false; alongX = false; return;
                case "Верх X": top = true; alongX = true; return;
                case "Верх Y": top = true; alongX = false; return;
                default: throw new ArgumentException("Не удалось определить направление арматуры для оформления чертежей.");
            }
        }
    }
}
