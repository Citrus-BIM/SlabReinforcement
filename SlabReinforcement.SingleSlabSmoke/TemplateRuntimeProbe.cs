using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using SlabReinforcement.Integration;

namespace SlabReinforcement.SingleSlabSmoke
{
    // Loads the built product assembly and its BAML; no production UI source is linked here.
    internal static class TemplateRuntimeProbe
    {
        public static object Run(string runDirectory, SlabGeometrySnapshot slab, List<RebarBarType> rebarTypes)
        {
            string workspace = Path.GetFullPath(Path.Combine(runDirectory, "..", "..", ".."));
            string productPath = Path.Combine(workspace, "SlabReinforcement", "bin", "R2023", "SlabReinforcement.dll");
            if (!File.Exists(productPath)) throw new FileNotFoundException("The actual R2023 product DLL is missing.", productPath);
            var product = Assembly.LoadFrom(productPath);
            if (!string.Equals(Path.GetFullPath(product.Location), productPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("A different product assembly was already loaded.");
            var dependencies = new[] { typeof(SlabReinforcement.Core.PlannedBar).Assembly, typeof(SlabReinforcement.Dxf.DxfFaceSource).Assembly, typeof(NetTopologySuite.Geometries.Geometry).Assembly };
            foreach (var dependency in dependencies)
                if (Fingerprint(dependency.Location) != Fingerprint(Path.Combine(Path.GetDirectoryName(productPath), Path.GetFileName(dependency.Location))))
                    throw new InvalidOperationException("Loaded dependency differs from the product distribution: " + dependency.GetName().Name);
            var allTypes = product.GetTypes();
            var commandType = product.GetType("SlabReinforcement.SlabReinforcementCommand", true);
            if (!typeof(IExternalCommand).IsAssignableFrom(commandType)) throw new InvalidOperationException("Product entrypoint is not an IExternalCommand.");
            var command = Activator.CreateInstance(commandType);
            if (command == null) throw new InvalidOperationException("Cannot construct the product command entrypoint.");

            var settingsType = product.GetType("SlabReinforcement.SlabReinforcementSettings", true);
            string settingsPath = (string)settingsType.GetMethod("GetSettingsFilePath").Invoke(null, null);
            string legacyPath = Path.Combine(Path.GetDirectoryName(productPath), "SlabReinforcementSettings.xml");
            if (!File.Exists(settingsPath) && File.Exists(legacyPath))
                throw new InvalidOperationException("Runtime probe refuses to trigger legacy settings migration.");
            string settingsBefore = Fingerprint(settingsPath);
            string legacyBefore = Fingerprint(legacyPath);

            var contextType = product.GetType("SlabReinforcement.PreviewContext", true);
            var context = Activator.CreateInstance(contextType);
            Set(context, "HostName", slab.Host.Name + " · Revit runtime probe");
            Set(context, "AxisDescription", slab.AxisDescription);
            Set(context, "Slabs", slab.Slabs);
            Set(context, "TopCoverMm", slab.TopCoverMm);
            Set(context, "BottomCoverMm", slab.BottomCoverMm);
            Set(context, "SideCoverMm", slab.SideCoverMm);
            Set(context, "RebarTypeKeys", rebarTypes.GroupBy(t => t.Name).ToDictionary(g => g.Key, g => g.First().UniqueId));
            // Calculate remains null and every component is disabled before Loaded.
            var colors = Activator.CreateInstance(typeof(List<>).MakeGenericType(product.GetType("ColorStat", true)));
            var windowType = product.GetType("SlabReinforcement.SlabReinforcementWPF", true);
            Window window = null;
            var binding = new BindingErrors();
            var bindingSource = PresentationTraceSources.DataBindingSource;
            var oldLevel = bindingSource.Switch.Level;
            bindingSource.Listeners.Add(binding);
            bindingSource.Switch.Level = SourceLevels.Error;
            bool loaded = false;
            int before = new Autodesk.Revit.DB.FilteredElementCollector(slab.Host.Document).OfClass(typeof(Rebar)).GetElementCount();
            var images = new List<string>();
            string status = null;
            bool zoneEndOptionTransitionsChecked=false;
            bool perimeterCreationToggleChecked=false;
            try
            {
                window = (Window)Activator.CreateInstance(windowType, new[] { colors, rebarTypes, context });
                var zoneEndOption=(CheckBox)window.FindName("CheckBox_ZoneEndsInPerimeter");
                if(zoneEndOption==null||zoneEndOption.IsChecked!=false)throw new InvalidOperationException("Shared U-end option must start unchecked for each operation. "+OptionState(window));
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = -32000;
                window.Top = -32000;
                window.ShowActivated = false;
                window.ShowInTaskbar = false;
                window.Width = 1280;
                window.Height = 850;
                ((CheckBox)window.FindName("CheckBox_IncludeZones")).IsChecked = false;
                ((CheckBox)window.FindName("CheckBox_PerimeterEnabled")).IsChecked = false;
                foreach (object row in ((DataGrid)window.FindName("BackgroundDataGrid")).ItemsSource)
                    Set(row, "Enabled", false);
                window.Loaded += (sender, args) => loaded = true;
                window.Show();
                foreach (string tab in new[] { "BackgroundTab", "ZonesTab" })
                {
                    ((TabControl)window.FindName("ComponentsTabs")).SelectedItem = window.FindName(tab);
                    window.Dispatcher.Invoke(DispatcherPriority.ContextIdle, new Action(window.UpdateLayout));
                    if (!loaded || !window.IsLoaded) throw new InvalidOperationException("The actual product WPF window did not reach Loaded.");
                    if (((Button)window.FindName("Button_Confirm")).IsEnabled) throw new InvalidOperationException("Creation must stay disabled during the runtime probe.");
                    string image = Path.Combine(runDirectory, "MVP-product-runtime-" + tab + ".png");
                    RenderWindow(window,image);
                    images.Add(image);
                }
                var demandMeaning=(System.Windows.Controls.ComboBox)window.FindName("ComboBox_DemandMeaning");
                demandMeaning.SelectedValue="Manual";
                window.Dispatcher.Invoke(DispatcherPriority.ContextIdle,new Action(window.UpdateLayout));
                if(!zoneEndOption.IsEnabled||zoneEndOption.IsChecked!=false)throw new InvalidOperationException("Manual mode must offer an unchecked shared U-end option. "+OptionState(window));
                zoneEndOption.IsChecked=true;
                window.Dispatcher.Invoke(DispatcherPriority.ContextIdle,new Action(window.UpdateLayout));
                if(zoneEndOption.IsChecked!=true)throw new InvalidOperationException("Manual shared U-end option could not be selected. "+OptionState(window));
                demandMeaning.SelectedValue="AdditionalArea";
                window.Dispatcher.Invoke(DispatcherPriority.ContextIdle,new Action(window.UpdateLayout));
                if(zoneEndOption.IsEnabled||zoneEndOption.IsChecked!=false)throw new InvalidOperationException("Numerical As mode must disable and clear the shared U-end option. "+OptionState(window));
                demandMeaning.SelectedValue="Manual";
                window.Dispatcher.Invoke(DispatcherPriority.ContextIdle,new Action(window.UpdateLayout));
                if(!zoneEndOption.IsEnabled||zoneEndOption.IsChecked!=false)throw new InvalidOperationException("Returning to Manual must require a fresh explicit choice. "+OptionState(window));
                zoneEndOptionTransitionsChecked=true;
                var createPerimeter=(CheckBox)window.FindName("CheckBox_PerimeterEnabled");
                var zoneCreatePerimeter=(CheckBox)window.FindName("CheckBox_ZoneCreatePerimeter");
                var perimeterType=(System.Windows.Controls.ComboBox)window.FindName("ComboBox_PerimeterType");
                var perimeterSpacing=(System.Windows.Controls.TextBox)window.FindName("TextBox_PerimeterSpacing");
                var perimeterLeg=(System.Windows.Controls.TextBox)window.FindName("TextBox_PerimeterLeg");
                if(zoneCreatePerimeter==null)throw new InvalidOperationException("Zones tab must expose the common perimeter creation switch.");
                var retainedType=perimeterType.SelectedItem;string retainedSpacing=perimeterSpacing.Text,retainedLeg=perimeterLeg.Text;
                zoneCreatePerimeter.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,true);
                window.Dispatcher.Invoke(DispatcherPriority.ContextIdle,new Action(window.UpdateLayout));
                if(createPerimeter.IsChecked!=true)throw new InvalidOperationException("Zones create-perimeter switch did not update the common option.");
                zoneCreatePerimeter.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,false);
                zoneEndOption.IsChecked=true;
                window.Dispatcher.Invoke(DispatcherPriority.ContextIdle,new Action(window.UpdateLayout));
                if(createPerimeter.IsChecked!=false||zoneEndOption.IsChecked!=true||!zoneEndOption.IsEnabled)
                    throw new InvalidOperationException("Manual existing-perimeter use must remain selectable with creation off.");
                if(!perimeterType.IsEnabled||!perimeterSpacing.IsEnabled||!perimeterLeg.IsEnabled
                    ||!ReferenceEquals(retainedType,perimeterType.SelectedItem)||retainedSpacing!=perimeterSpacing.Text||retainedLeg!=perimeterLeg.Text)
                    throw new InvalidOperationException("Disabling creation must preserve editable perimeter reference parameters.");
                perimeterCreationToggleChecked=true;
                ((Expander)window.FindName("SettingsExpander")).IsExpanded=true;
                window.Dispatcher.Invoke(DispatcherPriority.ContextIdle,new Action(window.UpdateLayout));
                if(((Button)window.FindName("Button_Confirm")).IsEnabled)throw new InvalidOperationException("UI mode toggles unexpectedly enabled model creation.");
                string settingsImage=Path.Combine(runDirectory,"MVP-product-runtime-ZonesSettings.png");
                RenderWindow(window,settingsImage);images.Add(settingsImage);
                status = ((TextBlock)window.FindName("StatusText")).Text;
                window.Close();
                if (windowType.GetProperty("AcceptedPlan").GetValue(window) != null || windowType.GetProperty("AcceptedParameters").GetValue(window) != null)
                    throw new InvalidOperationException("Runtime probe unexpectedly accepted a plan.");
                if (binding.Messages.Count > 0) throw new InvalidOperationException("Product WPF binding errors: " + string.Join(Environment.NewLine, binding.Messages));
            }
            finally
            {
                if (window != null && window.IsVisible) window.Close();
                bindingSource.Listeners.Remove(binding);
                bindingSource.Switch.Level = oldLevel;
            }
            if (settingsBefore != Fingerprint(settingsPath) || legacyBefore != Fingerprint(legacyPath))
                throw new InvalidOperationException("Runtime probe changed settings.");
            int after = new Autodesk.Revit.DB.FilteredElementCollector(slab.Host.Document).OfClass(typeof(Rebar)).GetElementCount();
            if (before != after) throw new InvalidOperationException("Runtime probe changed the rebar count.");
            return new
            {
                productPath, productSha256 = Fingerprint(productPath), loadedTypes = allTypes.Length,
                entrypoint = commandType.FullName, entrypointConstructed = true,
                windowType = windowType.FullName, framework = product.ImageRuntimeVersion,
                targetFramework = product.GetCustomAttributes(typeof(TargetFrameworkAttribute), false).Cast<TargetFrameworkAttribute>().FirstOrDefault()?.FrameworkName,
                moduleVersionId = product.ManifestModule.ModuleVersionId.ToString(), productDependenciesMatch = true,
                realRebarTypes = rebarTypes.Count, loaded, tabsRendered = 2, settingsPanelRendered=true, images,
                zoneEndOptionTransitionsChecked,zoneEndOptionDefaultFalse=true,manualSelectionAllowed=true,numericAsClearsAndDisables=true,returnToManualRequiresExplicitChoice=true,
                perimeterCreationToggleChecked,existingPerimeterUseWithCreationOff=true,perimeterReferenceParametersRetained=true,
                bindingErrors = binding.Messages.Count, settingsUnchanged = true, rebarCountUnchanged = true,
                statusText = status, scope = "ACTUAL PRODUCT DLL/BAML RUNTIME; NO COMMAND EXECUTE OR INTERACTIVE PICK/CREATE"
            };
        }

