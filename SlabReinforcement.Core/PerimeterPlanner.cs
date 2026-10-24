using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Buffer;

namespace SlabReinforcement.Core
{
    public sealed class PerimeterRequest
    {
        public List<PolygonInput> Slabs { get; set; } = new List<PolygonInput>();
        public string TypeKey { get; set; }
        public double DiameterMm { get; set; }
        public double ModelDiameterMm { get; set; }
        public double BendDiameterMm { get; set; }
        public double ThicknessMm { get; set; }
        public double TopCoverMm { get; set; }
        public double BottomCoverMm { get; set; }
        public double SideCoverMm { get; set; }
        public double SpacingMm { get; set; }
        public double LegLengthMm { get; set; }
        public bool Outer { get; set; }
        public bool Openings { get; set; }
        public bool UseUbars { get; set; }
        public bool UseLongitudinalBars { get; set; }
    }

    public static class PerimeterPlanner
    {
        public static double ReservedDepth(PerimeterRequest request)
        {
            var host=GeometryKernel.Union(request.Slabs.Select(GeometryKernel.ReadPolygon));
            var inset=host.Buffer(-request.SideCoverMm-request.ModelDiameterMm/2,new BufferParameters{JoinStyle=JoinStyle.Mitre,MitreLimit=5,SimplifyFactor=0});
            int bands = GeometryKernel.ToInputs(inset).SelectMany(p=>SelectedRings(p,request)).Select(r=>r.Count%2==0?2:3).DefaultIfEmpty(2).Max();
            return (request.UseUbars?bands*request.ModelDiameterMm:0)+(request.UseLongitudinalBars?request.ModelDiameterMm:0);
        }

