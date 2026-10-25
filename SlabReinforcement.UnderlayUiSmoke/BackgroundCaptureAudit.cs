using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using Autodesk.Revit.DB.Structure;
using SlabReinforcement.Core;
using WpfCheckBox=System.Windows.Controls.CheckBox;
using WpfComboBox=System.Windows.Controls.ComboBox;
using WpfTextBox=System.Windows.Controls.TextBox;
using WpfTextBlock=System.Windows.Controls.TextBlock;
using WpfDataGrid=System.Windows.Controls.DataGrid;
using WpfRadioButton=System.Windows.Controls.RadioButton;

namespace SlabReinforcement.UnderlayUiSmoke
{
    public sealed partial class UnderlayApplication
    {
        private void RunBackgroundCaptureAudit(List<RebarBarType> types)
        {
            string workspace=Path.GetFullPath(Path.Combine(_run,"..","..",".."));
            string path=Path.Combine(workspace,"SlabReinforcement","bin","R2023","SlabReinforcement.dll");
            var assembly=Assembly.LoadFrom(path);
            _report["product"]=new{path,sha256=Hash(path)};
            _report["scope"]="READ-ONLY AUDIT: actual product TryCaptureParameters and WPF status; mocked preview, no model writes or production fixes";
            string settings=(string)assembly.GetType("SlabReinforcement.SlabReinforcementSettings",true).GetMethod("GetSettingsFilePath").Invoke(null,null);
            string legacy=Path.Combine(Path.GetDirectoryName(path),"SlabReinforcementSettings.xml");
            Require(File.Exists(settings)||!File.Exists(legacy),"Probe refuses settings migration.");
            string settingsHash=Hash(settings),legacyHash=Hash(legacy);
            var gate=new CalculationGate(0,false);gate.Plan.Groups.Clear();
            gate.Plan.ReferenceBars.Add(new PlannedBar{Start=new Point2(1000,1000),End=new Point2(8000,1000),Placement=new BarPlacement{Component="Background",ModelDiameterMm=12,ElevationMm=-50}});
            gate.Plan.ReferenceBars.Add(new PlannedBar{Start=new Point2(1000,1200),End=new Point2(8000,1200),Placement=new BarPlacement{Component="Background",ModelDiameterMm=12,ElevationMm=-50}});
            gate.Plan.Diagnostics.Add(new PlanDiagnostic{Code="EMPTY_PLAN",Message="Заданный фон и обрамление уже существуют. Новых стержней нет.",IsBlocking=true});
            Window window=null;
            try
            {
                window=(Window)Activator.CreateInstance(assembly.GetType("SlabReinforcement.SlabReinforcementWPF",true),new[]{Colors(assembly),types,Context(assembly,types,gate,null)});
                window.WindowStartupLocation=WindowStartupLocation.Manual;window.Left=-32000;window.Top=-32000;
                window.Width=1280;window.Height=850;window.ShowActivated=false;window.ShowInTaskbar=false;
                Control<WpfCheckBox>(window,"CheckBox_IncludeZones").IsChecked=false;
                Control<WpfCheckBox>(window,"CheckBox_PerimeterEnabled").IsChecked=false;
                Control<WpfCheckBox>(window,"CheckBox_ZoneEndsInPerimeter").IsChecked=false;
                Control<WpfComboBox>(window,"ComboBox_ConcreteClass").SelectedItem="B25";
                Control<WpfComboBox>(window,"ComboBox_Direction").SelectedItem="Низ X";
                Control<WpfTextBox>(window,"TextBox_EdgeCover").Text="35";
                Control<WpfTextBox>(window,"TextBox_ZoneMergeTolerance").Text="0";
                Require(window.FindName("TextBox_LayerOffset")==null,"The removed zone-offset editor must not be exposed.");
                Control<WpfTextBox>(window,"TextBox_RoundIncrement").Text="10";
                Control<WpfRadioButton>(window,"RadioButton_RoundIncrement").IsChecked=true;
                var rows=((IEnumerable)Control<WpfDataGrid>(window,"BackgroundDataGrid").ItemsSource).Cast<object>().ToList();
                foreach(object row in rows){Set(row,"Enabled",false);Set(row,"SpacingText","200");Set(row,"SelectedRebarType",types[0]);}
                Set(rows[0],"Enabled",true);
                bool capturesDone=false;
                Modal(window,()=>
                {
                    if(!window.IsLoaded)return false;
                    if(!capturesDone)
                    {
                        var baseline=Capture(window);Require(baseline.Valid&&baseline.Problems.Count==0,"Valid background control unexpectedly failed.");
                        Check("background-valid-control",baseline);
                        CaptureInvalidZoneField(window,"TextBox_ZoneMergeTolerance","background-invalid-zone-merge","Допуск объединения должен быть неотрицательным числом.");
                        Check("zone-offset-editor-removed",new{editorAbsent=window.FindName("TextBox_LayerOffset")==null,capturedOffsetMm=baseline.LayerOffsetMm});
                        CaptureInvalidZoneField(window,"TextBox_RoundIncrement","background-invalid-zone-rounding","Кратность округления должна быть больше нуля.");
                        Control<WpfTextBox>(window,"TextBox_RoundIncrement").Text="bad";
                        Control<WpfRadioButton>(window,"RadioButton_RoundCutLengths").IsChecked=true;
                        var cuts=Capture(window);Require(cuts.Valid&&cuts.Problems.Count==0,"Cut-length control did not bypass irrelevant rounding text.");
                        Check("background-invalid-rounding-with-cut-lengths-control",cuts);
                        Control<WpfTextBox>(window,"TextBox_RoundIncrement").Text="10";
                        Control<WpfRadioButton>(window,"RadioButton_RoundIncrement").IsChecked=true;
                        capturesDone=true;
                    }
                    return gate.Completed.IsSet&&Control<WpfTextBlock>(window,"PlanSummary").Text.Contains("Существующих: 2");
                },()=>
                {
                    string summary=Control<WpfTextBlock>(window,"PlanSummary").Text;
                    string status=Control<WpfTextBlock>(window,"StatusText").Text;
                    bool createEnabled=Control<System.Windows.Controls.Button>(window,"Button_Confirm").IsEnabled;
                    Require(!createEnabled,"Reference-only mock must not enable writing.");
                    Render(window,"reference-only-status.png");
                    Check("reference-only-plan-ui-presentation",new{summary,status,createEnabled,newBars=0,referenceBars=2,
                        suppliedDiagnostic=gate.Plan.Diagnostics[0],scope="UI presentation only; reference-only plan is mocked, not created in the model"});
                    window.Close();
                });
            }
            finally{if(window!=null&&window.IsVisible)window.Close();}
            Require(Hash(settings)==settingsHash&&Hash(legacy)==legacyHash,"Audit changed saved settings.");
            Check("background-audit-settings-unchanged",new{settingsUnchanged=true});
            _report["finding"]=new{reproduced=true,severity="P2",description="Zone-only merge/rounding text blocks background-only creation even when IncludeZones=false; rounding exemption depends on UseCutLengths. Manual zone offset has no editor and is captured as zero."};
        }
        private void CaptureInvalidZoneField(Window window,string editor,string name,string diagnostic)
        {
            var control=Control<WpfTextBox>(window,editor);string prior=control.Text;control.Text="bad";
            var capture=Capture(window);
            Require(!capture.Valid&&capture.Problems.SequenceEqual(new[]{diagnostic}),"Unexpected validation result for "+editor+": "+string.Join(";",capture.Problems));
            Render(window,name+".png");Check(name,new{capture,editor,invalidText="bad",actualStatus=Control<WpfTextBlock>(window,"StatusText").Text});
            control.Text=prior;
        }
        private sealed class CaptureEvidence
        {
            public bool Valid{get;set;}public bool IncludeZones{get;set;}public bool PerimeterEnabled{get;set;}
            public int EnabledBackgroundLayers{get;set;}public bool UseCutLengths{get;set;}public double LayerOffsetMm{get;set;}public List<string> Problems{get;set;}
        }
        private static CaptureEvidence Capture(Window window)
        {
            object[] arguments={null,null};bool valid=(bool)window.GetType().GetMethod("TryCaptureParameters",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(window,arguments);
            object parameters=arguments[0];var evidence=new CaptureEvidence{Valid=valid,IncludeZones=(bool)Get(parameters,"IncludeZones"),
                PerimeterEnabled=(bool)Get(Get(parameters,"Perimeter"),"Enabled"),UseCutLengths=(bool)Get(parameters,"UseCutLengths"),
                LayerOffsetMm=(double)Get(parameters,"LayerOffsetMm"),
                EnabledBackgroundLayers=((IEnumerable)Get(parameters,"BackgroundLayers")).Cast<object>().Count(row=>(bool)Get(row,"Enabled")),Problems=(List<string>)arguments[1]};
            Require(window.FindName("TextBox_LayerOffset")==null&&evidence.LayerOffsetMm==0,"Manual zone offset must have no editor and must capture as zero.");
            Require(!evidence.IncludeZones&&!evidence.PerimeterEnabled&&evidence.EnabledBackgroundLayers==1,"Capture fixture is not background-only.");return evidence;
        }
    }
}
