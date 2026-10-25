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
        // Configured rows define the vertical order even when they are created separately.
        public List<BackgroundLayer> PlacementLayers { get; set; } = new List<BackgroundLayer>();
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
                    throw new ArgumentException("Выберите плиту и включите хотя бы один слой фона.");
                if (request.Slabs.Count > GeometryKernel.MaximumPolygons || request.Layers.Count > 4)
                    throw new ArgumentException("Слишком много контуров или слоёв фона. Проверьте выбранную область и слои.");
                foreach (double value in new[] { request.ThicknessMm, request.TopCoverMm, request.BottomCoverMm, request.SideCoverMm, request.OuterLayerOffsetMm })
                    if (!GeometryKernel.Finite(value) || value < 0) throw new ArgumentException("Проверьте размеры плиты и защитные слои: нужны числа не меньше нуля.");
                if (request.Layers.Any(l => l == null || string.IsNullOrWhiteSpace(l.Key) || string.IsNullOrWhiteSpace(l.TypeKey)
                    || !Positive(l.DiameterMm) || !Positive(l.ModelDiameterMm) || !Positive(l.SpacingMm) || l.SpacingMm < l.ModelDiameterMm))
                    throw new ArgumentException("Выберите тип фона и шаг больше нуля, не меньше диаметра стержня.");
                if (request.Layers.Select(l=>l.Key).Distinct().Count()!=request.Layers.Count
                    || request.Layers.GroupBy(l=>new {l.Top,l.AlongX}).Any(g=>g.Count()>1))
                    throw new ArgumentException("Один слой фона указан несколько раз. Оставьте одно назначение для каждого слоя.");
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
                        var rows=Rows(component,layer);
                        if(rows==null||result.Bars.Count+rows.Count>200000)return TooLarge(result);
                        foreach(double transverse in rows)
                        {
                            token.ThrowIfCancellationRequested();
                            var a=layer.AlongX?new Coordinate(box.MinX-1,transverse):new Coordinate(transverse,box.MinY-1);
                            var b=layer.AlongX?new Coordinate(box.MaxX+1,transverse):new Coordinate(transverse,box.MaxY+1);
                            var intervals=Lines(GeometryKernel.Intersect(component,GeometryKernel.Factory.CreateLineString(new[]{a,b})))
                                .Select(line=>new[]{layer.AlongX?line.EnvelopeInternal.MinX:line.EnvelopeInternal.MinY,layer.AlongX?line.EnvelopeInternal.MaxX:line.EnvelopeInternal.MaxY})
                                .OrderBy(v=>v[0]).ToList();
                            var merged=new List<double[]>();
                            foreach(var interval in intervals)
                            {
                                if(merged.Count==0||interval[0]>merged[merged.Count-1][1]+1e-6)merged.Add(interval);
                                else merged[merged.Count-1][1]=Math.Max(merged[merged.Count-1][1],interval[1]);
                            }
                            foreach(var interval in merged)
                            {
                                if(interval[1]-interval[0]<2)continue;
                                var start=layer.AlongX?new Point2(interval[0],transverse):new Point2(transverse,interval[0]);
                                var end=layer.AlongX?new Point2(interval[1],transverse):new Point2(transverse,interval[1]);
                                var bar=new PlannedBar { Start=start,End=end,SpecKey=layer.Key,ColorKey=layer.Top?"RGB(52, 130, 164)":"RGB(95, 108, 126)",Placement=placements[layer.Key] };
                                result.Bars.Add(bar);
                                if(result.Bars.Count>200000) return TooLarge(result);
                            }
                        }
                    }
                    if(result.Bars.Count==before) throw new ArgumentException("Слой фона «"+layer.Key+"» не помещается. Проверьте диаметр и защитный слой.");
                    var physical=result.Bars.Skip(before).OrderBy(b=>Transverse(b.Start,b.Placement)).ToList();
                    int checks=0;
                    for(int i=0;i<physical.Count;i++)for(int j=i-1;j>=0;j--)
                    {
                        token.ThrowIfCancellationRequested();
                        if(Transverse(physical[i].Start,physical[i].Placement)-Transverse(physical[j].Start,physical[j].Placement)>=layer.ModelDiameterMm-1e-6)break;
                        if(++checks>CompositePlanValidator.MaximumCandidatePairs)return TooLarge(result);
                        if(Math.Min(Longitudinal(physical[i].End,physical[i].Placement),Longitudinal(physical[j].End,physical[j].Placement))
                            -Math.Max(Longitudinal(physical[i].Start,physical[i].Placement),Longitudinal(physical[j].Start,physical[j].Placement))>1e-6)
                        {
                            result.Bars.Clear();result.Groups.Clear();result.Diagnostics.Add(new PlanDiagnostic {Code="BACKGROUND_BOUNDARY_CONFLICT",IsBlocking=true,
                                Message="Участок слишком узкий: стержни фона пересекаются. Здесь нужно отдельное армирование."});return result;
                        }
                    }
                }
                // Compression preserves every physical row and splits at the Revit array limit.
                foreach(var set in result.Bars.GroupBy(b=>new {b.SpecKey,Start=Longitudinal(b.Start,b.Placement),End=Longitudinal(b.End,b.Placement)}))
                {
                    var bars=set.OrderBy(b=>Transverse(b.Start,b.Placement)).ToList();
                    double maximum=request.Layers.Single(l=>l.Key==set.Key.SpecKey).SpacingMm;
                    for(int i=0;i<bars.Count;)
                    {
                        int count=1;double step=i+1<bars.Count?Transverse(bars[i+1].Start,bars[i+1].Placement)-Transverse(bars[i].Start,bars[i].Placement):0;
                        while(i+count<bars.Count&&count<1002&&step>0&&step<=maximum+1e-6)
                        {
                            double length=Transverse(bars[i+count].Start,bars[i+count].Placement)-Transverse(bars[i].Start,bars[i].Placement);
                            if(Math.Abs(length-count*step)>1e-6||(int)Math.Ceiling(length/maximum)+1!=count+1)break;
                            count++;
                        }
                        var first=bars[i];double arrayLength=count>1?Transverse(bars[i+count-1].Start,first.Placement)-Transverse(first.Start,first.Placement):0;
                        result.Groups.Add(new BarGroup {Start=first.Start,End=first.End,SpecKey=first.SpecKey,ColorKey=first.ColorKey,Placement=first.Placement,
                            Count=count,SpacingMm=count>1?arrayLength/(count-1):0,LayoutRule=BarLayoutRule.MaximumSpacing,MaximumSpacingMm=maximum,ArrayLengthMm=arrayLength});
                        i+=count;
                    }
                }
                result.Diagnostics.Add(new PlanDiagnostic { Code="BACKGROUND_DETAILING", Message="Анкеровку фона и стыки нужно задать отдельно." });
            }
            catch(ArgumentException ex) { result.Bars.Clear();result.Groups.Clear();result.Diagnostics.Add(new PlanDiagnostic {Code="BACKGROUND_LAYERS",Message=ex.Message,IsBlocking=true}); }
            catch(TopologyException) { result.Bars.Clear();result.Groups.Clear();result.Diagnostics.Add(new PlanDiagnostic {Code="BACKGROUND_GEOMETRY",Message="Не удалось построить фон. Проверьте контур плиты и отверстия.",IsBlocking=true}); }
            return result;
        }

        public static Dictionary<string,BarPlacement> GetPlacements(BackgroundRequest request)
        {
            var result=new Dictionary<string,BarPlacement>();
            bool explicitOrder=request.PlacementLayers!=null&&request.PlacementLayers.Count>0;
            var definitions=explicitOrder?request.PlacementLayers:request.Layers;
            if(definitions.Count>4||definitions.Any(l=>l==null)
                ||definitions.GroupBy(l=>new{l.Top,l.AlongX}).Any(g=>g.Count()>1))
                throw new ArgumentException("Проверьте слои фона: направления не должны повторяться.");
            foreach(var layer in request.Layers)
            {
                // X is the outer row at both faces; Y rests immediately inside X.
                var outer=definitions.SingleOrDefault(l=>l.Top==layer.Top && l.AlongX);
                if(!layer.AlongX&&explicitOrder&&outer==null)
                    throw new ArgumentException("Для слоя Y выберите тип слоя X той же стороны, даже выключенного.");
                if(!layer.AlongX&&outer!=null&&!Positive(outer.ModelDiameterMm))
                    throw new ArgumentException("Чтобы определить высоту слоя Y, задайте диаметр слоя X больше нуля.");
                double offset=request.OuterLayerOffsetMm+(!layer.AlongX && outer!=null?outer.ModelDiameterMm:0);
                double z=BarElevation.Calculate(layer.Top,request.ThicknessMm,layer.Top?request.TopCoverMm:request.BottomCoverMm,
                    layer.Top?request.BottomCoverMm:request.TopCoverMm,layer.ModelDiameterMm,offset);
                result[layer.Key]=new BarPlacement {TypeKey=layer.TypeKey,Component="Background",ElevationMm=z,ModelDiameterMm=layer.ModelDiameterMm,
                    Distribution=layer.AlongX?new Point3(0,1,0):new Point3(1,0,0)};
            }
            var bottom=request.Layers.Where(l=>!l.Top).ToList(); var top=request.Layers.Where(l=>l.Top).ToList();
            if(bottom.Count>0 && top.Count>0 && bottom.Max(l=>result[l.Key].ElevationMm+l.ModelDiameterMm/2)>top.Min(l=>result[l.Key].ElevationMm-l.ModelDiameterMm/2)+1e-6)
                throw new ArgumentException("Верхний и нижний фон пересекаются. Проверьте диаметры и защитные слои.");
            return result;
        }
        private static bool Positive(double d)=>GeometryKernel.Finite(d)&&d>0;
        private static List<double> Rows(Polygon component,BackgroundLayer layer)
        {
            var box=component.EnvelopeInternal;
            double min=layer.AlongX?box.MinY:box.MinX,max=layer.AlongX?box.MaxY:box.MaxX;
            if(max-min<=layer.ModelDiameterMm+1e-6)return new List<double>{(min+max)/2};
            var required=new SortedSet<double>{min,max};
            foreach(var ring in new[]{component.ExteriorRing}.Concat(Enumerable.Range(0,component.NumInteriorRings).Select(component.GetInteriorRingN)))
            {
                var points=ring.Coordinates;
                for(int i=1;i<points.Length;i++)
                {
                    double a=layer.AlongX?points[i-1].Y:points[i-1].X,b=layer.AlongX?points[i].Y:points[i].X;
                    if(Math.Abs(a-b)<=1e-6)required.Add((a+b)/2);
                }
            }
            var bounds=new List<double>();foreach(double value in required)if(bounds.Count==0||value-bounds[bounds.Count-1]>1e-6)bounds.Add(value);
            var rows=new List<double>{bounds[0]};
            for(int i=1;i<bounds.Count;i++)
            {
                double span=bounds[i]-bounds[i-1],intervals=Math.Ceiling(span/layer.SpacingMm);
                if(intervals>100000||rows.Count+intervals>100000)return null;
                for(int j=1;j<=(int)intervals;j++)rows.Add(j==(int)intervals?bounds[i]:bounds[i-1]+span*j/intervals);
            }
            return rows;
        }
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
