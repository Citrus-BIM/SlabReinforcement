using System;
using System.Diagnostics;
using System.IO;
using System.Web.Script.Serialization;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace SlabReinforcement.PluginUiBootstrap
{
    // This application opens an existing test copy only. The separate manifest command
    // points directly to the installed product DLL; no product call or model write occurs here.
    public sealed class BootstrapApplication : IExternalApplication
    {
        private string _run;
        public Result OnStartup(UIControlledApplication app)
        {
            _run=Path.GetDirectoryName(typeof(BootstrapApplication).Assembly.Location);
            app.DialogBoxShowing+=OnDialog;app.Idling+=Open;
            Log("STARTUP "+app.ControlledApplication.VersionNumber+" PID="+Process.GetCurrentProcess().Id);
            return Result.Succeeded;
        }
        public Result OnShutdown(UIControlledApplication app){Log("SHUTDOWN");return Result.Succeeded;}
        private void OnDialog(object sender,DialogBoxShowingEventArgs e)=>Log("DIALOG "+e.DialogId+" "+(e is TaskDialogShowingEventArgs task?task.Message:""));
        private void Open(object sender,IdlingEventArgs e)
        {
            var ui=(UIApplication)sender;ui.Idling-=Open;
            try
            {
                var model=File.ReadAllText(Path.Combine(_run,"model-path.txt")).Trim();
                Log("OPEN_AND_ACTIVATE "+model);
                var active=ui.OpenAndActivateDocument(model);var doc=active.Document;
                var ready=new{status="READY_FOR_REAL_PLUGIN_UI",processId=Process.GetCurrentProcess().Id,model=doc.PathName,title=doc.Title,activeView=doc.ActiveView.Name,
                    floors=new FilteredElementCollector(doc).OfClass(typeof(Floor)).GetElementCount(),
                    rebar=new FilteredElementCollector(doc).OfClass(typeof(Rebar)).GetElementCount(),
                    areas=new FilteredElementCollector(doc).OfClass(typeof(AreaReinforcement)).GetElementCount(),
                    paths=new FilteredElementCollector(doc).OfClass(typeof(PathReinforcement)).GetElementCount(),
                    rebarInSystem=new FilteredElementCollector(doc).OfClass(typeof(RebarInSystem)).GetElementCount(),
                    modified=doc.IsModified,productCommandExecuted=false,modelWriteTransactions=0,readyUtc=DateTime.UtcNow.ToString("O")};
                File.WriteAllText(Path.Combine(_run,"ready.json"),new JavaScriptSerializer().Serialize(ready));
                Log("READY; process remains open for actual product command through Revit UI.");
            }
            catch(Exception error)
            {
                File.WriteAllText(Path.Combine(_run,"failed.txt"),error.ToString());Log("FAILED "+error);
            }
        }
        private void Log(string text)=>File.AppendAllText(Path.Combine(_run,"bootstrap.log"),DateTime.UtcNow.ToString("O")+" "+text+Environment.NewLine);
    }
}