        private static string OptionState(Window window)
        {
            var choice=(System.Windows.Controls.ComboBox)window.FindName("ComboBox_DemandMeaning");
            var option=(CheckBox)window.FindName("CheckBox_ZoneEndsInPerimeter");
            var key=choice?.SelectedItem?.GetType().GetProperty("Key")?.GetValue(choice.SelectedItem);
            if(option==null)return "Checkbox missing; SelectedValue="+choice?.SelectedValue+"; SelectedItem.Key="+key;
            var source=DependencyPropertyHelper.GetValueSource(option,UIElement.IsEnabledProperty);
            return "SelectedValue="+choice?.SelectedValue+"; SelectedItem.Key="+key+"; SelectedIndex="+choice?.SelectedIndex
                +"; IsChecked="+option.IsChecked+"; IsEnabled="+option.IsEnabled+"; BaseValueSource="+source.BaseValueSource
                +"; IsCoerced="+source.IsCoerced+"; ReadLocalValue="+option.ReadLocalValue(UIElement.IsEnabledProperty)
                +"; WindowEnabled="+window.IsEnabled+"; ComboEnabled="+choice?.IsEnabled;
        }

        private static void RenderWindow(Window window,string filename)
        {
            var content=(FrameworkElement)window.Content;
            if(content.ActualWidth<500||content.ActualHeight<400)throw new InvalidOperationException("Product window did not lay out.");
            var bitmap=new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth),(int)Math.Ceiling(content.ActualHeight),96,96,PixelFormats.Pbgra32);
            var surface=new DrawingVisual();
            using(var drawing=surface.RenderOpen())
            {
                var bounds=new Rect(0,0,content.ActualWidth,content.ActualHeight);
                drawing.DrawRectangle(window.Background??Brushes.White,null,bounds);
                drawing.DrawRectangle(new VisualBrush(content){Stretch=Stretch.Fill},null,bounds);
            }
            bitmap.Render(surface);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using(var stream=File.Create(filename))encoder.Save(stream);
        }

        private static void Set(object target, string name, object value) => target.GetType().GetProperty(name).SetValue(target, value);
        private static string Fingerprint(string path)
        {
            if (!File.Exists(path)) return "MISSING";
            using (var hash = SHA256.Create())
            using (var stream = File.OpenRead(path)) return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", string.Empty);
        }
        private sealed class BindingErrors : TraceListener
        {
            public List<string> Messages { get; } = new List<string>();
            public override void Write(string message) { if (!string.IsNullOrWhiteSpace(message)) Messages.Add(message); }
            public override void WriteLine(string message) { if (!string.IsNullOrWhiteSpace(message)) Messages.Add(message); }
        }
    }
}
