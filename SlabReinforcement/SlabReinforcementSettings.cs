using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using System.Xml.Serialization;

namespace SlabReinforcement
{
    public class SlabReinforcementSettings
    {
        public string ConcreteClass { get; set; } = "B20";
        // Retained so existing XML and callers continue to load.
        public string AdjacentElementsTolerance { get; set; } = "50";
        public string ZoneMergeTolerance { get; set; } = "600";
        public string EdgeCoverMm { get; set; }
        public string LayerOffsetMm { get; set; } = "0";
        public string SelectedReinforcementDirection { get; set; } = "Низ X";
        public bool UseCutLengths { get; set; }
        // Retained for old XML; intersections no longer require a user toggle.
        public bool AllowBarIntersections { get; set; } = true;
        public string RoundIncrement { get; set; } = "10";
        public List<ColorReinforcementSettings> ColorSettings { get; set; } = new List<ColorReinforcementSettings>();
        public string DemandMeaning { get; set; } = "Manual";
        public bool AutomaticZoneEnds { get; set; } = true;
        public List<BackgroundLayerSettings> BackgroundLayers { get; set; } = new List<BackgroundLayerSettings>();
        public PerimeterReinforcementSettings Perimeter { get; set; } = new PerimeterReinforcementSettings();

        [XmlIgnore]
        public string Warning { get; private set; }

        public static string GetSettingsFilePath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CITRUS", "SlabReinforcement", "settings.xml");
        }

        public static SlabReinforcementSettings LoadSettings()
        {
            try
            {
                string localPath = GetSettingsFilePath();
                string assemblyDirectory = Path.GetDirectoryName(typeof(SlabReinforcementSettings).Assembly.Location);
                string legacyPath = Path.Combine(assemblyDirectory ?? string.Empty, "SlabReinforcementSettings.xml");
                string path = File.Exists(localPath) ? localPath : legacyPath;
                if (!File.Exists(path)) return new SlabReinforcementSettings();

                SlabReinforcementSettings result;
                using (var stream = File.OpenRead(path))
                using (var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                    result = (SlabReinforcementSettings)new XmlSerializer(typeof(SlabReinforcementSettings)).Deserialize(reader);

                result = result ?? new SlabReinforcementSettings();
                result.ColorSettings = result.ColorSettings ?? new List<ColorReinforcementSettings>();
                result.BackgroundLayers = result.BackgroundLayers ?? new List<BackgroundLayerSettings>();
                result.Perimeter = result.Perimeter ?? new PerimeterReinforcementSettings();
                // Older MVP settings may enable longitudinal contours; the selected MVP scope is U-bars only.
                result.Perimeter.UseUbars = true;
                result.Perimeter.UseLongitudinalBars = false;
                result.AllowBarIntersections = true;
                if (path == legacyPath && !result.TrySaveSettings(out string migrationWarning))
                    result.Warning = "Загружены прежние настройки. " + migrationWarning;
                return result;
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException ||
                exception is InvalidOperationException || exception is System.Security.SecurityException || exception is XmlException)
            {
                return new SlabReinforcementSettings
                {
                    Warning = "Не удалось загрузить настройки. Проверьте параметры: выбраны значения по умолчанию."
                };
            }
        }

        public bool TrySaveSettings(out string warning)
        {
            string temporaryPath = null;
            try
            {
                string path = GetSettingsFilePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    new XmlSerializer(typeof(SlabReinforcementSettings)).Serialize(stream, this);
                    stream.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temporaryPath, path, null);
                else File.Move(temporaryPath, path);
                warning = null;
                Warning = null;
                return true;
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException ||
                exception is InvalidOperationException || exception is System.Security.SecurityException)
            {
                warning = "Настройки не сохранены. При следующем запуске задайте параметры заново.";
                Warning = warning;
                return false;
            }
            finally
            {
                if (temporaryPath != null)
                {
                    try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                    catch (System.Security.SecurityException) { }
                }
            }
        }

        public void SaveSettings()
        {
            TrySaveSettings(out _);
        }
    }
}