        public static ReinforcementPlan Build(PerimeterRequest request,CancellationToken token=default)
        {
            token.ThrowIfCancellationRequested();var result=new ReinforcementPlan();
            try
            {
                if(request==null || request.Slabs==null || request.Slabs.Count==0 || string.IsNullOrWhiteSpace(request.TypeKey))
                    throw new ArgumentException("Для обрамления задайте плиту и тип арматуры.");
                if(!request.Outer && !request.Openings || !request.UseUbars && !request.UseLongitudinalBars)
                    throw new ArgumentException("Выберите контуры и вид обрамления.");
                foreach(double value in new[]{request.DiameterMm,request.ModelDiameterMm,request.BendDiameterMm,request.ThicknessMm,request.SpacingMm,request.LegLengthMm})
                    if(!GeometryKernel.Finite(value)||value<=0) throw new ArgumentException("Диаметр, радиус загиба, толщина, шаг и длина лапки должны быть положительными.");
                foreach(double value in new[]{request.TopCoverMm,request.BottomCoverMm,request.SideCoverMm})
                    if(!GeometryKernel.Finite(value)||value<0) throw new ArgumentException("Защитные слои должны быть конечными неотрицательными числами.");
                if(request.SpacingMm<request.ModelDiameterMm) throw new ArgumentException("Шаг обрамления меньше физического диаметра.");
                var host=GeometryKernel.Union(request.Slabs.Select(GeometryKernel.ReadPolygon));
                double radius=request.ModelDiameterMm/2;
                double bendRadius=(request.BendDiameterMm+request.ModelDiameterMm)/2;
                var allowed=host.Buffer(-request.SideCoverMm-radius,new BufferParameters {JoinStyle=JoinStyle.Mitre,MitreLimit=5,SimplifyFactor=0});
                // Erosion can merge openings with each other or with the outside.
                // Selecting only surviving inset rings would silently omit requested
                // opening edges, even though every remaining bar passes containment.
                if(request.Openings && GeometryKernel.Polygons(host).Sum(p=>p.NumInteriorRings)
                    !=GeometryKernel.Polygons(allowed).Sum(p=>p.NumInteriorRings))
                {
                    result.Diagnostics.Add(new PlanDiagnostic{Code="PERIMETER_OPENING_TOPOLOGY",
                        Message="После учёта защитного слоя отверстия сливаются между собой или с наружным контуром. Полное обрамление выбранных отверстий не может быть построено; узкие перемычки требуют отдельного решения.",IsBlocking=true});
                    return result;
                }
                if(request.Outer)
                {
                    // Each eroded component remains inside exactly one original
                    // host polygon. Check that identity, not the total count: a
                    // vanished island and a split island can cancel in that count.
                    var survivingComponents=GeometryKernel.Polygons(allowed).Select(p=>new {Polygon=p,Point=p.InteriorPoint}).ToList();
                    foreach(var original in GeometryKernel.Polygons(host))
                    {
                        token.ThrowIfCancellationRequested();
                        var matches=survivingComponents.Where(part=>original.Covers(part.Point)).ToList();
                        bool changed=matches.Count!=1;
                        if(!changed && original.NumInteriorRings>0)
                        {
                            var inset=matches[0].Polygon;
                            var holes=Enumerable.Range(0,inset.NumInteriorRings)
                                .Select(i=>GeometryKernel.Factory.CreatePolygon(inset.GetInteriorRingN(i).Coordinates)).ToList();
                            // A former hole joining the outside changes the selected
                            // exterior even if the host still has one component. Merged
                            // internal holes do not change it when openings are unselected.
                            for(int i=0;i<original.NumInteriorRings;i++)
                            {
                                token.ThrowIfCancellationRequested();
                                var inside=GeometryKernel.Factory.CreatePolygon(original.GetInteriorRingN(i).Coordinates).InteriorPoint;
                                if(holes.Any(hole=>hole.Covers(inside))) continue;
                                changed=true;
                                break;
                            }
                        }
                        if(!changed) continue;
                        result.Diagnostics.Add(new PlanDiagnostic{Code="PERIMETER_OUTER_TOPOLOGY",
                            Message="После учёта защитного слоя часть плиты исчезает, разделяется или наружный контур соединяется с отверстием. Полное обрамление выбранного наружного контура не может быть построено; узкие участки требуют отдельного решения.",IsBlocking=true});
                        return result;
                    }
                }
                var rings=GeometryKernel.ToInputs(allowed).SelectMany(p=>SelectedRings(p,request)).ToList();
                if(rings.Count==0) throw new ArgumentException("Выбранных контуров для обрамления нет.");
                if(rings.Sum(r=>r.Count)>2000) throw new ArgumentException("Слишком сложный контур обрамления. Для MVP нужны прямолинейные края без плотной аппроксимации дуг.");
                int ringIndex=0;
                foreach(var ring in rings)
                {
                    token.ThrowIfCancellationRequested();
                    if(request.UseUbars)
                    for(int edge=0;edge<ring.Count;edge++)
                    {
                        var a=ring[edge];var b=ring[(edge+1)%ring.Count];
                        var along=Unit(new Point2(b.X-a.X,b.Y-a.Y));var inward=new Point2(-along.Y,along.X);
                        double length=Distance(a,b);
                        // Shells are CCW and openings CW: concrete is always on the left.
                        int band=(ring.Count%2!=0 && edge==ring.Count-1)?2:edge%2;
                        double zBottom=-request.ThicknessMm+request.BottomCoverMm+radius+band*request.ModelDiameterMm;
                        double zTop=-request.TopCoverMm-radius-band*request.ModelDiameterMm;
                        if(zTop-zBottom<2*bendRadius+2 || request.LegLengthMm<=bendRadius+2)
                            throw new ArgumentException("П-образный стержень с фактическим загибом не помещается по толщине или длине лапки.");
                        int count=(int)Math.Ceiling(length/request.SpacingMm);
                        if(count>100000 || result.Bars.Count+count>200000) throw new ArgumentException("Слишком много П-образных стержней.");
                        // A centred station sequence avoids a doubled U bar at each corner.
                        double step=length/count;
                        if(step<request.ModelDiameterMm) throw new ArgumentException("Участок контура слишком короткий для размещения обрамления.");
                        for(int i=0;i<count;i++)
                        {
                            token.ThrowIfCancellationRequested();
                            var edgePoint=new Point2(a.X+along.X*step*(i+0.5),a.Y+along.Y*step*(i+0.5));
                            Func<double,double,Point3> at=(x,z)=>new Point3(edgePoint.X+inward.X*x,edgePoint.Y+inward.Y*x,z);
                            double q=1-Math.Sqrt(0.5);
                            var path=new List<CenterlineSegment>
                            {
                                Segment(at(request.LegLengthMm,zBottom),at(bendRadius,zBottom)),
                                Segment(at(bendRadius,zBottom),at(0,zBottom+bendRadius),at(bendRadius*q,zBottom+bendRadius*q)),
                                Segment(at(0,zBottom+bendRadius),at(0,zTop-bendRadius)),
                                Segment(at(0,zTop-bendRadius),at(bendRadius,zTop),at(bendRadius*q,zTop-bendRadius*q)),
                                Segment(at(bendRadius,zTop),at(request.LegLengthMm,zTop))
                            };
                            // U projection is a complete line; this catches a leg crossing a nearby hole.
                            var projected=GeometryKernel.Factory.CreateLineString(new[]{new Coordinate(edgePoint.X,edgePoint.Y),new Coordinate(edgePoint.X+inward.X*request.LegLengthMm,edgePoint.Y+inward.Y*request.LegLengthMm)});
                            if(!allowed.Buffer(1e-5).Covers(projected))
                            {
                                result.Diagnostics.Add(new PlanDiagnostic{Code="PERIMETER_CONTAINMENT",Message="Лапка обрамления выходит из бетона или пересекает отверстие. Уменьшите длину только после проверки узла.",SourceId=ringIndex+":"+edge,IsBlocking=true});
                                continue;
                            }
                            Add(result,path,request,"PerimeterU",new Point3(along.X,along.Y,0));
                        }
                    }
                    ringIndex++;
                }
                if(request.UseLongitudinalBars)
                {
                    double uDepth=ReservedDepth(request)-request.ModelDiameterMm;
                    if(request.TopCoverMm+request.BottomCoverMm+2*(uDepth+request.ModelDiameterMm)>request.ThicknessMm+1e-6)
                        throw new ArgumentException("Верхнее и нижнее продольное обрамление пересекаются по толщине плиты.");
                    // Extra setback leaves room for a real bend at re-entrant/hole corners.
                    double inset=request.SideCoverMm+radius+uDepth+bendRadius;
                    var longitudinal=host.Buffer(-inset,new BufferParameters{JoinStyle=JoinStyle.Mitre,MitreLimit=5,SimplifyFactor=0});
                    var longitudinalRings=GeometryKernel.ToInputs(longitudinal).SelectMany(p=>SelectedRings(p,request)).ToList();
                    if(longitudinalRings.Count!=rings.Count) throw new ArgumentException("После отступа продольного обрамления изменилось число контуров. Узкие участки требуют отдельного решения.");
                    foreach(var ring in longitudinalRings)
                    foreach(bool top in new[]{false,true})
                    {
                        token.ThrowIfCancellationRequested();
                        double z=BarElevation.Calculate(top,request.ThicknessMm,top?request.TopCoverMm:request.BottomCoverMm,
                            top?request.BottomCoverMm:request.TopCoverMm,request.ModelDiameterMm,uDepth);
                        var path=RoundedRing(ring,z,bendRadius);
                        var projected=GeometryKernel.Factory.CreateLineString(Sample(path,0.01).Select(p=>new Coordinate(p.X,p.Y)).ToArray());
                        if(!projected.IsSimple) throw new ArgumentException("Продольное обрамление имеет самопересечение после загибов.");
                        if(!allowed.Buffer(1e-5).Covers(projected)) throw new ArgumentException("Загиб продольного обрамления выходит за допустимый бетон.");
                        Add(result,path,request,"PerimeterLongitudinal",new Point3(0,0,1));
                    }
                    result.Diagnostics.Add(new PlanDiagnostic{Code="PERIMETER_SPLICES",Message="Продольное обрамление показано замкнутым контуром; стык и разбивка на монтажные марки задаются отдельным узлом."});
                }
                if(result.Bars.Count==0 && !result.Diagnostics.Any(d=>d.IsBlocking)) throw new ArgumentException("Не удалось разместить обрамление.");
            }
            catch(ArgumentException ex){result.Diagnostics.Add(new PlanDiagnostic{Code="PERIMETER_GEOMETRY",Message=ex.Message,IsBlocking=true});}
            catch(TopologyException){result.Diagnostics.Add(new PlanDiagnostic{Code="PERIMETER_GEOMETRY",Message="Ошибка геометрии контура обрамления.",IsBlocking=true});}
            return result;
        }

