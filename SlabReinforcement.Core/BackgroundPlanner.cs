using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Buffer;

namespace SlabReinforcement.Core
{
    public sealed class BackgroundLayer
    {
        public string Key { get; set; }
        public string TypeKey { get; set; }
        public bool Top { get; set; }
        public bool AlongX { get; set; }
        public double DiameterMm { get; set; }
        public double ModelDiameterMm { get; set; }
        public double SpacingMm { get; set; }
    }

    public sealed class BackgroundRequest
    {
        public List<PolygonInput> Slabs { get; set; } = new List<PolygonInput>();
        public List<BackgroundLayer> Layers { get; set; } = new List<BackgroundLayer>();
        public double ThicknessMm { get; set; }
        public double TopCoverMm { get; set; }
        public double BottomCoverMm { get; set; }
        public double SideCoverMm { get; set; }
        public double OuterLayerOffsetMm { get; set; }
    }

    public static class BackgroundPlanner
    {
        public static ReinforcementPlan Build(BackgroundRequest request, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            var result = new ReinforcementPlan();
            try
            {
                if (request == null || request.Slabs == null || request.Slabs.Count == 0 || request.Layers == null || request.Layers.Count == 0)
                    throw new ArgumentException("Задайте плиту и хотя бы один слой фоновой арматуры.");
                if (request.Slabs.Count > GeometryKernel.MaximumPolygons || request.Layers.Count > 4)
                    throw new ArgumentException("Превышено допустимое количество контуров или слоёв.");
                foreach (double value in new[] { request.ThicknessMm, request.TopCoverMm, request.BottomCoverMm, request.SideCoverMm, request.OuterLayerOffsetMm })
                    if (!GeometryKernel.Finite(value) || value < 0) throw new ArgumentException("Размеры плиты и защитные слои должны быть конечными неотрицательными числами.");
                if (request.Layers.Any(l => l == null || string.IsNullOrWhiteSpace(l.Key) || string.IsNullOrWhiteSpace(l.TypeKey)
                    || !Positive(l.DiameterMm) || !Positive(l.ModelDiameterMm) || !Positive(l.SpacingMm) || l.SpacingMm < l.ModelDiameterMm))
                    throw new ArgumentException("Для фона задайте тип, положительные диаметр и шаг; шаг не меньше физического диаметра.");
                if (request.Layers.Select(l=>l.Key).Distinct().Count()!=request.Layers.Count
                    || request.Layers.GroupBy(l=>new {l.Top,l.AlongX}).Any(g=>g.Count()>1))
                    throw new ArgumentException("Фон содержит повторяющийся слой или ключ.");
                var placements = GetPlacements(request);
                var host = GeometryKernel.Union(request.Slabs.Select(GeometryKernel.ReadPolygon));
                foreach (var layer in request.Layers)
                {
                    token.ThrowIfCancellationRequested();
                    double inset = request.SideCoverMm + layer.ModelDiameterMm / 2;
                    var allowed = host.Buffer(-inset, new BufferParameters { JoinStyle=JoinStyle.Mitre, MitreLimit=5, SimplifyFactor=0 });
                    int before=result.Bars.Count;
                    foreach (var component in GeometryKernel.Polygons(allowed))
                    {
                        token.ThrowIfCancellationRequested();
                        var box=component.EnvelopeInternal;
                        double min=layer.AlongX?box.MinY:box.MinX, max=layer.AlongX?box.MaxY:box.MaxX;
                        double first=Math.Ceiling((min-1e-6)/layer.SpacingMm)*layer.SpacingMm;
                        double count=Math.Floor((max-first+1e-6)/layer.SpacingMm)+1;
                        if (count>100000 || result.Bars.Count+count>200000) return TooLarge(result);
                        // A thin disconnected component can lie wholly between two global grid lines.
                        if (count<1) {first=(min+max)/2;count=1;}
                        for(int row=0;row<(int)count;row++)
                        {
                            token.ThrowIfCancellationRequested();
                            double transverse=first+row*layer.SpacingMm;
                            var a=layer.AlongX?new Coordinate(box.MinX-1,transverse):new Coordinate(transverse,box.MinY-1);
                            var b=layer.AlongX?new Coordinate(box.MaxX+1,transverse):new Coordinate(transverse,box.MaxY+1);
                            foreach(var line in Lines(GeometryKernel.Intersect(component,GeometryKernel.Factory.CreateLineString(new[]{a,b}))))
                            {
                                if(line.Length<2) continue;
                                var points=line.Coordinates;
                                var start=new Point2(points[0].X,points[0].Y); var end=new Point2(points[points.Length-1].X,points[points.Length-1].Y);
                                if((layer.AlongX?start.X:start.Y)>(layer.AlongX?end.X:end.Y)) {var tmp=start;start=end;end=tmp;}
                                var bar=new PlannedBar { Start=start,End=end,SpecKey=layer.Key,ColorKey=layer.Top?"RGB(52, 130, 164)":"RGB(95, 108, 126)",Placement=placements[layer.Key] };
                                result.Bars.Add(bar);
                                if(result.Bars.Count>200000) return TooLarge(result);
                            }
                        }
                    }
                    if(result.Bars.Count==before) throw new ArgumentException("Слой фона «"+layer.Key+"» не помещается внутри защитного слоя.");
                }
                // Compression preserves every physical row and splits at the Revit array limit.
                foreach(var set in result.Bars.GroupBy(b=>new {b.SpecKey,Start=Longitudinal(b.Start,b.Placement),End=Longitudinal(b.End,b.Placement)}))
                {
                    var bars=set.OrderBy(b=>Transverse(b.Start,b.Placement)).ToList();
                    double step=request.Layers.Single(l=>l.Key==set.Key.SpecKey).SpacingMm;
                    for(int i=0;i<bars.Count;)
                    {
                        int count=1;
                        while(i+count<bars.Count && count<1002 && Math.Abs(Transverse(bars[i+count].Start,bars[i+count].Placement)-Transverse(bars[i].Start,bars[i].Placement)-count*step)<1e-6) count++;
                        var first=bars[i]; result.Groups.Add(new BarGroup {Start=first.Start,End=first.End,SpecKey=first.SpecKey,ColorKey=first.ColorKey,Placement=first.Placement,Count=count,SpacingMm=step});
                        i+=count;
                    }
                }
                result.Diagnostics.Add(new PlanDiagnostic { Code="BACKGROUND_DETAILING", Message="Фон обрезан по защитному слою. Анкеровка в опорах и стыки длинных стержней требуют принятого узла." });
            }
            catch(ArgumentException ex) { result.Bars.Clear();result.Groups.Clear();result.Diagnostics.Add(new PlanDiagnostic {Code="BACKGROUND_LAYERS",Message=ex.Message,IsBlocking=true}); }
            catch(TopologyException) { result.Bars.Clear();result.Groups.Clear();result.Diagnostics.Add(new PlanDiagnostic {Code="BACKGROUND_GEOMETRY",Message="Не удалось обработать контуры фоновой арматуры.",IsBlocking=true}); }
            return result;
        }

