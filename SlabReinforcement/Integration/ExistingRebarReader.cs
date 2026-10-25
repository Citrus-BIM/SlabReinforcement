using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using SlabReinforcement.Core;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SlabReinforcement.Integration
{
    internal static class ExistingRebarReader
    {
        private const int MaximumPhysicalBars = 200000;

        public static List<PlannedBar> Read(SlabGeometrySnapshot slab, Func<ElementId, bool> include = null)
        {
            var doc=slab.Host.Document;
            var systemBars=new FilteredElementCollector(doc).OfClass(typeof(RebarInSystem)).Cast<RebarInSystem>()
                .Where(r=>r.GetHostId().Equals(slab.Host.Id)).ToList();
            // The predicate describes physical child IDs. A childless system cannot be
            // classified by that predicate; the complete slab read checks those systems.
            if(include==null)RequireInspectableSystems(slab,new HashSet<ElementId>(systemBars.Select(r=>r.Id)));
            var result=new List<PlannedBar>();
            foreach(var rebar in new FilteredElementCollector(doc).OfClass(typeof(Rebar)).Cast<Rebar>().Where(r=>r.GetHostId().Equals(slab.Host.Id) && (include == null || include(r.Id))))
            {
                var type=ReadBarType(doc,rebar);
#if R2019 || R2020 || R2021
                if(!rebar.IsRebarShapeDriven()) throw new ArgumentException("В плите есть арматура свободной формы. Для её проверки нужен Revit 2022 или новее.");
#endif
                for(int index=0;index<rebar.NumberOfBarPositions;index++)
                {
                    if(!rebar.DoesBarExistAtPosition(index))continue;
                    RequireCapacity(result);
#if R2019 || R2020 || R2021
                    IList<Curve> curves;
                    using(var accessor=rebar.GetShapeDrivenAccessor())
                    {
                        var transform=accessor.GetBarPositionTransform(index);
                        curves=rebar.GetCenterlineCurves(true,false,false,MultiplanarOption.IncludeAllMultiplanarCurves,index).Select(c=>c.CreateTransformed(transform)).ToList();
                    }
#else
                    // Fine geometry can move overlapping stirrup legs out of their sketch plane.
                    var curves=rebar.GetTransformedCenterlineCurves(true,false,false,MultiplanarOption.IncludeAllMultiplanarCurves,index);
#endif
                    result.Add(ReadPhysicalBar(slab,rebar,type,index,curves));
                }
            }
            foreach(var rebar in systemBars.Where(r=>include==null || include(r.Id)))
            {
                var type=ReadBarType(doc,rebar);
                for(int index=0;index<rebar.NumberOfBarPositions;index++)
                {
                    if(!rebar.DoesBarExistAtPosition(index))continue;
                    RequireCapacity(result);
#if R2019 || R2020 || R2021
                    // Before 2022 the system API exposes the first bar and a translation
                    // for each position. Individual moved bars were introduced in 2022.
                    var transform=rebar.GetBarPositionTransform(index);
                    var curves=rebar.GetCenterlineCurves(true,false,false).Select(c=>c.CreateTransformed(transform)).ToList();
#else
                    // Includes both the distribution transform and any individual move.
                    var curves=rebar.GetTransformedCenterlineCurves(true,false,false,index);
#endif
                    result.Add(ReadPhysicalBar(slab,rebar,type,index,curves));
                }
            }
            return result;
        }

        private static void RequireInspectableSystems(SlabGeometrySnapshot slab, HashSet<ElementId> physicalIds)
        {
            var doc=slab.Host.Document;
            foreach(var system in new FilteredElementCollector(doc).OfClass(typeof(PathReinforcement)).Cast<PathReinforcement>()
                .Where(r=>r.GetHostId().Equals(slab.Host.Id)))
                RequireInspectableSystem(system,system.GetRebarInSystemIds(),physicalIds);
            foreach(var system in new FilteredElementCollector(doc).OfClass(typeof(AreaReinforcement)).Cast<AreaReinforcement>()
                .Where(r=>r.GetHostId().Equals(slab.Host.Id)))
                RequireInspectableSystem(system,system.GetRebarInSystemIds(),physicalIds);
        }

        private static void RequireInspectableSystem(Element system, ICollection<ElementId> children, HashSet<ElementId> physicalIds)
        {
            if(children.Count==0 || children.Any(id=>!physicalIds.Contains(id)))
                throw new ArgumentException("Не прочитаны стержни системы "+system.Id+". Включите создание стержней в настройках систем армирования.");
        }

        private static RebarBarType ReadBarType(Document doc, Element rebar)
        {
            var type=doc.GetElement(rebar.GetTypeId()) as RebarBarType;
            if(type==null)throw new ArgumentException("Не удалось определить тип существующего стержня "+rebar.Id+".");
            return type;
        }

        private static void RequireCapacity(List<PlannedBar> result)
        {
            if(result.Count>=MaximumPhysicalBars)throw new ArgumentException("В плите слишком много стержней для проверки.");
        }

        private static PlannedBar ReadPhysicalBar(SlabGeometrySnapshot slab, Element rebar, RebarBarType type, int index, IList<Curve> curves)
        {
            if(curves.Count==0)throw new ArgumentException("Не удалось прочитать форму существующего стержня "+rebar.Id+", позиция "+index+".");
#if R2019 || R2020 || R2021
            double diameter=type.BarDiameter*304.8;
#else
            double diameter=type.BarModelDiameter*304.8;
#endif
            var path=new List<CenterlineSegment>();
            Func<XYZ,Point3> local=p=>slab.Frame.ToLocal(new Point3(p.X*304.8,p.Y*304.8,p.Z*304.8));
            foreach(var curve in curves)
            {
                if(!(curve is Line) && !(curve is Arc))throw new ArgumentException("Форма существующей арматуры не поддерживается: допустимы только прямые и дуги окружности.");
                path.Add(new CenterlineSegment {Start=local(curve.GetEndPoint(0)),End=local(curve.GetEndPoint(1)),Midpoint=curve is Arc?(Point3?)local(curve.Evaluate(0.5,true)):null});
            }
            return new PlannedBar {SpecKey="Existing|"+rebar.UniqueId+"|"+index,Centerline=path,
                Start=new Point2(path[0].Start.X,path[0].Start.Y),End=new Point2(path[path.Count-1].End.X,path[path.Count-1].End.Y),
                Placement=new BarPlacement {TypeKey=type.UniqueId,Component="Existing",ModelDiameterMm=diameter,ElevationMm=path[0].Start.Z}};
        }
    }
}
