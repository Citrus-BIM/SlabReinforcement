using SlabReinforcement.Core;
using Xunit;

public class BackgroundPlannerTests
{
    private static PolygonInput Rectangle(double x, double y, double w, double h) => new PolygonInput
    { Shell = new List<Point2> { new(x,y), new(x+w,y), new(x+w,y+h), new(x,y+h) } };

    private static BackgroundRequest Request() => new BackgroundRequest
    {
        Slabs = new List<PolygonInput> { Rectangle(0,0,4000,3000) },
        ThicknessMm = 200, TopCoverMm = 25, BottomCoverMm = 25, SideCoverMm = 25,
        Layers = new List<BackgroundLayer>
        {
            new() { Key="BX", TypeKey="12", Top=false, AlongX=true, DiameterMm=12, ModelDiameterMm=12, SpacingMm=200 },
            new() { Key="BY", TypeKey="16", Top=false, AlongX=false, DiameterMm=16, ModelDiameterMm=16, SpacingMm=200 },
            new() { Key="TX", TypeKey="12", Top=true, AlongX=true, DiameterMm=12, ModelDiameterMm=12, SpacingMm=200 },
            new() { Key="TY", TypeKey="16", Top=true, AlongX=false, DiameterMm=16, ModelDiameterMm=16, SpacingMm=200 }
        }
    };

    [Fact] public void FourLayersHavePhysicalCoverAndTouchingOrthogonalNets()
    {
        var plan=BackgroundPlanner.Build(Request());
        Assert.True(plan.CanCreate, string.Join(";", plan.Diagnostics.Select(d=>d.Message)));
        var z=plan.Bars.GroupBy(b=>b.SpecKey).ToDictionary(g=>g.Key,g=>g.First().Placement.ElevationMm);
        Assert.Equal(-169,z["BX"]); Assert.Equal(-155,z["BY"]);
        Assert.Equal(-31,z["TX"]); Assert.Equal(-45,z["TY"]);
        Assert.All(plan.Bars, b => Assert.InRange(b.Start.X, 31, 3969));
    }

    [Fact] public void OpeningSplitsRowsAndPreservesConcreteClearance()
    {
        var request=Request(); request.Layers=request.Layers.Take(1).ToList();
        request.Slabs[0].Holes.Add(Rectangle(1500,1000,1000,1000).Shell);
        var plan=BackgroundPlanner.Build(request);
        Assert.True(plan.CanCreate);
        var transverse=plan.Bars.Where(b=>b.Start.Y>969&&b.Start.Y<2031).Select(b=>b.Start.Y).OrderBy(y=>Math.Abs(y-1400)).First();
        var row=plan.Bars.Where(b=>Math.Abs(b.Start.Y-transverse)<1e-6).OrderBy(b=>b.Start.X).ToList();
        Assert.Equal(2,row.Count); Assert.Equal(1469,row[0].End.X,5); Assert.Equal(2531,row[1].Start.X,5);
        Assert.DoesNotContain(plan.Bars,b=>b.Start.Y>969 && b.Start.Y<2031 && b.Start.X<1500 && b.End.X>2500);
    }

    [Fact] public void ThinSlabRejectsInterpenetratingTopAndBottomNets()
    {
        var request=Request(); request.ThicknessMm=100;
        var plan=BackgroundPlanner.Build(request);
        Assert.False(plan.CanCreate); Assert.Contains(plan.Diagnostics,d=>d.Code=="BACKGROUND_LAYERS");
    }

    [Fact] public void NarrowSlabPlacesBothBoundaryRowsWhenTheyPhysicallyFit()
    {
        var request=Request(); request.Slabs=new List<PolygonInput>{Rectangle(0,0,4000,100)};
        request.Layers=request.Layers.Take(1).ToList();
        var plan=BackgroundPlanner.Build(request);
        Assert.True(plan.CanCreate); Assert.Equal(new[]{31.0,69.0},plan.Bars.Select(b=>b.Start.Y).OrderBy(y=>y));
    }

    [Fact] public void CountLimitAndCancellationAreEnforced()
    {
        var request=Request(); request.Slabs=new List<PolygonInput>{Rectangle(0,0,4e7,4e7)};
        Assert.Contains(BackgroundPlanner.Build(request).Diagnostics,d=>d.Code=="PLAN_TOO_LARGE");
        Assert.Throws<OperationCanceledException>(()=>BackgroundPlanner.Build(Request(),new CancellationToken(true)));
    }
}
