using System.Reflection;
using System.Runtime.CompilerServices;
using SlabReinforcement;
using SlabReinforcement.Core;
using SlabReinforcement.Integration;
using Xunit;

public class ReinforcementSessionTests
{
    [Fact]
    public void EnablingPerimeterDoesNotChangeAnyBackgroundCoordinate()
    {
        var plain = Calculate(Parameters(false));
        var framed = Calculate(Parameters(true));
        Assert.True(plain.CanCreate, Messages(plain));
        Assert.True(framed.CanCreate, Messages(framed));
        Assert.Equal(BackgroundGeometry(plain), BackgroundGeometry(framed));
        Assert.Contains(framed.Bars, b => b.Placement.Component == "PerimeterU");
        var z = plain.Bars.GroupBy(b => b.SpecKey).ToDictionary(g => g.Key, g => g.First().Placement.ElevationMm);
        Assert.Equal(-159, z["Background|Низ X"]);
        Assert.Equal(-145, z["Background|Низ Y"]);
        Assert.Equal(-41, z["Background|Верх X"]);
        Assert.Equal(-55, z["Background|Верх Y"]);
    }

    [Fact]
    public void SeparatelyCreatedYUsesTheSameElevationAsAllFourLayers()
    {
        var all = Calculate(Parameters(false));
        var onlyY = Parameters(false);
        foreach (var layer in onlyY.BackgroundLayers) layer.Enabled = layer.Direction.EndsWith("Y");
        var separate = Calculate(onlyY);
        Assert.True(separate.CanCreate, Messages(separate));
        foreach (var bar in separate.Bars)
            Assert.Equal(all.Bars.First(b => b.SpecKey == bar.SpecKey).Placement.ElevationMm, bar.Placement.ElevationMm);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PerimeterWithoutLowerBackgroundUsesManualSpacing(bool upperBackground)
    {
        var parameters = Parameters(true);
        foreach (var layer in parameters.BackgroundLayers) layer.Enabled = upperBackground && layer.Direction.StartsWith("Верх");
        parameters.Perimeter.SpacingMm = 350;
        var plan = Calculate(parameters);
        Assert.True(plan.CanCreate, Messages(plan));
        Assert.Equal(4, plan.PerimeterUEdges.Count);
        Assert.All(plan.PerimeterUEdges, edge => { Assert.False(edge.FollowsBackgroundRows); Assert.Equal(350, edge.SpacingMm); });
        Assert.Contains(plan.Bars, b => b.Placement.Component == "PerimeterU");
    }

    [Theory]
    [InlineData("Низ X", false)]
    [InlineData("Низ Y", false)]
    [InlineData("Низ X", true)]
    [InlineData("Низ Y", true)]
    public void SingleLowerDirectionKeepsItsRowsAndCompletesOtherEdges(string direction, bool existingBackground)
    {
        var parameters = Parameters(false);
        foreach (var layer in parameters.BackgroundLayers) layer.Enabled = layer.Direction == direction;
        var background = Calculate(parameters);
        Assert.True(background.CanCreate, Messages(background));
        var existing = existingBackground ? AsExisting(background.Bars, false) : null;
        if (existingBackground) foreach (var layer in parameters.BackgroundLayers) layer.Enabled = false;
        parameters.Perimeter.Enabled = true;
        parameters.Perimeter.SpacingMm = 350;
        var plan = Calculate(parameters, existing);
        Assert.True(plan.CanCreate, Messages(plan));
        Assert.Equal(2, plan.PerimeterUEdges.Count(e => e.FollowsBackgroundRows));
        Assert.Equal(2, plan.PerimeterUEdges.Count(e => !e.FollowsBackgroundRows));
        Assert.All(plan.PerimeterUEdges.Where(e => !e.FollowsBackgroundRows), edge => Assert.Equal(350, edge.SpacingMm));
        if (existingBackground) Assert.Equal(background.Bars.Count, plan.ReferenceBars.Count);
        else Assert.Equal(BackgroundGeometry(background), BackgroundGeometry(plan));
    }

    [Fact]
    public void CompleteLowerBackgroundIgnoresInvalidUnusedManualSpacing()
    {
        var parameters = Parameters(true);
        parameters.Perimeter.SpacingMm = double.NaN;
        var plan = Calculate(parameters);
        Assert.True(plan.CanCreate, Messages(plan));
        Assert.All(plan.PerimeterUEdges, e => Assert.True(e.FollowsBackgroundRows));
    }

    [Fact]
    public void ExistingBackgroundFeedsMeasuredCoordinatesToPerimeter()
    {
        var original = Calculate(Parameters(false));
        Assert.True(original.CanCreate, Messages(original));
        var existing = AsExisting(original.Bars, measuredShift: true);
        var parameters = Parameters(true);
        foreach (var layer in parameters.BackgroundLayers) layer.Enabled = false;
        var plan = Calculate(parameters, existing);
        Assert.True(plan.CanCreate, Messages(plan));
        Assert.Equal(existing.Count, plan.ReferenceBars.Count);
        Assert.All(plan.Bars, b => Assert.Equal("PerimeterU", b.Placement.Component));
        Assert.Equal(existing.Select(b => (b.Start.X, b.Start.Y, b.End.X, b.End.Y, b.Placement.ElevationMm)).OrderBy(p => p),
            plan.ReferenceBars.Select(b => (b.Start.X, b.Start.Y, b.End.X, b.End.Y, b.Placement.ElevationMm)).OrderBy(p => p));
        var lowerHeights = existing.Where(b => b.Placement.ElevationMm < -100).Select(b => b.Placement.ElevationMm).Distinct();
        foreach (double z in lowerHeights) Assert.Contains(plan.PerimeterUEdges, e => Math.Abs(e.BottomZ - z) < 1e-8);
    }

    [Fact]
    public void OldUWithoutRequestedPerimeterDoesNotBlockAnUpperOnlyOperation()
    {
        var framed = Calculate(Parameters(true));
        Assert.True(framed.CanCreate, Messages(framed));
        var existingU = AsExisting(framed.Bars.Where(b => b.Placement.Component == "PerimeterU"), false);
        var parameters = Parameters(false);
        foreach (var layer in parameters.BackgroundLayers) layer.Enabled = layer.Direction == "Верх Y";
        var plan = Calculate(parameters, existingU);
        Assert.True(plan.CanCreate, Messages(plan));
        Assert.All(plan.Bars, b => Assert.Equal("Background|Верх Y", b.SpecKey));
    }

    [Theory]
    [InlineData("Низ X", 0, -159)]
    [InlineData("Низ Y", 0, -147)]
    [InlineData("Верх Y", 0, -53)]
    public void SeparateZoneWithoutBackgroundUsesConfiguredXAndItsOwnDiameter(string direction, double offset, double expectedZ)
    {
        var parameters = ZoneParameters(direction); parameters.LayerOffsetMm = offset;
        var plan = Calculate(parameters);
        Assert.True(plan.CanCreate, Messages(plan));
        Assert.All(plan.Bars, bar => { Assert.Equal("Zones", bar.Placement.Component); Assert.Equal(expectedZ, bar.Placement.ElevationMm, 6); });
    }

    [Theory]
    [InlineData("Низ X")]
    [InlineData("Низ Y")]
    [InlineData("Верх X")]
    [InlineData("Верх Y")]
    public void LegacyUserOffsetCannotMoveZoneAwayFromTheSharedLayer(string direction)
    {
        var baseline = Calculate(ZoneParameters(direction));
        var changed = ZoneParameters(direction);
        changed.LayerOffsetMm = 23;
        var actual = Calculate(changed);
        Assert.True(baseline.CanCreate, Messages(baseline));
        Assert.True(actual.CanCreate, Messages(actual));
        Assert.Equal(baseline.Bars.Select(b => b.Placement.ElevationMm), actual.Bars.Select(b => b.Placement.ElevationMm));
    }

    [Fact]
    public void SeparateYZoneNeedsTheConfiguredOuterXDescriptor()
    {
        var parameters = ZoneParameters("Низ Y");
        parameters.BackgroundLayers.RemoveAll(l => l.Direction == "Низ X");
        var plan = Calculate(parameters);
        Assert.False(plan.CanCreate);
        Assert.Contains(plan.Diagnostics, d => d.Code == "SPECIFICATION" && d.Message.Contains("X"));
    }

    [Theory]
    [InlineData("Низ Y", -144.98)]
    [InlineData("Верх Y", -55.02)]
    public void ActualYBackgroundElevationStillTakesPrecedenceOverVirtualZonePlacement(string direction, double expectedZ)
    {
        var original = Calculate(Parameters(false));
        var existing = AsExisting(original.Bars, measuredShift: true);
        var plan = Calculate(ZoneParameters(direction), existing);
        Assert.True(plan.CanCreate, Messages(plan));
        Assert.All(plan.Bars, bar => { Assert.Equal("Zones", bar.Placement.Component); Assert.Equal(expectedZ, bar.Placement.ElevationMm, 6); });
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void SkippedSingleRowZonesDoNotBlockOtherNewComponents(bool createBackground, bool createPerimeter)
    {
        var parameters = ZoneParameters("Низ X");
        foreach (var layer in parameters.BackgroundLayers) layer.Enabled = createBackground;
        parameters.Perimeter.Enabled = createPerimeter;
        var existing = createBackground ? null : AsExisting(Calculate(Parameters(false)).Bars, false);
        parameters.IncludeZones = false;
        var withoutZones = Calculate(parameters, existing);
        parameters.IncludeZones = true;
        var plan = Calculate(parameters, existing, SingleRowZoneFaces());

        Assert.True(withoutZones.CanCreate, Messages(withoutZones));
        Assert.Contains(plan.Diagnostics, d => d.Code == "SINGLE_ROW_ZONES_SKIPPED" && !d.IsBlocking);
        Assert.True(plan.CanCreate, Messages(plan));
        Assert.DoesNotContain(plan.Diagnostics, d => d.Code == "NO_BARS" && d.IsBlocking);
        Assert.DoesNotContain(plan.Bars, b => b.Placement.Component == "Zones");
        Assert.DoesNotContain(plan.Groups, g => g.Placement.Component == "Zones");
        Assert.Equal(withoutZones.Bars.Count, plan.Bars.Count);
        Assert.Equal(withoutZones.Groups.Count, plan.Groups.Count);
        Assert.Equal(BackgroundGeometry(withoutZones), BackgroundGeometry(plan));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SkippedSingleRowZonesStillBlockAnOperationWithNoNewComponents(bool existingBackground)
    {
        var existing = existingBackground ? AsExisting(Calculate(Parameters(false)).Bars, false) : null;
        var plan = Calculate(ZoneParameters("Низ X"), existing, SingleRowZoneFaces());

        Assert.False(plan.CanCreate);
        Assert.Empty(plan.Bars);
        Assert.Contains(plan.Diagnostics, d => d.Code == "SINGLE_ROW_ZONES_SKIPPED" && !d.IsBlocking);
        Assert.Contains(plan.Diagnostics, d => d.Code == "NO_BARS" && d.IsBlocking);
    }

    [Fact]
    public void SkippedSingleRowZonesDoNotSuppressOtherZoneFailures()
    {
        var parameters = ZoneParameters("Низ X");
        foreach (var layer in parameters.BackgroundLayers) layer.Enabled = true;
        var faces = SingleRowZoneFaces();
        faces.Add(new() { Id = "outside-cover", ColorKey = "red",
            Vertices = new() { new(1200, 10), new(2400, 10), new(2400, 20), new(1200, 20) } });
        var plan = Calculate(parameters, zoneFaces: faces);

        Assert.Contains(plan.Bars, b => b.Placement.Component == "Background");
        Assert.Contains(plan.Diagnostics, d => d.Code == "SINGLE_ROW_ZONES_SKIPPED" && !d.IsBlocking);
        Assert.Contains(plan.Diagnostics, d => d.Code == "DEMAND_OUTSIDE_COVER" && d.IsBlocking);
        Assert.False(plan.CanCreate);
    }

    private static List<FaceInput> SingleRowZoneFaces() => new() { new() { Id = "single-row", ColorKey = "red",
        Vertices = new() { new(1200, 980), new(2400, 980), new(2400, 1120), new(1200, 1120) } } };

    [Theory]
    [InlineData("Низ X")]
    [InlineData("Верх X")]
    [InlineData("Низ Y")]
    [InlineData("Верх Y")]
    public void LocalZoneEndsAreCreatedWithoutEnablingMainReinforcement(string direction)
    {
        var p = ZoneParameters(direction);
        p.UsePerimeterUForZoneEnds = true; p.ZoneEndLegLengthMm = 500;
        p.Perimeter.SpecKey = null; p.Perimeter.SpacingMm = double.NaN;
        bool alongX = direction.EndsWith("X");
        var vertices = alongX ? new List<Point2> { new(3300,900),new(3990,900),new(3990,2100),new(3300,2100) }
            : new List<Point2> { new(900,2300),new(2100,2300),new(2100,2990),new(900,2990) };
        var plan = Calculate(p, zoneFaces: new() { new() { Id="edge-zone",ColorKey="red",Vertices=vertices } });
        Assert.True(plan.CanCreate, Messages(plan));
        Assert.DoesNotContain(plan.Bars, b => b.Placement.Component == "Background");
        var zones = plan.Bars.Where(b => b.Placement.Component == "Zones").ToList();
        var endings = plan.Bars.Where(b => b.Placement.Component == "PerimeterU").ToList();
        Assert.NotEmpty(zones); Assert.Equal(zones.Count, endings.Count);
        Assert.All(endings, b => Assert.Equal("b12", b.Placement.TypeKey));
        Assert.All(zones, b => Assert.False(Assert.Single(b.EndConnections).AtStart));
        Assert.All(endings, b => Assert.InRange(alongX ? b.Centerline[0].Start.Y : b.Centerline[0].Start.X,900,2100));
        Assert.False(p.Perimeter.Enabled);
        Assert.All(p.BackgroundLayers, l => Assert.False(l.Enabled));
    }

    [Fact]
    public void InteriorZonesDoNotCreateAutomaticEnds()
    {
        var p = ZoneParameters("Низ X"); p.UsePerimeterUForZoneEnds=true;
        p.Perimeter.SpecKey = null;
        var plan = Calculate(p);
        Assert.True(plan.CanCreate, Messages(plan));
        Assert.All(plan.Bars, b => Assert.Equal("Zones", b.Placement.Component));
        Assert.All(plan.Bars, b => Assert.Empty(b.EndConnections));
    }

    [Theory]
    [InlineData("B20",1190,1590)]
    [InlineData("B25",1030,1370)]
    public void AutomaticZoneLegsUseEachZoneTypeAndSelectedConcrete(string concrete, double leg12, double leg16)
    {
        var p = ZoneParameters("Низ X"); p.UsePerimeterUForZoneEnds = true; p.AutomaticULegLength = true;
        p.ConcreteClass=concrete; p.ZoneEndLegLengthMm=double.NaN; p.Perimeter.SpecKey=null;
        p.Selections.Add(new() { ColorKey="blue",SpecKey="b16",SpacingMm=150 });
        var faces = new List<FaceInput> {
            new() {Id="red",ColorKey="red",Vertices=new(){new(3800,500),new(3995,500),new(3995,1100),new(3800,1100)}},
            new() {Id="blue",ColorKey="blue",Vertices=new(){new(3800,1700),new(3995,1700),new(3995,2300),new(3800,2300)}} };
        var plan=Calculate(p,zoneFaces:faces);
        Assert.True(plan.CanCreate,Messages(plan));
        var ends=plan.Bars.Where(b=>b.Placement.Component=="PerimeterU").ToList();
        Assert.All(plan.Bars,b=>Assert.Equal(PreviewPurpose.Zones,b.PreviewPurpose));
        Assert.All(plan.Groups,g=>Assert.Equal(PreviewPurpose.Zones,g.PreviewPurpose));
        Assert.Equal(new[]{"b12","b16"},ends.Select(b=>b.Placement.TypeKey).Distinct().OrderBy(x=>x));
        foreach(var b in ends) Assert.Equal(b.Placement.TypeKey=="b12"?leg12:leg16,Math.Abs(b.Centerline[0].Start.X-b.Centerline[2].Start.X),5);
        Assert.All(plan.Bars.Where(b=>b.Placement.Component=="Zones"), b =>
            Assert.True(Assert.Single(b.EndConnections).OverlapMm >= (b.Placement.TypeKey=="b12"?leg12-30:leg16-40)-1e-6));
    }

    [Theory]
    [InlineData("B20",800)]
    [InlineData("B25",690)]
    public void MainPerimeterAlsoDerivesLegLengthFromConcrete(string concrete,double expected)
    {
        var p=Parameters(true); p.AutomaticULegLength=true; p.ConcreteClass=concrete; p.Perimeter.LegLengthMm=double.NaN;
        var plan=Calculate(p);
        Assert.True(plan.CanCreate,Messages(plan));
        Assert.All(plan.Bars.Where(b=>b.Placement.Component=="PerimeterU"), b => {
            var a=b.Centerline[0].Start;var web=b.Centerline[2].Start;
            Assert.Equal(expected,Math.Sqrt(Math.Pow(a.X-web.X,2)+Math.Pow(a.Y-web.Y,2)),5);
        });
        Assert.Equal(BackgroundGeometry(Calculate(Parameters(false))),BackgroundGeometry(plan));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AutomaticZoneEndsReuseFullOrPartialExistingMatches(bool partial)
    {
        var p=ZoneParameters("Низ X");p.UsePerimeterUForZoneEnds=true;
        var faces=new List<FaceInput>{new(){Id="edge",ColorKey="red",Vertices=new(){new(3300,900),new(3990,900),new(3990,2100),new(3300,2100)}}};
        var first=Calculate(p,zoneFaces:faces);
        Assert.True(first.CanCreate,Messages(first));
        var allEnds=first.Bars.Where(b=>b.Placement.Component=="PerimeterU").ToList();
        var existing=AsExisting(allEnds.Where((b,i)=>!partial||i%2==0),false);
        var next=Calculate(p,existing,faces);
        Assert.True(next.CanCreate,Messages(next));
        Assert.Equal(existing.Count,next.ReferenceBars.Count(b=>b.Placement.Component=="PerimeterU"));
        Assert.Equal(allEnds.Count-existing.Count,next.Bars.Count(b=>b.Placement.Component=="PerimeterU"));
        Assert.Contains(next.Diagnostics,d=>d.Code=="ZONE_ENDS_REUSED");
        Assert.All(next.Bars.Concat(next.ReferenceBars),b=>Assert.Equal(PreviewPurpose.Zones,b.PreviewPurpose));
        Assert.All(next.Groups,g=>Assert.Equal(PreviewPurpose.Zones,g.PreviewPurpose));
    }

    [Fact]
    public void NewBackgroundAndIndependentLocalEndsKeepSeparatePreviewPurposes()
    {
        var p=SharedPreviewParameters();p.Perimeter.SpecKey="u8";
        var plan=Calculate(p,zoneFaces:SharedPreviewFaces());
        Assert.True(plan.CanCreate,Messages(plan));
        Assert.Contains(plan.Bars,b=>b.Placement.Component=="Background");
        Assert.Contains(plan.Bars,b=>b.SpecKey.StartsWith("AutoZoneUBar|"));
        foreach(var bar in plan.Bars)
            Assert.Equal(bar.Placement.Component=="Zones"||bar.SpecKey.StartsWith("AutoZoneUBar|")
                ?PreviewPurpose.Zones:PreviewPurpose.Background,bar.PreviewPurpose);
        foreach(var group in plan.Groups)
            Assert.Equal(group.Placement.Component=="Zones"||group.SpecKey.StartsWith("AutoZoneUBar|")
                ?PreviewPurpose.Zones:PreviewPurpose.Background,group.PreviewPurpose);
    }

    [Fact]
    public void ExactNewGlobalUReuseMarksOnlyThatPhysicalBarForBothPreviews()
    {
        var plan=Calculate(SharedPreviewParameters(),zoneFaces:SharedPreviewFaces());
        Assert.True(plan.CanCreate,Messages(plan));
        var sharedKey=Assert.Single(plan.PerimeterUEdges.Where(e=>e.Id.StartsWith("AutoZoneU|"))
            .SelectMany(e=>e.BarKeys),k=>!k.StartsWith("AutoZoneUBar|"));
        var shared=Assert.Single(plan.Bars,b=>b.SpecKey==sharedKey);
        Assert.Equal(1500,shared.Centerline[2].Start.Y,6);
        Assert.Equal(PreviewPurpose.Background|PreviewPurpose.Zones,shared.PreviewPurpose);
        Assert.All(plan.Bars.Where(b=>b.Placement.Component=="PerimeterU"&&b.SpecKey!=sharedKey),b=>
            Assert.Equal(b.SpecKey.StartsWith("AutoZoneUBar|")?PreviewPurpose.Zones:PreviewPurpose.Background,b.PreviewPurpose));
        // Sharing one member must not make the entire physical perimeter array a zone array.
        Assert.All(plan.Groups.Where(g=>g.Placement.Component=="PerimeterU"&&!g.SpecKey.StartsWith("AutoZoneUBar|")),
            g=>Assert.Equal(PreviewPurpose.Background,g.PreviewPurpose));
        Assert.Contains(plan.Diagnostics,d=>d.Code=="ZONE_ENDS_REUSED");
    }

    [Fact]
    public void ReusedGlobalAndLocalUReferencesKeepTheirOwnPreviewMembership()
    {
        var p=SharedPreviewParameters();p.IncludeZones=false;
        var main=Calculate(p);
        Assert.True(main.CanCreate,Messages(main));
        var existing=AsExisting(main.Bars,false);
        p.IncludeZones=true;
        var plan=Calculate(p,existing,SharedPreviewFaces());
        Assert.True(plan.CanCreate,Messages(plan));
        Assert.All(plan.ReferenceBars,b=>Assert.True((b.PreviewPurpose & PreviewPurpose.Background) != 0));
        var local=Assert.Single(plan.ReferenceBars,b=>(b.PreviewPurpose & PreviewPurpose.Zones) != 0);
        Assert.Equal(PreviewPurpose.Background | PreviewPurpose.Zones,local.PreviewPurpose);
        Assert.Equal(main.Bars.Count,plan.ReferenceBars.Count);
        Assert.True(ExistingReinforcementMatcher.Match(plan.ReferenceBars,existing).IsComplete);
        Assert.Equal(1500,local.Centerline[2].Start.Y,6);
        Assert.All(plan.Bars,b=>Assert.Equal(PreviewPurpose.Zones,b.PreviewPurpose));
    }

    private static PreviewParameters SharedPreviewParameters()
    {
        var p=ZoneParameters("Низ X");
        foreach(var layer in p.BackgroundLayers)layer.Enabled=true;
        p.BackgroundLayers.Single(l=>l.Direction=="Низ X").SpacingMm=250;
        p.Selections[0].SpacingMm=275;
        p.UsePerimeterUForZoneEnds=true;p.ZoneEndLegLengthMm=500;p.AutomaticULegLength=false;
        p.Perimeter.Enabled=true;p.Perimeter.SpecKey="b12";p.Perimeter.LegLengthMm=500;
        return p;
    }

    [Fact]
    public void BackgroundCreationScopeIgnoresInvalidInactiveZoneInputs()
    {
        var p=Parameters(true);p.CreationScope=ReinforcementCreationScope.Background;
        p.IncludeZones=true;p.SourceGroupKey="missing";p.Direction=null;p.CalculationAxesConfirmed=false;
        p.DemandMeaning="invalid";p.MergeToleranceMm=double.NaN;p.RoundIncrementMm=0;p.Selections=new(){null};
        var plan=Calculate(p);
        Assert.True(plan.CanCreate,Messages(plan));
        Assert.All(plan.Bars,b=>Assert.Equal(PreviewPurpose.Background,b.PreviewPurpose));
        Assert.DoesNotContain(plan.Groups,g=>g.Placement.Component=="Zones");
        Assert.Empty(plan.DemandRegions);
    }

    [Theory]
    [InlineData("Низ X",-159)]
    [InlineData("Низ Y",-147)]
    [InlineData("Верх Y",-53)]
    public void ZoneCreationScopeIgnoresEnabledInactiveFieldsButPreservesOuterXType(string direction,double expectedZ)
    {
        var p=ZoneParameters(direction);p.CreationScope=ReinforcementCreationScope.Zones;p.IncludeZones=false;
        foreach(var layer in p.BackgroundLayers){layer.Enabled=true;layer.SpacingMm=double.NaN;}
        p.BackgroundLayers.Single(l=>l.Direction==(direction.StartsWith("Верх")?"Низ Y":"Верх Y")).SpecKey="missing";
        p.Perimeter.Enabled=true;p.Perimeter.SpecKey="missing";p.Perimeter.SpacingMm=double.NaN;p.Perimeter.LegLengthMm=double.NaN;
        var plan=Calculate(p);
        Assert.True(plan.CanCreate,Messages(plan));
        Assert.All(plan.Bars,b=>{Assert.Equal("Zones",b.Placement.Component);Assert.Equal(expectedZ,b.Placement.ElevationMm,6);});
        Assert.All(plan.Groups,g=>Assert.Equal(PreviewPurpose.Zones,g.PreviewPurpose));
        Assert.Empty(plan.ReferenceBars);
    }

    [Fact]
    public void ZoneCreationScopeNeverCreditsEnabledButUnbuiltBackground()
    {
        var p=ZoneParameters("Низ X");p.CreationScope=ReinforcementCreationScope.Zones;p.DemandMeaning="TotalArea";
        foreach(var layer in p.BackgroundLayers)layer.Enabled=true;
        p.Selections[0].RequiredAreaMm2PerM=1000;
        var plan=Calculate(p);
        Assert.False(plan.CanCreate);
        Assert.Empty(plan.Bars);
        Assert.Contains(plan.Diagnostics,d=>d.Code=="SPECIFICATION"&&d.Message.Contains("фон"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ZoneCreationScopeUsesOnlyMeasuredMatchingBackgroundForGridElevationAndArea(bool totalArea)
    {
        var existing=AsExisting(Calculate(Parameters(false)).Bars,true);
        var p=ZoneParameters("Низ X");p.CreationScope=ReinforcementCreationScope.Zones;
        foreach(var layer in p.BackgroundLayers)layer.Enabled=true;
        if(totalArea){p.DemandMeaning="TotalArea";p.Selections[0].RequiredAreaMm2PerM=1000;}
        var plan=Calculate(p,existing);
        Assert.True(plan.CanCreate,Messages(plan));
        Assert.All(plan.Bars,b=>{Assert.Equal("Zones",b.Placement.Component);Assert.Equal(-158.98,b.Placement.ElevationMm,6);});
        Assert.Equal(1100,plan.Bars.Min(b=>b.Start.Y),6);
        Assert.NotEmpty(plan.ReferenceBars);
        Assert.All(plan.ReferenceBars,b=>
        {
            Assert.Equal("Background|Низ X",b.SpecKey);
            var measuredPath=Assert.Single(b.Centerline);
            Assert.Contains(existing,e=>e.Centerline[0].Start.Equals(measuredPath.Start)&&e.Centerline[0].End.Equals(measuredPath.End));
        });
    }

    [Fact]
    public void ZoneCreationScopeRecognizesMeasuredYWithoutDependingOnConfiguredOuterX()
    {
        var existing=AsExisting(Calculate(Parameters(false)).Bars,true);
        var p=ZoneParameters("Низ Y");p.CreationScope=ReinforcementCreationScope.Zones;
        p.BackgroundLayers.RemoveAll(l=>l.Direction=="Низ X");
        var plan=Calculate(p,existing);
        Assert.True(plan.CanCreate,Messages(plan));
        Assert.All(plan.Bars,b=>Assert.Equal(-144.98,b.Placement.ElevationMm,6));
        Assert.All(plan.ReferenceBars,b=>Assert.Equal("Background|Низ Y",b.SpecKey));
    }

    [Fact]
    public void ZoneCreationScopeUsesLocalEndsAndReusesExistingWithoutCreatingMainPerimeter()
    {
        var p=SharedPreviewParameters();p.CreationScope=ReinforcementCreationScope.Zones;
        p.Perimeter.SpecKey="missing";
        var first=Calculate(p,zoneFaces:SharedPreviewFaces());
        Assert.True(first.CanCreate,Messages(first));
        Assert.All(first.Bars,b=>Assert.Equal(PreviewPurpose.Zones,b.PreviewPurpose));
        var ends=first.Bars.Where(b=>b.Placement.Component=="PerimeterU").ToList();Assert.NotEmpty(ends);
        var existing=AsExisting(ends,false);
        var next=Calculate(p,existing,SharedPreviewFaces());
        Assert.True(next.CanCreate,Messages(next));
        Assert.All(next.Bars,b=>Assert.Equal("Zones",b.Placement.Component));
        Assert.Equal(ends.Count,next.ReferenceBars.Count);
        Assert.All(next.ReferenceBars,b=>Assert.Equal(PreviewPurpose.Zones,b.PreviewPurpose));
    }

    [Fact]
    public void ZoneCreationScopeIgnoresUnrelatedExistingMainUMismatch()
    {
        var main=Calculate(Parameters(true));
        var existing=AsExisting(main.Bars.Where(b=>b.Placement.Component=="PerimeterU"),false);
        var p=ZoneParameters("Низ X");p.CreationScope=ReinforcementCreationScope.Zones;p.Perimeter.Enabled=true;p.Perimeter.SpecKey="b16";
        var plan=Calculate(p,existing);
        Assert.True(plan.CanCreate,Messages(plan));
        Assert.All(plan.Bars,b=>Assert.Equal("Zones",b.Placement.Component));
        Assert.Empty(plan.ReferenceBars);
    }

    [Theory]
    [InlineData(ReinforcementCreationScope.Background)]
    [InlineData(ReinforcementCreationScope.Zones)]
    public void CreationScopeDoesNotMutateSavedComponentSelections(ReinforcementCreationScope scope)
    {
        var p=SharedPreviewParameters();p.CreationScope=scope;
        string before=System.Text.Json.JsonSerializer.Serialize(p);
        var plan=Calculate(p,zoneFaces:SharedPreviewFaces());
        Assert.True(plan.CanCreate,Messages(plan));
        Assert.Equal(before,System.Text.Json.JsonSerializer.Serialize(p));
        Assert.All(plan.Bars,b=>Assert.Equal(scope==ReinforcementCreationScope.Background?PreviewPurpose.Background:PreviewPurpose.Zones,b.PreviewPurpose));
    }

    [Theory]
    [InlineData(ReinforcementCreationScope.Background)]
    [InlineData(ReinforcementCreationScope.Zones)]
    public void InactiveAmbiguousExistingTopLayerDoesNotBlockLowerScope(ReinforcementCreationScope scope)
    {
        var original=Calculate(Parameters(false)).Bars.Where(b=>b.SpecKey=="Background|Верх X").ToList();
        var duplicate=original.Select(b=>new PlannedBar{Start=b.Start,End=b.End,SpecKey=b.SpecKey,Placement=new()
            {TypeKey=b.Placement.TypeKey,Component=b.Placement.Component,ModelDiameterMm=b.Placement.ModelDiameterMm,ElevationMm=b.Placement.ElevationMm-1}}).ToList();
        var existing=AsExisting(original.Concat(duplicate),false);
        var p=ZoneParameters("Низ X");p.CreationScope=scope;
        p.BackgroundLayers.Single(l=>l.Direction=="Низ X").Enabled=true;
        var plan=Calculate(p,existing);
        Assert.True(plan.CanCreate,Messages(plan));
        Assert.DoesNotContain(plan.Diagnostics,d=>d.Code=="BACKGROUND_AMBIGUOUS");
        Assert.Empty(plan.ReferenceBars);
    }

    [Fact]
    public void BackgroundCreationScopeRetainsExistingOuterXHeightCheckForNewY()
    {
        var original=Calculate(Parameters(false)).Bars.Where(b=>b.SpecKey=="Background|Низ X").ToList();
        foreach(var placement in original.Select(b=>b.Placement).Distinct())placement.ElevationMm+=.5;
        var existing=AsExisting(original,false);
        var p=Parameters(false);p.CreationScope=ReinforcementCreationScope.Background;
        foreach(var layer in p.BackgroundLayers)layer.Enabled=layer.Direction=="Низ Y";
        var plan=Calculate(p,existing);
        Assert.False(plan.CanCreate);
        Assert.Contains(plan.Diagnostics,d=>d.Code=="BACKGROUND_STACK_MISMATCH");
    }
    [Theory]
    [InlineData("Низ X", -157d)]
    [InlineData("Низ Y", -145d)]
    [InlineData("Верх X", -43d)]
    [InlineData("Верх Y", -55d)]
    public void ConnectedLineUsesWinningTypeStepAndSectionLayer(string direction, double elevation)
    {
        var parameters = ConnectedLineParameters(direction);
        var plan = Calculate(parameters, zoneFaces: ConnectedLineFaces(direction, false));
        Assert.True(plan.CanCreate, Messages(plan));
        Assert.NotEmpty(plan.Bars);
        Assert.All(plan.Bars, bar =>
        {
            Assert.Equal("Zones", bar.Placement.Component);
            Assert.Equal("b16", bar.Placement.TypeKey);
            Assert.Equal(16, bar.Placement.ModelDiameterMm);
            Assert.Equal(elevation, bar.Placement.ElevationMm, 6);
        });
        Assert.All(plan.Groups, group => Assert.Equal(300, group.SpacingMm));
    }

    [Theory]
    [InlineData("Низ X")]
    [InlineData("Низ Y")]
    [InlineData("Верх X")]
    [InlineData("Верх Y")]
    public void ConnectedLineLocalEndsInheritWinningType(string direction)
    {
        var parameters = ConnectedLineParameters(direction);
        parameters.UsePerimeterUForZoneEnds = true;
        parameters.AutomaticULegLength = true;
        var plan = Calculate(parameters, zoneFaces: ConnectedLineFaces(direction, true));
        Assert.True(plan.CanCreate, Messages(plan));
        Assert.Contains(plan.Bars, bar => bar.Placement.Component == "Zones");
        Assert.Contains(plan.Bars, bar => bar.Placement.Component == "PerimeterU");
        Assert.All(plan.Bars, bar => Assert.Equal("b16", bar.Placement.TypeKey));
        Assert.All(plan.Groups.Where(group => group.Placement.Component == "Zones"), group => Assert.Equal(300, group.SpacingMm));
    }

    [Theory]
    [InlineData(1000d, false)]
    [InlineData(600d, true)]
    public void ConnectedLineNumericalDemandIsCheckedAfterChoosingTheLargerDiameter(double requiredArea, bool expectedValid)
    {
        var parameters = ConnectedLineParameters("Низ X");
        parameters.DemandMeaning = "AdditionalArea";
        parameters.Selections[0].RequiredAreaMm2PerM = requiredArea;
        parameters.Selections[1].RequiredAreaMm2PerM = 500;
        // Both source assignments pass independently. Ø16/300 has only 670 mm²/m,
        // so the original Ø12/100 requirement must survive choosing the common type.
        var plan = Calculate(parameters, zoneFaces: ConnectedLineFaces("Низ X", false));
        Assert.Equal(expectedValid, plan.CanCreate);
        if (expectedValid)
            Assert.All(plan.Bars, bar => Assert.Equal("b16", bar.Placement.TypeKey));
        else
            Assert.Contains(plan.Diagnostics, diagnostic => diagnostic.IsBlocking && diagnostic.Code == "LINE_ASSIGNMENT_AREA_DEFICIT");
    }

    [Fact]
    public void ConnectedLineTotalAreaPassesOnlyTheDeficitAfterVerifiedBackground()
    {
        var background = Calculate(Parameters(false));
        var existing = AsExisting(background.Bars.Where(bar => bar.SpecKey == "Background|Низ X"), false);
        var parameters = ConnectedLineParameters("Низ X");
        parameters.DemandMeaning = "TotalArea";
        parameters.Selections[0].SpecKey = "b12";
        parameters.Selections[0].SpacingMm = 200;
        parameters.Selections[1].SpecKey = "b12";
        parameters.Selections[1].SpacingMm = 400;
        foreach (var selection in parameters.Selections) selection.RequiredAreaMm2PerM = 840;
        // Existing Ø12/200 supplies 565.5; only 274.5 is required from the line.
        // Passing the full 840 to the line check would incorrectly reject Ø12/200.
        var plan = Calculate(parameters, existing, ConnectedLineFaces("Низ X", false));
        Assert.True(plan.CanCreate, Messages(plan));
        Assert.NotEmpty(plan.ReferenceBars);
        Assert.NotEmpty(plan.Bars);
        Assert.All(plan.Groups, group => Assert.Equal(200, group.SpacingMm));
        Assert.DoesNotContain(plan.Diagnostics, diagnostic => diagnostic.Code == "LINE_ASSIGNMENT_AREA_DEFICIT");
    }

    private static PreviewParameters ConnectedLineParameters(string direction)
    {
        var parameters = ZoneParameters(direction);
        parameters.CreationScope = ReinforcementCreationScope.Zones;
        parameters.Selections = new()
        {
            new() { ColorKey="red", SpecKey="b12", SpacingMm=100 },
            new() { ColorKey="blue", SpecKey="b16", SpacingMm=300 }
        };
        return parameters;
    }

    private static List<FaceInput> ConnectedLineFaces(string direction, bool atEdge)
    {
        bool alongX = direction.EndsWith("X");
        double end = atEdge ? (alongX ? 3990 : 2990) : (alongX ? 2400 : 1900);
        double start = atEdge ? end - 1500 : (alongX ? 1200 : 1100);
        double middle = (start + end) / 2;
        FaceInput Face(string color, double a, double b) => new()
        {
            Id=color, ColorKey=color,
            Vertices=new (double p,double c)[]{(a,900),(b,900),(b,2100),(a,2100)}
                .Select(point => alongX ? new Point2(point.p,point.c) : new Point2(point.c,point.p)).ToList()
        };
        return new() { Face("red",start,middle), Face("blue",middle,end) };
    }

    private static List<FaceInput> SharedPreviewFaces()=>new(){new(){Id="edge",ColorKey="red",
        Vertices=new(){new(3300,1000),new(3990,1000),new(3990,2000),new(3300,2000)}}};

    private static PreviewParameters ZoneParameters(string direction)
    {
        var parameters = Parameters(false);
        foreach (var layer in parameters.BackgroundLayers) layer.Enabled = false;
        parameters.IncludeZones = true; parameters.Direction = direction;
        parameters.SourceGroupKey = "zone-source"; parameters.CalculationAxesConfirmed = true;
        parameters.RoundIncrementMm = 1; parameters.DemandMeaning = "Manual";
        // The zone is Ø12 while the configured Y background is Ø16: virtual placement
        // must use the zone's physical radius, not borrow the disabled Y axis.
        parameters.Selections = new() { new() { ColorKey = "red", SpecKey = "b12", SpacingMm = 200 } };
        return parameters;
    }

    private static PreviewParameters Parameters(bool perimeter) => new()
    {
        TopCoverMm = 35, BottomCoverMm = 35, EdgeCoverMm = 35, IncludeZones = false,
        Direction = "Низ X", ConcreteClass = "B25", AllowBarIntersections = false,
        BackgroundLayers = new[] { "Низ X", "Низ Y", "Верх X", "Верх Y" }.Select(d => new PreviewBackgroundLayer
            { Enabled = true, Direction = d, SpecKey = d.EndsWith("X") ? "b12" : "b16", SpacingMm = d.StartsWith("Низ") ? 200 : 250 }).ToList(),
        Perimeter = new PreviewPerimeterOptions { Enabled = perimeter, SpecKey = "u8", SpacingMm = 777, LegLengthMm = 500 }
    };

    private static ReinforcementPlan Calculate(PreviewParameters parameters, List<PlannedBar> existing = null, List<FaceInput> zoneFaces = null)
    {
        var session = (ReinforcementSession)RuntimeHelpers.GetUninitializedObject(typeof(ReinforcementSession));
        Set("_slab", new SlabGeometrySnapshot
        {
            ThicknessMm = 200, TopCoverMm = 35, BottomCoverMm = 35, SideCoverMm = 35,
            Slabs = new() { new() { Shell = new() { new(0, 0), new(4000, 0), new(4000, 3000), new(0, 3000) } } }
        });
        var mesh = new ImportedMeshSnapshot();
        mesh.Groups.Add(new CadPlaneGroup { Key = "zone-source", Faces = zoneFaces ?? new() { new() { Id = "zone", ColorKey = "red",
            Vertices = new() { new(1200,1000),new(2400,1000),new(2400,2000),new(1200,2000) } } } });
        Set("_mesh", mesh);
        Set("_existingBars", existing ?? new());
        Set("_hasExistingReinforcement", existing?.Count > 0);
        Set("_types", new[] { ("b12", 12d), ("b16", 16d), ("u8", 8d) }.ToDictionary(v => v.Item1,
            v => new BarTypeSnapshot { Key = v.Item1, Name = v.Item1, DiameterMm = v.Item2,
                ModelDiameterMm = v.Item2, BendDiameterMm = v.Item2 * 4, SteelGrade = 500 }));
        return (ReinforcementPlan)typeof(ReinforcementSession).GetMethod("Calculate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(session, new object[] { parameters, CancellationToken.None })!;
        void Set(string name, object value) => typeof(ReinforcementSession).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session, value);
    }

    private static List<PlannedBar> AsExisting(IEnumerable<PlannedBar> source, bool measuredShift)
    {
        int index = 0;
        return source.Select(b =>
        {
            double dx = measuredShift ? 0.02 : 0, dy = measuredShift ? 0.01 : 0;
            double dz = measuredShift ? (b.Placement.ElevationMm < -100 ? 0.02 : -0.02) : 0;
            var path = b.Centerline ?? new List<CenterlineSegment> { new() {
                Start = new(b.Start.X, b.Start.Y, b.Placement.ElevationMm), End = new(b.End.X, b.End.Y, b.Placement.ElevationMm) } };
            Point3 Shift(Point3 p) => new(p.X + dx, p.Y + dy, p.Z + dz);
            return new PlannedBar
            {
                Start = new(b.Start.X + dx, b.Start.Y + dy), End = new(b.End.X + dx, b.End.Y + dy), SpecKey = "Existing|" + index++,
                Centerline = path.Select(s => new CenterlineSegment { Start = Shift(s.Start), End = Shift(s.End), Midpoint = s.Midpoint.HasValue ? Shift(s.Midpoint.Value) : null }).ToList(),
                Placement = new() { TypeKey = b.Placement.TypeKey, Component = "Existing", ModelDiameterMm = b.Placement.ModelDiameterMm,
                    ElevationMm = b.Placement.ElevationMm + dz, Distribution = b.Placement.Distribution }
            };
        }).ToList();
    }

    private static IEnumerable<(string, double, double, double, double, double)> BackgroundGeometry(ReinforcementPlan plan) =>
        plan.Bars.Where(b => b.Placement.Component == "Background")
            .Select(b => (b.SpecKey, b.Start.X, b.Start.Y, b.End.X, b.End.Y, b.Placement.ElevationMm)).OrderBy(p => p);
    private static string Messages(ReinforcementPlan plan) => string.Join("; ", plan.Diagnostics.Select(d => d.Code + ": " + d.Message));
}