        private static IEnumerable<List<Point2>> SelectedRings(PolygonInput p,PerimeterRequest request)
        {if(request.Outer) yield return p.Shell;if(request.Openings)foreach(var hole in p.Holes)yield return hole;}
        private static CenterlineSegment Segment(Point3 a,Point3 b,Point3? mid=null)=>new CenterlineSegment{Start=a,End=b,Midpoint=mid};
        private static void Add(ReinforcementPlan plan,List<CenterlineSegment> path,PerimeterRequest request,string component,Point3 normal)
        {
            var placement=new BarPlacement{TypeKey=request.TypeKey,Component=component,ModelDiameterMm=request.ModelDiameterMm,Distribution=normal,ElevationMm=path[0].Start.Z};
            var a=path[0].Start;var b=path[path.Count-1].End;string key=component+"|"+plan.Bars.Count;
            var bar=new PlannedBar{Start=new Point2(a.X,a.Y),End=new Point2(b.X,b.Y),SpecKey=key,ColorKey="RGB(219, 100, 48)",Placement=placement,Centerline=path};
            plan.Bars.Add(bar);plan.Groups.Add(new BarGroup{Start=bar.Start,End=bar.End,SpecKey=key,ColorKey=bar.ColorKey,Count=1,Placement=placement,Centerline=path});
        }
        private static List<CenterlineSegment> RoundedRing(List<Point2> ring,double z,double radius)
        {
            var corners=new List<CenterlineSegment>();
            for(int i=0;i<ring.Count;i++)
            {
                var prev=ring[(i+ring.Count-1)%ring.Count];var v=ring[i];var next=ring[(i+1)%ring.Count];
                var incoming=Unit(new Point2(v.X-prev.X,v.Y-prev.Y));var outgoing=Unit(new Point2(next.X-v.X,next.Y-v.Y));
                double dot=incoming.X*outgoing.X+incoming.Y*outgoing.Y;double cross=incoming.X*outgoing.Y-incoming.Y*outgoing.X;
                if(Math.Abs(cross)<1e-8) throw new ArgumentException("Для обрамления удалите коллинеарные короткие рёбра контура.");
                double trim=radius*Math.Sqrt((1-dot)/(1+dot));
                if(trim>=Math.Min(Distance(prev,v),Distance(v,next))/2-1) throw new ArgumentException("Загиб продольного обрамления не помещается на коротком ребре.");
                var a=new Point3(v.X-incoming.X*trim,v.Y-incoming.Y*trim,z);var b=new Point3(v.X+outgoing.X*trim,v.Y+outgoing.Y*trim,z);
                double sign=Math.Sign(cross);var centre=new Point2(a.X-incoming.Y*radius*sign,a.Y+incoming.X*radius*sign);
                var bisector=Unit(new Point2((a.X+b.X)/2-centre.X,(a.Y+b.Y)/2-centre.Y));
                corners.Add(Segment(a,b,new Point3(centre.X+radius*bisector.X,centre.Y+radius*bisector.Y,z)));
            }
            // A shape-driven closed bar must start on a straight segment: Revit does
            // not fillet the common first/last vertex if that seam is at a corner.
            var midpoint=Multiply(Add(corners[0].End,corners[1].Start),0.5);
            var path=new List<CenterlineSegment>{Segment(midpoint,corners[1].Start)};
            for(int i=1;i<corners.Count;i++)
            {
                path.Add(corners[i]);
                path.Add(Segment(corners[i].End,corners[(i+1)%corners.Count].Start));
            }
            path.Add(corners[0]);path.Add(Segment(corners[0].End,midpoint));
            return path;
        }
        // Used for preview/containment only. Each three-point circular arc is sampled by its actual circle.
        public static IEnumerable<Point3> Sample(IEnumerable<CenterlineSegment> path,double tolerance)
        {
            if(!GeometryKernel.Finite(tolerance)||tolerance<=0)throw new ArgumentException("Допуск аппроксимации должен быть положительным конечным числом.");
            Point3? previous=null;
            foreach(var point in SampleRaw(path,tolerance))
            {
                if(previous.HasValue && Dot(Subtract(point,previous.Value),Subtract(point,previous.Value))<1e-18)continue;
                yield return point;previous=point;
            }
        }
        private static IEnumerable<Point3> SampleRaw(IEnumerable<CenterlineSegment> path,double tolerance)
        {
            foreach(var segment in path)
            {
                yield return segment.Start;
                if(segment.Midpoint.HasValue)
                {
                    var a=segment.Start;var m=segment.Midpoint.Value;var b=segment.End;
                    var u=Subtract(m,a);var v=Subtract(b,a);var n=Cross(u,v);double n2=Dot(n,n);
                    if(n2<=1e-18)throw new ArgumentException("Вырожденная дуга обрамления.");
                    var cv=Cross(v,n);var cu=Cross(n,u);
                    var centre=Add(a,Multiply(Add(Multiply(cv,Dot(u,u)),Multiply(cu,Dot(v,v))),1/(2*n2)));
                    double r=Math.Sqrt(Dot(Subtract(a,centre),Subtract(a,centre)));
                    var ex=Multiply(Subtract(a,centre),1/r);var normal=Multiply(n,1/Math.Sqrt(n2));var ey=Cross(normal,ex);
                    double sweep=Math.Atan2(Dot(Subtract(b,centre),ey),Dot(Subtract(b,centre),ex));if(sweep<=0)sweep+=2*Math.PI;
                    int count=Math.Max(2,(int)Math.Ceiling(sweep/(2*Math.Acos(Math.Max(-1,Math.Min(1,1-tolerance/r))))));
                    if(count>100000)throw new ArgumentException("Слишком много точек дуги.");
                    for(int i=1;i<count;i++)yield return Add(centre,Multiply(Add(Multiply(ex,Math.Cos(sweep*i/count)),Multiply(ey,Math.Sin(sweep*i/count))),r));
                }
                yield return segment.End;
            }
        }
        private static double Distance(Point2 a,Point2 b)=>Math.Sqrt((a.X-b.X)*(a.X-b.X)+(a.Y-b.Y)*(a.Y-b.Y));
        private static Point2 Unit(Point2 p){double l=Math.Sqrt(p.X*p.X+p.Y*p.Y);if(l<1e-9)throw new ArgumentException("Вырожденное ребро обрамления.");return new Point2(p.X/l,p.Y/l);}
        private static Point3 Add(Point3 a,Point3 b)=>new Point3(a.X+b.X,a.Y+b.Y,a.Z+b.Z);
        private static Point3 Subtract(Point3 a,Point3 b)=>new Point3(a.X-b.X,a.Y-b.Y,a.Z-b.Z);
        private static Point3 Multiply(Point3 a,double s)=>new Point3(a.X*s,a.Y*s,a.Z*s);
        private static double Dot(Point3 a,Point3 b)=>a.X*b.X+a.Y*b.Y+a.Z*b.Z;
        private static Point3 Cross(Point3 a,Point3 b)=>new Point3(a.Y*b.Z-a.Z*b.Y,a.Z*b.X-a.X*b.Z,a.X*b.Y-a.Y*b.X);
    }
}
