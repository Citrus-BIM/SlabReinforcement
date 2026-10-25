using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Expression = System.Linq.Expressions.Expression;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using SlabReinforcement.Core;
using WpfCheckBox = System.Windows.Controls.CheckBox;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfTextBox = System.Windows.Controls.TextBox;
using WpfButton = System.Windows.Controls.Button;
using WpfTextBlock = System.Windows.Controls.TextBlock;
using WpfDataGrid = System.Windows.Controls.DataGrid;

namespace SlabReinforcement.UnderlayUiSmoke
{
    public sealed partial class UnderlayApplication : IExternalApplication
    {
        private string _run;
        private readonly Dictionary<string,object> _report = new Dictionary<string,object>();
        private readonly List<object> _checks = new List<object>();
        public Result OnStartup(UIControlledApplication app)
        {
            _run=File.ReadAllText(Path.Combine(Path.GetDirectoryName(GetType().Assembly.Location),"run-directory.txt")).Trim();
            app.Idling+=Execute;
            _report["startedUtc"]=DateTime.UtcNow.ToString("O");
            _report["revitVersion"]=app.ControlledApplication.VersionNumber;
            _report["revitBuild"]=app.ControlledApplication.VersionBuild;
            Log("STARTUP");return Result.Succeeded;
        }
        public Result OnShutdown(UIControlledApplication app){Log("SHUTDOWN");return Result.Succeeded;}
        private void Execute(object sender,IdlingEventArgs args)
        {
            var ui=(UIApplication)sender;ui.Idling-=Execute;Document doc=null;
            try
            {
                bool newTemplate=File.Exists(Path.Combine(_run,"new-template-native.flag"));
                doc=newTemplate?ui.Application.NewProjectDocument(Path.Combine(_run,"template.rte")):ui.Application.OpenDocumentFile(Path.Combine(_run,"input.rvt"));
                bool modified=doc.IsModified;
                int bars=new FilteredElementCollector(doc).OfClass(typeof(Rebar)).GetElementCount();
                var types=new FilteredElementCollector(doc).OfClass(typeof(RebarBarType)).Cast<RebarBarType>().ToList();
                Require(types.Count>0,"Actual template types are required.");
                if(File.Exists(Path.Combine(_run,"current-plugin-plan.flag")))RunCurrentPluginPlan(ui.Application,ref doc);
                else if(newTemplate)RunNewTemplateNative(ui.Application,ref doc,types);
                else if(File.Exists(Path.Combine(_run,"native-path-probe.flag")))RunNativePathProbe(ui.Application,ref doc,types);
                else if(File.Exists(Path.Combine(_run,"native-area-probe.flag")))RunNativeAreaProbe(ui.Application,ref doc,types);
                else
                {
                    if(File.Exists(Path.Combine(_run,"layer-alignment-audit.flag")))RunLayerAlignmentAudit(types);
                    else if(File.Exists(Path.Combine(_run,"area-template-inventory.flag")))RunNativeAreaTemplateInventory(doc,types);
                    else if(File.Exists(Path.Combine(_run,"background-capture-audit.flag")))RunBackgroundCaptureAudit(types);
                    else RunModalProbe(doc,types);
                    Require(doc.IsModified==modified&&new FilteredElementCollector(doc).OfClass(typeof(Rebar)).GetElementCount()==bars,"Read-only UI probe changed document state.");
                    Check("document-unchanged",new{modified,bars,writeTransactions=0,saved=false});
                }
                _report["status"]="PASS";
            }
            catch(Exception error){_report["status"]="FAILED";_report["exception"]=error.ToString();Log("FAILED "+error);}
            finally
            {
                if(doc!=null&&doc.IsValidObject)doc.Close(false);
                _report["finishedUtc"]=DateTime.UtcNow.ToString("O");_report["checks"]=_checks;
                File.WriteAllText(Path.Combine(_run,"results.json"),new JavaScriptSerializer{MaxJsonLength=int.MaxValue}.Serialize(_report));
                Log("COMPLETED "+_report["status"]);
                ui.PostCommand(RevitCommandId.LookupPostableCommandId(PostableCommand.ExitRevit));
            }
        }
        private void RunModalProbe(Document doc,List<RebarBarType> types)
        {
            string workspace=Path.GetFullPath(Path.Combine(_run,"..","..",".."));
            string productPath=Path.Combine(workspace,"SlabReinforcement","bin","R2023","SlabReinforcement.dll");
            string explicitProduct=Path.Combine(_run,"product-path.txt");
            if(File.Exists(explicitProduct))productPath=File.ReadAllText(explicitProduct).Trim();
            var assembly=Assembly.LoadFrom(productPath);var windowType=assembly.GetType("SlabReinforcement.SlabReinforcementWPF",true);
            foreach(string name in new[]{"RequestUnderlaySelection","ConsumeUnderlaySelectionRequest","ApplyUnderlay","ResumePreview"})
                Require(windowType.GetMethod(name,BindingFlags.Public|BindingFlags.Instance)!=null,"Missing new public method: "+name);
            _report["product"]=new{path=productPath,sha256=Hash(productPath),coreSha256=Hash(typeof(ReinforcementPlan).Assembly.Location)};
            string settings=(string)assembly.GetType("SlabReinforcement.SlabReinforcementSettings",true).GetMethod("GetSettingsFilePath").Invoke(null,null);
            string legacy=Path.Combine(Path.GetDirectoryName(productPath),"SlabReinforcementSettings.xml");
            Require(File.Exists(settings)||!File.Exists(legacy),"Probe refuses settings migration.");
            string settingsHash=Hash(settings),legacyHash=Hash(legacy);
            var old=new CalculationGate(1,true);var current=new CalculationGate(2,false);var replacement=new CalculationGate(3,false);
            Window window=null;var binding=new BindingErrors();var trace=PresentationTraceSources.DataBindingSource;var oldLevel=trace.Switch.Level;
            trace.Listeners.Add(binding);trace.Switch.Level=SourceLevels.Error;
            try
            {
                object empty=Context(assembly,types,old,null);
                window=(Window)Activator.CreateInstance(windowType,new[]{Colors(assembly),types,empty});
                window.WindowStartupLocation=WindowStartupLocation.Manual;window.Left=-32000;window.Top=-32000;
                window.Width=1280;window.Height=850;window.ShowActivated=false;window.ShowInTaskbar=false;
                Require(window.FindName("CheckBox_IncludeZones")==null,"Redundant zone toggle is still exposed.");
                Require(ReferenceEquals(Control<System.Windows.Controls.TabControl>(window,"ComponentsTabs").SelectedItem,window.FindName("BackgroundTab")),"Source-free window must start on background.");
                Control<WpfComboBox>(window,"ComboBox_ConcreteClass").SelectedItem="B25";
                Control<WpfComboBox>(window,"ComboBox_Direction").SelectedItem="Низ Y";
                Control<WpfTextBox>(window,"TextBox_EdgeCover").Text="37";
                Require(window.FindName("TextBox_LayerOffset")==null,"The removed zone-offset editor must not be exposed.");
                Control<WpfTextBox>(window,"TextBox_ZoneMergeTolerance").Text="17";
                Control<WpfTextBox>(window,"TextBox_RoundIncrement").Text="5";
                Control<WpfCheckBox>(window,"CheckBox_PerimeterEnabled").IsChecked=false;
                var rows=((IEnumerable)Control<WpfDataGrid>(window,"BackgroundDataGrid").ItemsSource).Cast<object>().ToList();
                foreach(object row in rows){Set(row,"Enabled",false);Set(row,"SelectedRebarType",types[0]);Set(row,"SpacingText","175");}
                Set(rows[0],"Enabled",true);
                var editors=EditorSnapshot(window);
                Modal(window,()=>old.Started.IsSet,()=>{Render(window,"01-no-underlay.png");Call(window,"RequestUnderlaySelection");});
                Require(!window.IsVisible&&!BoolField(window,"_closed"),"Hide closed the reusable window.");
                Require((bool)Get(window,"UnderlaySelectionRequested")&&old.Cancelled,"Request did not expose selection and cancel old preview.");
                Require(GetField(window,"_readyPlan")==null&&!Control<WpfButton>(window,"Button_Confirm").IsEnabled,"Hidden window retained an accepted/ready plan.");
                Consume(window);ApiReadBetweenDialogs(doc);Check("initial-no-source-modal-hide",new{sameWindowReusable=true,oldCalculationCancelled=true});

                object firstColors=Colors(assembly,"RGB(0, 255, 0)");object firstColor=((IList)firstColors)[0];
                Set(firstColor,"IsSelected",true);Set(firstColor,"RequiredAreaText","999");
                object firstContext=Context(assembly,types,current,"map-a");
                Call(window,"ApplyUnderlay",firstColors,firstContext,"Synthetic map A");
                Require(ReferenceEquals(GetField(window,"_context"),firstContext),"ApplyUnderlay did not replace context.");
                Require(!(bool)Get(firstColor,"IsSelected")&&(string)Get(firstColor,"RequiredAreaText")=="","New source retained selection/As.");
                AssertResetAndEditors(window,editors);EnableFirstColor(window,firstColor);
                Call(window,"ResumePreview");
                bool oldReleased=false;DateTime releaseTime=DateTime.MaxValue;
                Modal(window,()=>
                {
                    if(!ReferenceEquals(GetField(window,"_readyPlan"),current.Plan))return false;
                    if(!oldReleased){oldReleased=true;releaseTime=DateTime.UtcNow;old.Release.Set();return false;}
                    return old.Completed.IsSet&&DateTime.UtcNow-releaseTime>TimeSpan.FromMilliseconds(400);
                },()=>
                {
                    Require(ReferenceEquals(GetField(window,"_readyPlan"),current.Plan),"A late stale result replaced the current source preview.");
                    VerifyPreviewScopes(window,current);
                    Render(window,"02-applied-source.png");Call(window,"RequestUnderlaySelection");
                });
                Consume(window);ApiReadBetweenDialogs(doc);Check("apply-source-cancel-stale-preview",new{oldCompleted=true,staleNotApplied=true,source="map-a",preservedEditors=true});

                // Selection cancellation: no ApplyUnderlay, reopen the same window/context and inputs.
                Call(window,"ResumePreview");
                Modal(window,()=>ReferenceEquals(GetField(window,"_readyPlan"),current.Plan),()=>
                {
                    Require(ReferenceEquals(GetField(window,"_context"),firstContext),"Cancel-return replaced the context.");
                    Require((bool)Get(firstColor,"IsSelected"),"Cancel-return lost the prior color choice.");
                    Require(EditorSnapshot(window)==editors,"Cancel-return changed common parameters.");
                    Call(window,"RequestUnderlaySelection");
                });
                Consume(window);Check("selection-cancel-reopen",new{sameWindow=true,sameContext=true,editorChoicesPreserved=true});

                // Replacement source shares a color so only type/spacing may be preserved.
                Set(firstColor,"SpacingText","185");Set(firstColor,"RequiredAreaText","456");
                Control<WpfCheckBox>(window,"CheckBox_ZoneEndsInPerimeter").IsChecked=true;
                object finalColors=Colors(assembly,"RGB(0, 255, 0)","RGB(0, 255, 255)");
                object finalContext=Context(assembly,types,replacement,"map-b");
                Call(window,"ApplyUnderlay",finalColors,finalContext,"Synthetic map B");
                AssertResetAndEditors(window,editors);
                var finalRows=((IList)finalColors).Cast<object>().ToList();
                Require(finalRows.All(c=>!(bool)Get(c,"IsSelected")&&(string)Get(c,"RequiredAreaText")==""),"Replacement retained old color selection/As.");
                Require((string)Get(finalRows[0],"SpacingText")=="185","Same-color spacing was not preserved.");
                Require(Control<WpfDataGrid>(window,"ColorStatsDataGrid").Items.Count==2,"Replacement colors were not refreshed.");
                EnableFirstColor(window,finalRows[0]);Call(window,"ResumePreview");
                Modal(window,()=>ReferenceEquals(GetField(window,"_readyPlan"),replacement.Plan),()=>
                {
                    int before=(int)GetField(window,"_revision");
                    Set(firstColor,"SpacingText","199");
                    Require((int)GetField(window,"_revision")==before,"Detached old color still invalidates the new source.");
                    Render(window,"03-replaced-source.png");
                    object ambiguous=Context(assembly,types,replacement,"map-c");
                    var sourceFaces=((List<CadPlaneGroup>)Get(ambiguous,"SourceGroups"))[0].Faces;
                    Set(ambiguous,"SourceGroups",new List<CadPlaneGroup>{
                        new CadPlaneGroup{Key="map-c1",Layer="group one",Faces=new List<FaceInput>{sourceFaces[0]}},
                        new CadPlaneGroup{Key="map-c2",Layer="group two",Faces=new List<FaceInput>{sourceFaces[1]}}});
                    Call(window,"ApplyUnderlay",Colors(assembly,"RGB(0, 255, 0)","RGB(0, 255, 255)"),ambiguous,"Synthetic multiple-group map");
                    Require(Control<WpfComboBox>(window,"ComboBox_SourceGroup").SelectedIndex==-1
                        &&((List<FaceInput>)Get(ambiguous,"Faces")).Count==0
                        &&Control<WpfDataGrid>(window,"ColorStatsDataGrid").Items.Count==0,"Replacing one group by multiple groups retained stale selection/faces/colors.");
                    Require(GetField(window,"_readyPlan")==null&&!Control<WpfButton>(window,"Button_Confirm").IsEnabled,"Ambiguous source retained a ready plan.");
                    Render(window,"04-multiple-groups.png");Check("one-to-many-source-groups",new{selectionCleared=true,facesCleared=true,readyPlanCleared=true});
                    window.Close();
                });
                window.Close(); // Mirrors the command's finally block after an already closed modal window.
                Require(Get(window,"AcceptedPlan")==null&&Get(window,"AcceptedParameters")==null,"Probe unexpectedly accepted model creation.");
                Check("replacement-source-modal-close",new{sameColorSpacingPreserved=true,oldColorDetached=true,axesAndEdgeAcknowledgmentReset=true,colorsReplaced=2});
                Require(binding.Messages.Count==0,"Binding errors: "+string.Join(";",binding.Messages));
            }
            finally
            {
                old.Release.Set();if(window!=null&&window.IsVisible)window.Close();trace.Listeners.Remove(binding);trace.Switch.Level=oldLevel;
            }
            Require(Hash(settings)==settingsHash&&Hash(legacy)==legacyHash,"Window close changed saved settings.");
            Check("settings-and-bindings",new{settingsUnchanged=true,bindingErrors=binding.Messages.Count,modalCycles=4,interactiveClicks=false});
        }
        private void Modal(Window window,Func<bool> ready,Action action)
        {
            Exception failure=null;var started=DateTime.UtcNow;bool completed=false;
            var timer=new DispatcherTimer(DispatcherPriority.ApplicationIdle,window.Dispatcher){Interval=TimeSpan.FromMilliseconds(50)};
            timer.Tick+=(sender,args)=>
            {
                try
                {
                    if(DateTime.UtcNow-started>TimeSpan.FromSeconds(12))throw new TimeoutException("Modal probe stage timed out.");
                    if(!ready())return;
                    timer.Stop();completed=true;action();
                }
                catch(Exception error){timer.Stop();failure=error;window.Close();}
            };
            timer.Start();try{window.ShowDialog();}finally{timer.Stop();}
            if(failure!=null)throw new InvalidOperationException("Modal callback failed.",failure);
            Require(completed,"ShowDialog ended before the scheduled probe action.");
        }
        private static object Context(Assembly assembly,List<RebarBarType> types,CalculationGate gate,string source)
        {
            object context=Activator.CreateInstance(assembly.GetType("SlabReinforcement.PreviewContext",true));
            Set(context,"HostName","Read-only modal underlay probe");Set(context,"AxisDescription","Synthetic UI fixture; no reinforcement written");
            Set(context,"RebarTypeKeys",types.GroupBy(t=>t.Name).ToDictionary(g=>g.Key,g=>g.First().UniqueId));
            var polygon=new PolygonInput{Shell=new List<Point2>{new Point2(0,0),new Point2(10000,0),new Point2(10000,10000),new Point2(0,10000)}};
            Set(context,"Slabs",new List<PolygonInput>{polygon});
            if(source!=null)
            {
                var faces=new List<FaceInput>{new FaceInput{Id=source+"-a",ColorKey="RGB(0, 255, 0)",Vertices=polygon.Shell},
                    new FaceInput{Id=source+"-b",ColorKey="RGB(0, 255, 255)",Vertices=polygon.Shell}};
                Set(context,"SourceGroups",new List<CadPlaneGroup>{new CadPlaneGroup{Key=source,Layer=source,Faces=faces}});
            }
            var property=context.GetType().GetProperty("Calculate");var signature=property.PropertyType.GetMethod("Invoke");
            var p=Expression.Parameter(signature.GetParameters()[0].ParameterType,"parameters");var token=Expression.Parameter(typeof(CancellationToken),"token");
            var call=Expression.Call(Expression.Constant(gate),typeof(CalculationGate).GetMethod("Calculate"),Expression.Convert(p,typeof(object)),token);
            property.SetValue(context,Expression.Lambda(property.PropertyType,call,p,token).Compile());return context;
        }
        private static object Colors(Assembly assembly,params string[] keys)
        {
            var type=assembly.GetType("ColorStat",true);var list=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(type));
            foreach(string key in keys){object color=Activator.CreateInstance(type);Set(color,"Color",key);Set(color,"ColorBrush",new SolidColorBrush(ColorsGreen));list.Add(color);}return list;
        }
        private static readonly System.Windows.Media.Color ColorsGreen=System.Windows.Media.Color.FromRgb(0,170,90);
        private static void EnableFirstColor(Window window,object color)
        {
            Control<WpfComboBox>(window,"ComboBox_DemandMeaning").SelectedValue="Manual";Set(color,"IsSelected",true);
            Require(window.FindName("CheckBox_CalculationAxesConfirmed")==null,"Redundant axis acknowledgement is still exposed.");
        }
        private static void AssertResetAndEditors(Window window,string editors)
        {
            Require(EditorSnapshot(window)==editors,"ApplyUnderlay changed background/common inputs.");
            Require(window.FindName("CheckBox_CalculationAxesConfirmed")==null
                &&Control<WpfCheckBox>(window,"CheckBox_ZoneEndsInPerimeter").IsChecked==true,"Automatic zone-end preference was not preserved.");
            Require(ReferenceEquals(Control<System.Windows.Controls.TabControl>(window,"ComponentsTabs").SelectedItem,window.FindName("ZonesTab")),"Applied source must open Zones tab.");
        }
        private static string EditorSnapshot(Window window)
        {
            Require(window.FindName("TextBox_LayerOffset")==null,"The zone-offset editor reappeared during the underlay lifecycle.");
            var values=new List<string>{Control<WpfComboBox>(window,"ComboBox_ConcreteClass").SelectedItem?.ToString(),Control<WpfComboBox>(window,"ComboBox_Direction").SelectedItem?.ToString()};
            foreach(string name in new[]{"TextBox_EdgeCover","TextBox_ZoneMergeTolerance","TextBox_RoundIncrement","TextBox_PerimeterSpacing"})values.Add(Control<WpfTextBox>(window,name).Text);
            foreach(object row in (IEnumerable)Control<WpfDataGrid>(window,"BackgroundDataGrid").ItemsSource)
                values.Add(Get(row,"Direction")+"|"+Get(row,"Enabled")+"|"+Get(row,"SpacingText")+"|"+((RebarBarType)Get(row,"SelectedRebarType"))?.UniqueId);
            return string.Join(";",values);
        }
        private void ApiReadBetweenDialogs(Document doc)
        {Require(new FilteredElementCollector(doc).OfClass(typeof(Floor)).GetElementCount()>0,"Read-only Revit API query failed between modal loops.");}
        private static void Consume(Window window){Require((bool)Call(window,"ConsumeUnderlaySelectionRequest"),"Selection request was not consumable.");Require(!(bool)Call(window,"ConsumeUnderlaySelectionRequest"),"Selection flag did not reset.");}
        private void Render(Window window,string name)
        {
            window.UpdateLayout();var content=(FrameworkElement)window.Content;
            var image=new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth),(int)Math.Ceiling(content.ActualHeight),96,96,PixelFormats.Pbgra32);
            var visual=new DrawingVisual();using(var drawing=visual.RenderOpen()){var area=new Rect(0,0,content.ActualWidth,content.ActualHeight);drawing.DrawRectangle(window.Background??Brushes.White,null,area);drawing.DrawRectangle(new VisualBrush(content),null,area);}image.Render(visual);
            var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(image));using(var file=File.Create(Path.Combine(_run,name)))png.Save(file);
        }
        private void VerifyPreviewScopes(Window window, CalculationGate gate)
        {
            var canvas=window.FindName("PreviewCanvas");
            var tabs=Control<System.Windows.Controls.TabControl>(window,"ComponentsTabs");
            int revision=(int)GetField(window,"_revision"), calls=gate.Calls;
            var zones=(ReinforcementPlan)Get(canvas,"DisplayedPlan");
            Require(zones.Bars.Count==gate.ZonePlan.Bars.Count && zones.Bars.All(b=>b.Placement.Component=="Zones" || (b.PreviewPurpose&PreviewPurpose.Zones)!=0),"Zones preview contains background or misses its U endings.");
            tabs.SelectedItem=window.FindName("BackgroundTab");
            Require(GetField(window,"_readyPlan")==null && Get(canvas,"DisplayedPlan")==null && !Control<WpfButton>(window,"Button_Confirm").IsEnabled,"Tab switch retained the other component's creation plan.");
            PumpUntil(window,()=>ReferenceEquals(GetField(window,"_readyPlan"),gate.BackgroundPlan));
            var background=(ReinforcementPlan)Get(canvas,"DisplayedPlan");
            Require(background.Bars.Count==2 && background.Bars.All(b=>(b.PreviewPurpose&PreviewPurpose.Background)!=0),"Background creation contains zones.");
            Require(Get(GetField(window,"_readyParameters"),"CreationScope").ToString()=="Background","Background calculation has the wrong creation scope.");
            Render(window,"02-background-scope.png");
            tabs.SelectedItem=window.FindName("ZonesTab");
            Require(GetField(window,"_readyPlan")==null && !Control<WpfButton>(window,"Button_Confirm").IsEnabled,"Return to Zones retained a background creation plan.");
            PumpUntil(window,()=>ReferenceEquals(GetField(window,"_readyPlan"),gate.ZonePlan));
            Require(((ReinforcementPlan)Get(canvas,"DisplayedPlan")).Bars.SequenceEqual(zones.Bars),"Tab round-trip changed zone membership.");
            Require(Get(GetField(window,"_readyParameters"),"CreationScope").ToString()=="Zones" && (int)GetField(window,"_revision")>=revision+2 && gate.Calls>=calls+2,"Tab switch did not recalculate a scoped creation plan.");
            Check("tab-creation-scopes",new{backgroundBars=background.Bars.Count,zoneBars=zones.Bars.Count,oldPlanCleared=true,onlyActiveComponentReady=true,tabChangesRecalculate=true});
        }

        private static void PumpUntil(Window window,Func<bool> ready)
        {
            var frame=new DispatcherFrame();var started=DateTime.UtcNow;Exception failure=null;
            var timer=new DispatcherTimer(DispatcherPriority.ApplicationIdle,window.Dispatcher){Interval=TimeSpan.FromMilliseconds(30)};
            timer.Tick+=(sender,args)=>
            {
                try
                {
                    if(ready()){frame.Continue=false;return;}
                    if(DateTime.UtcNow-started>TimeSpan.FromSeconds(10))throw new TimeoutException("Scoped calculation did not become ready.");
                }
                catch(Exception error){failure=error;frame.Continue=false;}
            };
            timer.Start();try{Dispatcher.PushFrame(frame);}finally{timer.Stop();}
            if(failure!=null)throw failure;
        }

        public sealed class CalculationGate
        {
            public readonly ManualResetEventSlim Started=new ManualResetEventSlim(),Release=new ManualResetEventSlim(),Completed=new ManualResetEventSlim();
            public volatile bool Cancelled;public int Calls;private readonly bool _hold;
            public readonly ReinforcementPlan ZonePlan=new ReinforcementPlan(),BackgroundPlan=new ReinforcementPlan();
            public ReinforcementPlan Plan;
            public CalculationGate(int count,bool hold)
            {
                _hold=hold;Plan=ZonePlan;
                for(int i=0;i<count;i++){ZonePlan.Bars.Add(new PlannedBar{Start=new Point2(1000,1000+i*200),End=new Point2(8000,1000+i*200),SpecKey="synthetic",Placement=new BarPlacement{Component="Zones",ModelDiameterMm=12,ElevationMm=-50}});}
                ZonePlan.Groups.Add(new BarGroup{Count=count,Start=new Point2(1000,1000),End=new Point2(8000,1000),SpacingMm=200,SpecKey="synthetic",Placement=new BarPlacement{Component="Zones"}});
                BackgroundPlan.Bars.Add(new PlannedBar{Start=new Point2(1000,2000),End=new Point2(8000,2000),SpecKey="background",PreviewPurpose=PreviewPurpose.Background,Placement=new BarPlacement{Component="Background"}});
                BackgroundPlan.Bars.Add(new PlannedBar{Start=new Point2(1000,2500),End=new Point2(1500,2500),SpecKey="main-u",PreviewPurpose=PreviewPurpose.Background,Placement=new BarPlacement{Component="PerimeterU"}});
                BackgroundPlan.Groups.Add(new BarGroup{Count=1,SpecKey="background",PreviewPurpose=PreviewPurpose.Background,Placement=new BarPlacement{Component="Background"}});
                BackgroundPlan.Groups.Add(new BarGroup{Count=1,SpecKey="main-u",PreviewPurpose=PreviewPurpose.Background,Placement=new BarPlacement{Component="PerimeterU"}});
                ZonePlan.Bars.Add(new PlannedBar{Start=new Point2(1000,2700),End=new Point2(1500,2700),SpecKey="local-u",PreviewPurpose=PreviewPurpose.Zones,Placement=new BarPlacement{Component="PerimeterU"}});
            }
            public ReinforcementPlan Calculate(object parameters,CancellationToken token)
            {
                Interlocked.Increment(ref Calls);
                var requestedScope=Get(parameters,"CreationScope").ToString();
                Require(requestedScope=="Background" || requestedScope=="Zones","Dialog used the legacy combined creation scope.");
                var response=requestedScope=="Background"?BackgroundPlan:ZonePlan;Plan=response;
                Require((double)Get(parameters,"LayerOffsetMm")==0,"Preview capture must use zero manual zone offset throughout the underlay lifecycle.");
                Started.Set();using(token.Register(()=>Cancelled=true))
                {if(_hold&&!Release.Wait(TimeSpan.FromSeconds(25)))throw new TimeoutException("Held stale worker was not released.");}
                Completed.Set();return response;
            }
        }
        private sealed class BindingErrors:TraceListener
        {public readonly List<string> Messages=new List<string>();public override void Write(string message){if(!string.IsNullOrWhiteSpace(message))Messages.Add(message);}public override void WriteLine(string message)=>Write(message);}
        private static T Control<T>(Window window,string name) where T:class => window.FindName(name) as T??throw new InvalidOperationException("Missing control "+name);
        private static object Get(object target,string name)=>target.GetType().GetProperty(name).GetValue(target);
        private static void Set(object target,string name,object value)=>target.GetType().GetProperty(name).SetValue(target,value);
        private static object GetField(object target,string name)=>target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(target);
        private static bool BoolField(object target,string name)=>(bool)GetField(target,name);
        private static object Call(object target,string name,params object[] args)=>target.GetType().GetMethod(name).Invoke(target,args);
        private static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
        private void Check(string name,object result){_checks.Add(new{name,status="PASS",result});Log(name+" PASS");}
        private void Log(string line)=>File.AppendAllText(Path.Combine(_run,"run.log"),DateTime.UtcNow.ToString("O")+" "+line+Environment.NewLine);
        private static string Hash(string file){if(!File.Exists(file))return "MISSING";using(var sha=SHA256.Create())using(var stream=File.OpenRead(file))return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","");}
    }
}