        public static Dictionary<string,BarPlacement> GetPlacements(BackgroundRequest request)
        {
            var result=new Dictionary<string,BarPlacement>();
            foreach(var layer in request.Layers)
            {
                // X is the outer row at both faces; Y rests immediately inside X.
                var outer=request.Layers.SingleOrDefault(l=>l.Top==layer.Top && l.AlongX);
                double offset=request.OuterLayerOffsetMm+(!layer.AlongX && outer!=null?outer.ModelDiameterMm:0);
                double z=BarElevation.Calculate(layer.Top,request.ThicknessMm,layer.Top?request.TopCoverMm:request.BottomCoverMm,
                    layer.Top?request.BottomCoverMm:request.TopCoverMm,layer.ModelDiameterMm,offset);
                result[layer.Key]=new BarPlacement {TypeKey=layer.TypeKey,Component="Background",ElevationMm=z,ModelDiameterMm=layer.ModelDiameterMm,
                    Distribution=layer.AlongX?new Point3(0,1,0):new Point3(1,0,0)};
            }
            var bottom=request.Layers.Where(l=>!l.Top).ToList(); var top=request.Layers.Where(l=>l.Top).ToList();
            if(bottom.Count>0 && top.Count>0 && bottom.Max(l=>result[l.Key].ElevationMm+l.ModelDiameterMm/2)>top.Min(l=>result[l.Key].ElevationMm-l.ModelDiameterMm/2)+1e-6)
                throw new ArgumentException("Верхняя и нижняя фоновые сетки пересекаются по толщине плиты.");
            return result;
        }
        private static bool Positive(double d)=>GeometryKernel.Finite(d)&&d>0;
        private static double Longitudinal(Point2 p,BarPlacement placement)=>placement.Distribution.Y>0.5?p.X:p.Y;
        private static double Transverse(Point2 p,BarPlacement placement)=>placement.Distribution.Y>0.5?p.Y:p.X;
        private static ReinforcementPlan TooLarge(ReinforcementPlan plan) {plan.Bars.Clear();plan.Groups.Clear();plan.Diagnostics.Add(new PlanDiagnostic{Code="PLAN_TOO_LARGE",Message="Слишком много стержней фона. Увеличьте шаг или сократите область.",IsBlocking=true});return plan;}
        private static IEnumerable<LineString> Lines(Geometry geometry)
        {
            if(geometry is LineString line && !line.IsEmpty) yield return line;
            else if(geometry is GeometryCollection collection)
                for(int i=0;i<collection.NumGeometries;i++) foreach(var item in Lines(collection.GetGeometryN(i))) yield return item;
        }
    }
}
