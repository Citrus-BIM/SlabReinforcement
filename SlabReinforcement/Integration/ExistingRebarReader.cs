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
        public static List<PlannedBar> Read(SlabGeometrySnapshot slab, Func<ElementId, bool> include = null)
        {
            var doc=slab.Host.Document;
            if(new FilteredElementCollector(doc).OfClass(typeof(RebarInSystem)).Cast<RebarInSystem>().Any(r=>r.GetHostId().Equals(slab.Host.Id)))
                throw new ArgumentException("В плите есть арматура системы. Проверка её совместимости пока не поддерживается; создание дополнительной арматуры заблокировано.");
            var result=new List<PlannedBar>();
            foreach(var rebar in new FilteredElementCollector(doc).OfClass(typeof(Rebar)).Cast<Rebar>().Where(r=>r.GetHostId().Equals(slab.Host.Id) && (include == null || include(r.Id))))
            {
                var type=(RebarBarType)doc.GetElement(rebar.GetTypeId());
#if R2019 || R2020 || R2021
                double diameter=type.BarDiameter*304.8;
                if(!rebar.IsRebarShapeDriven()) throw new ArgumentException("Проверка существующей арматуры свободной формы требует Revit 2022 или новее.");
#else
                double diameter=type.BarModelDiameter*304.8;
#endif
                for(int index=0;index<rebar.NumberOfBarPositions;index++)
                {
                    if(!rebar.DoesBarExistAtPosition(index))continue;
                    if(result.Count>=200000)throw new ArgumentException("Слишком много существующих стержней в плите для совместной проверки.");
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
                    if(curves.Count==0)throw new ArgumentException("Не удалось прочитать ось существующего стержня.");
                    var path=new List<CenterlineSegment>();
                    foreach(var curve in curves)
                    {
                        if(!(curve is Line) && !(curve is Arc))throw new ArgumentException("Существующий стержень имеет неподдерживаемую кривую. Совместная проверка невозможна.");
                        Func<XYZ,Point3> local=p=>slab.Frame.ToLocal(new Point3(p.X*304.8,p.Y*304.8,p.Z*304.8));
                        path.Add(new CenterlineSegment {Start=local(curve.GetEndPoint(0)),End=local(curve.GetEndPoint(1)),Midpoint=curve is Arc?(Point3?)local(curve.Evaluate(0.5,true)):null});
                    }
                    result.Add(new PlannedBar {SpecKey="Existing|"+rebar.UniqueId+"|"+index,Centerline=path,
                        Start=new Point2(path[0].Start.X,path[0].Start.Y),End=new Point2(path[path.Count-1].End.X,path[path.Count-1].End.Y),
                        Placement=new BarPlacement {TypeKey=type.UniqueId,Component="Existing",ModelDiameterMm=diameter,ElevationMm=path[0].Start.Z}});
                }
            }
            return result;
        }
    }
}
