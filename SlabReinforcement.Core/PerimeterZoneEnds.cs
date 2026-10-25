using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace SlabReinforcement.Core
{
    // A bounded geometric connection to this operation's complete U edge.
    // No minimum normative lap or anchorage length is inferred here.
    internal sealed class PerimeterZoneEnds
    {
        private const double Epsilon=GeometryKernel.Epsilon;
        private readonly List<PerimeterUEdge> edges=new List<PerimeterUEdge>();
        private readonly PlanOptions options;
        private readonly PlanRequest request;
        private readonly CancellationToken token;
        private AutomaticZoneEndPlanner automatic;
        internal string LastAutomaticFailure { get; private set; }
        internal PerimeterZoneEnds(PlanRequest request,CancellationToken token=default)
        {
            this.request=request;
            this.token=token;
            options=request.Options;
            // Automatic endings belong to the zone's exact row and layer. A
            // nearby background U must not substitute for that local geometry.
            if(request.AutomaticZoneEnds!=null)return;
            var plan=request.PerimeterPlan;
            if(plan==null||!plan.CanCreate||plan.PerimeterUEdges==null)return;
            var bars=plan.Bars.GroupBy(b=>b.SpecKey??string.Empty).Where(g=>g.Count()==1).ToDictionary(g=>g.Key,g=>g.Single());
            foreach(var edge in plan.PerimeterUEdges)
            {
                if(edge==null||edge.StationsMm==null||edge.BarKeys==null||edge.StationsMm.Count==0
                    ||edge.StationsMm.Count!=edge.BarKeys.Count||edge.BarKeys.Distinct().Count()!=edge.BarKeys.Count)continue;
                double length=Distance(edge.Start,edge.End);
                if(new[]{length,edge.ModelDiameterMm,edge.BendRadiusMm,edge.LegLengthMm,edge.SpacingMm}.Any(x=>!GeometryKernel.Finite(x)||x<=0)
                    ||new[]{edge.Start.X,edge.Start.Y,edge.End.X,edge.End.Y,edge.Inward.X,edge.Inward.Y,edge.TopZ,edge.BottomZ,edge.SideCoverMm}.Any(x=>!GeometryKernel.Finite(x))
                    ||edge.LegLengthMm<=edge.BendRadiusMm||edge.TopZ<=edge.BottomZ||edge.SideCoverMm<0)continue;
                if(!GeometryKernel.Finite(edge.StationRangeStartMm)||!GeometryKernel.Finite(edge.StationRangeEndMm)
                    ||edge.StationRangeStartMm< -Epsilon||edge.StationRangeEndMm>length+Epsilon||edge.StationRangeEndMm<edge.StationRangeStartMm)continue;
                var expectedStations=edge.FollowsBackgroundRows?edge.StationsMm:
                    PerimeterPlanner.UStations(edge.StationRangeStartMm,edge.StationRangeEndMm,edge.ModelDiameterMm,edge.SpacingMm);
                if(expectedStations==null||edge.StationsMm.Count!=expectedStations.Count)continue;
                var along=new Point2((edge.End.X-edge.Start.X)/length,(edge.End.Y-edge.Start.Y)/length);
                if(edge.FollowsBackgroundRows)
                {
                    if(Math.Abs(edge.Inward.X*edge.Inward.X+edge.Inward.Y*edge.Inward.Y-1)>Epsilon
                        ||(edge.AlongX?Math.Abs(edge.Inward.Y):Math.Abs(edge.Inward.X))>Epsilon
                        ||-along.Y*edge.Inward.X+along.X*edge.Inward.Y<=Epsilon)continue;
                }
                else if(Math.Abs(edge.Inward.X+along.Y)>Epsilon||Math.Abs(edge.Inward.Y-along.X)>Epsilon)continue;
                bool complete=true;
                for(int i=0;i<edge.StationsMm.Count;i++)
                {
                    double station=expectedStations[i];
                    if(!GeometryKernel.Finite(edge.StationsMm[i])||Math.Abs(edge.StationsMm[i]-station)>Epsilon
                        ||station<edge.StationRangeStartMm-Epsilon||station>edge.StationRangeEndMm+Epsilon
                        ||i>0&&station<=expectedStations[i-1]+Epsilon
                        ||!bars.TryGetValue(edge.BarKeys[i],out var bar)||bar.Placement?.Component!="PerimeterU"
                        ||Math.Abs(bar.Placement.ModelDiameterMm-edge.ModelDiameterMm)>Epsilon||bar.Centerline?.Count!=5){complete=false;break;}
                    var web=new Point2(edge.Start.X+along.X*station,edge.Start.Y+along.Y*station);
                    var p=bar.Centerline;
                    if(Distance(new Point2(p[2].Start.X,p[2].Start.Y),web)>Epsilon
                        ||Math.Abs(p[0].Start.Z-edge.BottomZ)>Epsilon||Math.Abs(p[4].End.Z-edge.TopZ)>Epsilon
                        ||Distance(new Point2(p[0].Start.X,p[0].Start.Y),new Point2(web.X+edge.Inward.X*edge.LegLengthMm,web.Y+edge.Inward.Y*edge.LegLengthMm))>Epsilon
                        ||Distance(new Point2(p[4].End.X,p[4].End.Y),new Point2(web.X+edge.Inward.X*edge.LegLengthMm,web.Y+edge.Inward.Y*edge.LegLengthMm))>Epsilon)
                    {complete=false;break;}
                }
                if(complete)edges.Add(edge);
            }
        }

        internal bool TryConnect(double start,double end,double row,ReinforcementSpec spec,bool atStart,out PerimeterZoneEndConnection connection,string requiredEdge=null)
        {
            LastAutomaticFailure=null;
            if(request.AutomaticZoneEnds!=null)
            {
                if(automatic==null)automatic=new AutomaticZoneEndPlanner(request,token);
                if(automatic.TryConnect(start,end,row,spec,atStart,out connection,requiredEdge))return true;
                SetAutomaticFailure(atStart?start:end,row,
                    automatic.LastFailureReason??"не удалось проверить положение П-стержня у края плиты.");
                return false;
            }
            connection=null;
            if(!spec.ElevationMm.HasValue||!GeometryKernel.Finite(spec.ElevationMm.Value)||end-start<=Epsilon)return false;
            var a=Point(atStart?start:end,row);var b=Point(atStart?end:start,row);
            foreach(var edge in edges)
            {
                if(requiredEdge!=null&&edge.Id!=requiredEdge)continue;
                double clearance=(spec.EffectiveModelDiameterMm+edge.ModelDiameterMm)/2;
                if(edge.FollowsBackgroundRows)
                {
                    if(edge.AlongX!=options.AlongX||Math.Min(Math.Abs(spec.ElevationMm.Value-edge.BottomZ),
                        Math.Abs(spec.ElevationMm.Value-edge.TopZ))>clearance+Epsilon)continue;
                }
                else if(spec.ElevationMm.Value<edge.BottomZ+clearance-Epsilon||spec.ElevationMm.Value>edge.TopZ-clearance+Epsilon)continue;
                double length=Distance(edge.Start,edge.End);
                var along=new Point2((edge.End.X-edge.Start.X)/length,(edge.End.Y-edge.Start.Y)/length);
                if(!Coordinates(a,edge.Start,along,edge.Inward,out double s,out double n)
                    ||!Coordinates(b,edge.Start,along,edge.Inward,out double endS,out double endN))continue;
                double expected=options.EdgeCoverMm+spec.EffectiveModelDiameterMm/2-edge.SideCoverMm-edge.ModelDiameterMm/2;
                // A leg follows X/Y even at an oblique edge; cover is measured along the face normal.
                double normalProjection=-along.Y*edge.Inward.X+along.X*edge.Inward.Y;
                if(normalProjection<=Epsilon)continue;
                expected/=normalProjection;
                if(Math.Abs(n-expected)>Epsilon||s<edge.StationRangeStartMm-Epsilon||s>edge.StationRangeEndMm+Epsilon)continue;
                double ds=endS-s;
                double dn=endN-n;
                if(dn<=Epsilon)continue;
                double low=0,high=1;
                if(!Clip(s,ds,edge.StationRangeStartMm,edge.StationRangeEndMm,ref low,ref high)
                    ||!Clip(n,dn,edge.BendRadiusMm,edge.LegLengthMm,ref low,ref high))continue;
                double overlap=(high-low)*(end-start);
                if(overlap<=Epsilon)continue;
                connection=new PerimeterZoneEndConnection {AtStart=atStart,EdgeId=edge.Id,OverlapMm=overlap,
                    StationOffsetMm=edge.StationsMm.Min(station=>Math.Abs(station-s)),WebOffsetMm=n};
                return true;
            }
            return false;
        }
        internal void ResetFailure()=>LastAutomaticFailure=null;
        internal ReinforcementPlan Complete(IReadOnlyList<PlannedBar> finalZoneBars)=>automatic?.Complete(finalZoneBars);
        internal double GetRequiredEndSpan(ReinforcementSpec spec)
        {
            var settings=request.AutomaticZoneEnds;
            if(request.AutomaticZoneEndTypes?.Count>0)
            {
                if(spec?.TypeKey==null||!request.AutomaticZoneEndTypes.TryGetValue(spec.TypeKey,out settings))return 0;
            }
            return settings!=null&&settings.RequiredLapMm>0?settings.LegLengthMm:0;
        }
        internal void ReportInsufficientSpan(double endpoint,double row,double available,double required)
        {
            SetAutomaticFailure(endpoint,row,"для нахлёста нужно "
                +required.ToString("0.###",CultureInfo.CurrentCulture)+" мм, доступно "
                +available.ToString("0.###",CultureInfo.CurrentCulture)+" мм. Сокращать нахлёст нельзя.");
        }
        private void SetAutomaticFailure(double endpointValue,double row,string reason)
        {
            var endpoint=Point(endpointValue,row);
            LastAutomaticFailure="П-окончание зоны: X="
                +endpoint.X.ToString("0.###",CultureInfo.CurrentCulture)+", Y="
                +endpoint.Y.ToString("0.###",CultureInfo.CurrentCulture)+" мм: "+reason;
        }
        private Point2 Point(double value,double row)=>options.AlongX?new Point2(value,row):new Point2(row,value);
        private static bool Coordinates(Point2 point,Point2 origin,Point2 along,Point2 inward,out double s,out double n)
        {
            double determinant=along.X*inward.Y-along.Y*inward.X;
            s=n=0;if(Math.Abs(determinant)<=Epsilon)return false;
            double x=point.X-origin.X,y=point.Y-origin.Y;
            s=(x*inward.Y-y*inward.X)/determinant;
            n=(along.X*y-along.Y*x)/determinant;
            return true;
        }
        private static double Distance(Point2 a,Point2 b)=>Math.Sqrt((a.X-b.X)*(a.X-b.X)+(a.Y-b.Y)*(a.Y-b.Y));
        private static bool Clip(double value,double delta,double min,double max,ref double low,ref double high)
        {
            if(Math.Abs(delta)<=Epsilon)return value>=min-Epsilon&&value<=max+Epsilon;
            double a=(min-value)/delta,b=(max-value)/delta;
            low=Math.Max(low,Math.Min(a,b));high=Math.Min(high,Math.Max(a,b));return high>low;
        }
    }
}
