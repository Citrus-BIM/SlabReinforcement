using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;

namespace SlabReinforcement.Core
{
    public sealed class ExistingBarMatch
    {
        public int ExpectedIndex { get; internal set; }
        public string ExpectedSpecKey { get; internal set; }
        public int ActualIndex { get; internal set; }
        public string ActualSpecKey { get; internal set; }
    }

    public sealed class ExistingReinforcementMatchResult
    {
        public int MatchedCount => Matches.Count;
        public bool IsComplete { get; internal set; }
        public IReadOnlyList<ExistingBarMatch> Matches { get; internal set; }
    }

    public static class ExistingReinforcementMatcher
    {
        // SpecKey labels the expected demand, but uniquely identifies each actual
        // physical Revit position. Duplicate actual identities are ambiguous and excluded.
        // Segment order and direction are ignored; segmentation itself must agree.
        public static ExistingReinforcementMatchResult Match(IReadOnlyList<PlannedBar> expected,IReadOnlyList<PlannedBar> actual,
            double toleranceMm=0.1,CancellationToken cancellationToken=default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if(expected==null)throw new ArgumentNullException(nameof(expected));
            if(actual==null)throw new ArgumentNullException(nameof(actual));
            if(!GeometryKernel.Finite(toleranceMm)||toleranceMm<0)throw new ArgumentOutOfRangeException(nameof(toleranceMm));
            if(expected.Count>CompositePlanValidator.MaximumBars||actual.Count>CompositePlanValidator.MaximumBars)
                throw new ArgumentException("Слишком много стержней для сравнения с существующими. Уменьшите область.");
            var counts=new Dictionary<string,int>(StringComparer.Ordinal);
            foreach(var bar in actual)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if(string.IsNullOrWhiteSpace(bar?.SpecKey))continue;
                counts.TryGetValue(bar.SpecKey,out int count);counts[bar.SpecKey]=count+1;
            }
            var indices=new Dictionary<(string,double,int,int),STRtree<Path>>();
            for(int i=0;i<actual.Count;i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if(string.IsNullOrWhiteSpace(actual[i]?.SpecKey)||counts[actual[i].SpecKey]!=1)continue;
                var path=Read(actual[i],i,cancellationToken);if(path==null)continue;
                if(!indices.TryGetValue(path.Key,out var index))indices[path.Key]=index=new STRtree<Path>();
                index.Insert(new Envelope(path.X,path.X,path.Y,path.Y),path);
            }
            foreach(var index in indices.Values)index.Build();
            var candidates=new IReadOnlyList<int>[expected.Count];int comparisons=0;
            for(int i=0;i<expected.Count;i++)
            {
                cancellationToken.ThrowIfCancellationRequested();var matches=new List<int>();candidates[i]=matches;
                var path=Read(expected[i],i,cancellationToken);
                if(path==null||!indices.TryGetValue(path.Key,out var index))continue;
                foreach(var other in index.Query(new Envelope(path.X-toleranceMm,path.X+toleranceMm,path.Y-toleranceMm,path.Y+toleranceMm)).OrderBy(p=>p.Index))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if(++comparisons>CompositePlanValidator.MaximumCandidatePairs)throw new ArgumentException("Не удалось сравнить близкие стержни с существующими. Уменьшите область.");
                    if(Math.Abs(path.Z-other.Z)>toleranceMm)continue;
                    if(Equivalent(path.Segments,other.Segments,toleranceMm,cancellationToken,ref comparisons))matches.Add(other.Index);
                }
            }
            var assignment=Assign(candidates,actual.Count,cancellationToken);
            var result=new List<ExistingBarMatch>();
            for(int i=0;i<assignment.Length;i++)if(assignment[i]>=0)result.Add(new ExistingBarMatch
            {ExpectedIndex=i,ExpectedSpecKey=expected[i].SpecKey,ActualIndex=assignment[i],ActualSpecKey=actual[assignment[i]].SpecKey});
            return new ExistingReinforcementMatchResult {IsComplete=result.Count==expected.Count,Matches=result.AsReadOnly()};
        }

        private static bool Equivalent(List<CenterlineSegment> a,List<CenterlineSegment> b,double tolerance,CancellationToken token,ref int comparisons)
        {
            var candidates=new IReadOnlyList<int>[a.Count];
            for(int i=0;i<a.Count;i++)
            {
                token.ThrowIfCancellationRequested();var list=new List<int>();candidates[i]=list;
                for(int j=0;j<b.Count;j++)
                {
                    if(++comparisons>CompositePlanValidator.MaximumCandidatePairs)throw new ArgumentException("Слишком сложные стержни для сравнения с существующими. Уменьшите область.");
                    if(a[i].Midpoint.HasValue!=b[j].Midpoint.HasValue)continue;
                    if(a[i].Midpoint.HasValue&&!Near(a[i].Midpoint.Value,b[j].Midpoint.Value,tolerance))continue;
                    if(Near(a[i].Start,b[j].Start,tolerance)&&Near(a[i].End,b[j].End,tolerance)
                        ||Near(a[i].Start,b[j].End,tolerance)&&Near(a[i].End,b[j].Start,tolerance))list.Add(j);
                }
                if(list.Count==0)return false;
            }
            return Assign(candidates,b.Count,token).All(i=>i>=0);
        }

        // Iterative augmenting paths avoid greedy false negatives and recursion depth.
        private static int[] Assign(IReadOnlyList<int>[] candidates,int actualCount,CancellationToken token)
        {
            var assigned=Enumerable.Repeat(-1,candidates.Length).ToArray();var owner=Enumerable.Repeat(-1,actualCount).ToArray();
            var seenActual=new int[actualCount];var seenExpected=new int[candidates.Length];var parent=new int[actualCount];
            var queue=new Queue<int>();
            for(int root=0;root<candidates.Length;root++)
            {
                token.ThrowIfCancellationRequested();int visit=root+1;queue.Clear();queue.Enqueue(root);seenExpected[root]=visit;
                int free=-1;
                while(queue.Count>0&&free<0)
                {
                    token.ThrowIfCancellationRequested();int e=queue.Dequeue();
                    foreach(int a in candidates[e])
                    {
                        if(seenActual[a]==visit)continue;seenActual[a]=visit;parent[a]=e;
                        if(owner[a]<0){free=a;break;}
                        if(seenExpected[owner[a]]!=visit){seenExpected[owner[a]]=visit;queue.Enqueue(owner[a]);}
                    }
                }
                while(free>=0){int e=parent[free],previous=assigned[e];assigned[e]=free;owner[free]=e;free=previous;}
            }
            return assigned;
        }

        private static Path Read(PlannedBar bar,int index,CancellationToken token)
        {
            if(bar?.Placement==null||string.IsNullOrWhiteSpace(bar.Placement.TypeKey)
                ||!GeometryKernel.Finite(bar.Placement.ModelDiameterMm)||bar.Placement.ModelDiameterMm<=0
                ||!GeometryKernel.Finite(bar.Placement.ElevationMm))return null;
            var segments=bar.Centerline??new List<CenterlineSegment>{new CenterlineSegment
                {Start=new Point3(bar.Start.X,bar.Start.Y,bar.Placement.ElevationMm),End=new Point3(bar.End.X,bar.End.Y,bar.Placement.ElevationMm)}};
            if(segments.Count==0||segments.Count>CompositePlanValidator.MaximumPieces)return null;
            var nodes=new List<Node>();var buckets=new Dictionary<(long,long,long),List<Node>>();
            var unique=new HashSet<(int,int,Point3?)>();var points=new List<Point3>();
            foreach(var segment in segments)
            {
                token.ThrowIfCancellationRequested();
                if(segment==null||!Valid(segment.Start)||!Valid(segment.End)||Near(segment.Start,segment.End,GeometryKernel.Epsilon))return null;
                if(segment.Midpoint.HasValue)
                {
                    var m=segment.Midpoint.Value;if(!Valid(m))return null;
                    var a=Minus(m,segment.Start);var b=Minus(segment.End,segment.Start);var cross=Cross(a,b);
                    double crossSquared=Squared(cross),aSquared=Squared(a),bSquared=Squared(b);
                    if(aSquared==0||crossSquared<=aSquared*bSquared*1e-20)return null;
                    double radius=Math.Sqrt(aSquared*bSquared*Squared(Minus(segment.End,m))/crossSquared)/2;
                    if(!GeometryKernel.Finite(radius)||radius<=bar.Placement.ModelDiameterMm/2)return null;
                    points.Add(m);
                }
                int start=NodeFor(segment.Start,nodes,buckets),end=NodeFor(segment.End,nodes,buckets);
                if(start<0||end<0||start==end||!unique.Add((Math.Min(start,end),Math.Max(start,end),segment.Midpoint)))return null;
                nodes[start].Neighbours.Add(end);nodes[end].Neighbours.Add(start);
                if(nodes[start].Neighbours.Count>2||nodes[end].Neighbours.Count>2)return null;
                points.Add(segment.Start);points.Add(segment.End);
            }
            int ends=nodes.Count(n=>n.Neighbours.Count==1);if(ends!=0&&ends!=2)return null;
            var visited=new HashSet<int>();var queue=new Queue<int>();queue.Enqueue(0);
            while(queue.Count>0){int node=queue.Dequeue();if(!visited.Add(node))continue;foreach(int next in nodes[node].Neighbours)queue.Enqueue(next);}
            if(visited.Count!=nodes.Count)return null;
            return new Path {Index=index,Segments=segments,Key=(bar.Placement.TypeKey,bar.Placement.ModelDiameterMm,segments.Count,segments.Count(s=>s.Midpoint.HasValue)),
                X=(points.Min(p=>p.X)+points.Max(p=>p.X))/2,Y=(points.Min(p=>p.Y)+points.Max(p=>p.Y))/2,Z=(points.Min(p=>p.Z)+points.Max(p=>p.Z))/2};
        }

        // Junction tolerance validates one path only; it NEVER enlarges the requested
        // expected/actual comparison tolerance (all endpoint/midpoint distances use it exactly).
        private static int NodeFor(Point3 point,List<Node> nodes,Dictionary<(long,long,long),List<Node>> buckets)
        {
            const double junction=GeometryKernel.Epsilon;
            long x=(long)Math.Floor(point.X/junction),y=(long)Math.Floor(point.Y/junction),z=(long)Math.Floor(point.Z/junction);
            Node found=null;
            for(int dx=-1;dx<=1;dx++)for(int dy=-1;dy<=1;dy++)for(int dz=-1;dz<=1;dz++)
                if(buckets.TryGetValue((x+dx,y+dy,z+dz),out var nearby))foreach(var node in nearby)
                    if(Near(point,node.Point,junction)){if(found!=null&&found!=node)return -1;found=node;}
            if(found!=null)return found.Id;
            var created=new Node {Id=nodes.Count,Point=point};nodes.Add(created);
            if(!buckets.TryGetValue((x,y,z),out var list))buckets[(x,y,z)]=list=new List<Node>();list.Add(created);return created.Id;
        }
        private static bool Valid(Point3 p)=>GeometryKernel.Finite(p.X)&&GeometryKernel.Finite(p.Y)&&GeometryKernel.Finite(p.Z)
            &&Math.Abs(p.X)<=1e9&&Math.Abs(p.Y)<=1e9&&Math.Abs(p.Z)<=1e9;
        private static bool Near(Point3 a,Point3 b,double tolerance)=>Squared(Minus(a,b))<=tolerance*tolerance;
        private static Point3 Minus(Point3 a,Point3 b)=>new Point3(a.X-b.X,a.Y-b.Y,a.Z-b.Z);
        private static Point3 Cross(Point3 a,Point3 b)=>new Point3(a.Y*b.Z-a.Z*b.Y,a.Z*b.X-a.X*b.Z,a.X*b.Y-a.Y*b.X);
        private static double Squared(Point3 p)=>p.X*p.X+p.Y*p.Y+p.Z*p.Z;
        private sealed class Node {public int Id;public Point3 Point;public List<int> Neighbours=new List<int>();}
        private sealed class Path {public int Index;public (string,double,int,int) Key;public List<CenterlineSegment> Segments;public double X,Y,Z;}
    }
}
