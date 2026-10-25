using Autodesk.Revit.DB.Structure;
using SlabReinforcement.Core;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SlabReinforcement
{
    public partial class SlabReinforcementWPF : Window
    {
        private PreviewContext _context;
        private readonly SlabReinforcementSettings _settings;
        private readonly List<ColorStat> _allColors;
        private readonly ObservableCollection<ColorStat> _colors;
        private readonly ObservableCollection<BackgroundLayerStat> _backgroundLayers = new ObservableCollection<BackgroundLayerStat>();
        private bool _hasCadSource;
        private bool _selectingUnderlay;
        private readonly Dictionary<RebarBarType, string> _typeNames = new Dictionary<RebarBarType, string>();
        private readonly List<string> _warnings = new List<string>();
        private readonly List<string> _planDetails = new List<string>();
        private static readonly HashSet<string> DetailDiagnosticCodes = new HashSet<string>(StringComparer.Ordinal)
        {
            "BACKGROUND_REUSED", "PERIMETER_U_LENGTH", "ZONE_U_LENGTH", "ZONE_ENDS_REUSED",
            "PERIMETER_REUSED", "PERIMETER_MANUAL_COMPLETION", "ZONE_EDGE_EXACT_LENGTH",
            "SUPPLEMENTAL_ROWS_CREATED", "ROWS_EXTENDED_FOR_COVERAGE"
        };
        private CancellationTokenSource _calculation;
        private int _revision;
        private int _readyRevision = -1;
        private bool _initialized;
        private bool _closed;
        private string _manualPerimeterSpacingText = "200";
        private bool _updatingPerimeterSpacing;
        private bool _automaticZoneEndsPreference = true;
        private bool _updatingZoneEnds;
        private ReinforcementPlan _readyPlan;
        private PreviewParameters _readyParameters;
        private List<ColorStat> _selectedColors;

        private sealed class SourceGroupChoice
        {
            public CadPlaneGroup Group { get; set; }
            public string DisplayName { get; set; }
        }

        private sealed class DemandMeaningChoice
        {
            public string Key { get; set; }
            public string DisplayName { get; set; }
        }

        // Compatibility with the command's previous public contract.
        public string ConcreteClass;
        public string AdjacentElementsTolerance;
        public string ZoneMergeTolerance;
        public string SelectedReinforcementDirection;
        public bool UseCutLengths;
        public string RoundIncrement;
        public ReinforcementPlan AcceptedPlan { get; private set; }
        public PreviewParameters AcceptedParameters { get; private set; }
        public bool UnderlaySelectionRequested { get; private set; }

        public SlabReinforcementWPF(List<ColorStat> colorStats, List<RebarBarType> availableRebarTypes)
            : this(colorStats, availableRebarTypes, new PreviewContext()) { }

        public SlabReinforcementWPF(List<ColorStat> colorStats, List<RebarBarType> availableRebarTypes, PreviewContext context)
            : this(colorStats, availableRebarTypes, context, SlabReinforcementSettings.LoadSettings()) { }

        internal SlabReinforcementWPF(List<ColorStat> colorStats, List<RebarBarType> availableRebarTypes,
            PreviewContext context, SlabReinforcementSettings settings)
        {
            _context = context ?? new PreviewContext();
            _hasCadSource = (_context.SourceGroups?.Count ?? 0) > 0 || (_context.Faces?.Count ?? 0) > 0;
            _settings = settings ?? new SlabReinforcementSettings();
            _automaticZoneEndsPreference = _settings.AutomaticZoneEnds;
            InitializeComponent();
            // Size only the initial window to the usable desktop; do not constrain later resizing.
            Rect workArea = SystemParameters.WorkArea;
            Width = Math.Max(MinWidth, Math.Min(Width, workArea.Width));
            Height = Math.Max(MinHeight, Math.Min(Height, workArea.Height));
            if (!string.IsNullOrWhiteSpace(_settings.Warning)) _warnings.Add(_settings.Warning);
            if (_context.Warnings != null) _warnings.AddRange(_context.Warnings.Where(x => !string.IsNullOrWhiteSpace(x)));
            HostDescription.Text = string.IsNullOrWhiteSpace(_context.HostName) ? "Армирование выбранной плиты" : _context.HostName;
            AxisDescription.Text = string.IsNullOrWhiteSpace(_context.AxisDescription) ? "Локальные оси плиты · все размеры в миллиметрах" : _context.AxisDescription;
            ComboBox_ConcreteClass.ItemsSource = new[] { "B15", "B20", "B25", "B30" };
            ComboBox_ConcreteClass.SelectedItem = _settings.ConcreteClass;
            if (ComboBox_ConcreteClass.SelectedItem == null)
            {
                ComboBox_ConcreteClass.SelectedItem = "B20";
                _warnings.Add("Сохранённый класс бетона не поддерживается. Выбран B20.");
            }
            ComboBox_Direction.ItemsSource = new[] { "Низ X", "Низ Y", "Верх X", "Верх Y" };
            ComboBox_Direction.SelectedItem = _settings.SelectedReinforcementDirection;
            if (ComboBox_Direction.SelectedItem == null)
            {
                ComboBox_Direction.SelectedItem = "Низ X";
                _warnings.Add("Сохранённое направление не распознано. Выбрано «Низ X».");
            }
            // Cover belongs to the selected slab, never to a global user profile.
            TextBox_TopCover.Text = _context.TopCoverMm.ToString("G", CultureInfo.CurrentCulture);
            TextBox_BottomCover.Text = _context.BottomCoverMm.ToString("G", CultureInfo.CurrentCulture);
            TextBox_EdgeCover.Text = _context.SideCoverMm.ToString("G", CultureInfo.CurrentCulture);
            TextBox_ZoneMergeTolerance.Text = _settings.ZoneMergeTolerance;
            TextBox_RoundIncrement.Text = _settings.RoundIncrement;
            RadioButton_RoundIncrement.IsChecked = !_settings.UseCutLengths;
            RadioButton_RoundCutLengths.IsChecked = _settings.UseCutLengths;
            RefreshDetails();

            availableRebarTypes = availableRebarTypes ?? new List<RebarBarType>();
            // The only access to Revit type names happens on this UI thread.
            foreach (RebarBarType type in availableRebarTypes) _typeNames[type] = type.Name;
            foreach (string direction in new[] { "Низ X", "Низ Y", "Верх X", "Верх Y" })
            {
                BackgroundLayerSettings saved = _settings.BackgroundLayers.FirstOrDefault(item => item != null && item.Direction == direction);
                var layer = new BackgroundLayerStat
                {
                    Direction = direction,
                    Enabled = saved?.Enabled ?? false,
                    RebarTypes = availableRebarTypes,
                    SelectedRebarType = saved == null ? availableRebarTypes.FirstOrDefault() :
                        availableRebarTypes.FirstOrDefault(type => _typeNames[type] == saved.RebarTypeName),
                    SpacingText = saved?.SpacingMm ?? "200"
                };
                layer.PropertyChanged += Background_PropertyChanged;
                _backgroundLayers.Add(layer);
            }
            BackgroundDataGrid.ItemsSource = _backgroundLayers;
            PerimeterReinforcementSettings perimeter = _settings.Perimeter;
            CheckBox_PerimeterEnabled.IsChecked = perimeter.Enabled;
            CheckBox_PerimeterOuter.IsChecked = perimeter.Outer;
            CheckBox_PerimeterOpenings.IsChecked = perimeter.Openings;
            ComboBox_PerimeterType.ItemsSource = availableRebarTypes;
            ComboBox_PerimeterType.SelectedItem = string.IsNullOrWhiteSpace(perimeter.RebarTypeName) ? availableRebarTypes.FirstOrDefault() :
                availableRebarTypes.FirstOrDefault(type => _typeNames[type] == perimeter.RebarTypeName);
            _manualPerimeterSpacingText = perimeter.SpacingMm ?? "200";
            TextBox_PerimeterSpacing.Text = _manualPerimeterSpacingText;
            ComboBox_DemandMeaning.ItemsSource = new[]
            {
                new DemandMeaningChoice { Key = "Manual", DisplayName = "Ручное назначение" },
                new DemandMeaningChoice { Key = "AdditionalArea", DisplayName = "Дополнительная площадь, мм²/м" },
                new DemandMeaningChoice { Key = "TotalArea", DisplayName = "Полная площадь, мм²/м" }
            };
            ComboBox_DemandMeaning.SelectedValue = _settings.DemandMeaning;
            if (ComboBox_DemandMeaning.SelectedIndex < 0) ComboBox_DemandMeaning.SelectedIndex = 0;
            ZoneSettingsPanel.IsEnabled = _hasCadSource;
            ColorStatsDataGrid.IsEnabled = _hasCadSource;
            ComboBox_DemandMeaning.IsEnabled = _hasCadSource;
            UpdateZoneEndAvailability();
            ComponentsTabs.SelectedItem = _hasCadSource ? ZonesTab : BackgroundTab;
            _allColors = colorStats ?? new List<ColorStat>();
            foreach (ColorStat stat in _allColors)
            {
                stat.RebarTypes = availableRebarTypes;
                ColorReinforcementSettings saved = _settings.ColorSettings.FirstOrDefault(x => x != null && x.Color == stat.Color);
                if (saved != null)
                {
                    stat.SelectedRebarType = availableRebarTypes.FirstOrDefault(x => _typeNames[x] == saved.RebarTypeName);
                    stat.Spacing = saved.Spacing;
                    stat.IsSelected = saved.IsSelected;
                    stat.RequiredAreaText = saved.RequiredAreaMm2PerM ?? string.Empty;
                    // A missing saved type stays empty; creation requires an explicit replacement.
                }
                else
                {
                    stat.SelectedRebarType = availableRebarTypes.FirstOrDefault();
                    stat.Spacing = 200;
                }
                stat.PropertyChanged += Color_PropertyChanged;
            }
            _colors = new ObservableCollection<ColorStat>(_allColors);
            ColorStatsDataGrid.ItemsSource = _colors;
            if (_hasCadSource)
            {
                Button_SelectUnderlay.Content = "Заменить";
                UnderlayName.Text = "Подложка выбрана";
            }
            PopulateSourceGroups();
            _initialized = true;
            UpdatePreviewScope();
            UpdatePerimeterSpacingMode(null, null);
        }

        private void PopulateSourceGroups()
        {
            var sourceChoices = (_context.SourceGroups ?? new List<CadPlaneGroup>())
                .Where(group => group != null)
                .OrderByDescending(group => (group.Faces ?? new List<FaceInput>())
                    .Where(face => face != null).Select(face => face.ColorKey).Distinct(StringComparer.Ordinal).Count())
                .ThenByDescending(group => group.Faces?.Count ?? 0)
                .ThenBy(group => group.Layer, StringComparer.Ordinal)
                .ThenBy(group => group.ElevationMm)
                .ThenBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => new SourceGroupChoice
                {
                    Group = group,
                    DisplayName = string.Format(CultureInfo.CurrentCulture, "{0} · {1} {2:0.###} мм · элементов: {3:N0}",
                        string.IsNullOrWhiteSpace(group.Layer) ? "Без слоя" : group.Layer,
                        _context.SourceElevationLabel, group.ElevationMm, group.Faces?.Count ?? 0)
                }).ToList();
            ComboBox_SourceGroup.ItemsSource = sourceChoices;
            SourceGroupPanel.Visibility = sourceChoices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (sourceChoices.Count == 1) ComboBox_SourceGroup.SelectedIndex = 0;
            else if (sourceChoices.Count > 1) UpdateSourceGroup();
            else PreviewCanvas.SetContext(_context);
        }

        private void Button_SelectUnderlay_Click(object sender, RoutedEventArgs e) { RequestUnderlaySelection(); }

        public void RequestUnderlaySelection()
        {
            if (_closed || _selectingUnderlay) return;
            // Preserve this window and its editors, but leave the modal loop before Revit selection.
            _selectingUnderlay = true;
            UnderlaySelectionRequested = true;
            DiscardPreview();
            Hide();
        }

        public bool ConsumeUnderlaySelectionRequest()
        {
            bool requested = UnderlaySelectionRequested;
            UnderlaySelectionRequested = false;
            return requested;
        }

        public void ResumePreview()
        {
            _selectingUnderlay = false;
            InvalidatePlan();
        }

        public void ApplyUnderlay(List<ColorStat> colors, PreviewContext context, string sourceName)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if ((context.SourceGroups?.Count ?? 0) == 0 && (context.Faces?.Count ?? 0) == 0)
                throw new ArgumentException("В подложке нет расчётных зон.", nameof(context));
            if (colors == null || colors.Count == 0) throw new ArgumentException("В подложке нет цветов зон.", nameof(colors));
            DiscardPreview();
            _initialized = false;
            try
            {
                var previousColors = _allColors.ToDictionary(color => color.Color, StringComparer.Ordinal);
                foreach (ColorStat stat in _allColors) stat.PropertyChanged -= Color_PropertyChanged;
                _allColors.Clear();
                foreach (ColorStat stat in colors)
                {
                    stat.RebarTypes = _typeNames.Keys.ToList();
                    previousColors.TryGetValue(stat.Color, out ColorStat previous);
                    ColorReinforcementSettings saved = _settings.ColorSettings.FirstOrDefault(x => x != null && x.Color == stat.Color);
                    stat.SelectedRebarType = previous?.SelectedRebarType ??
                        (saved == null ? stat.RebarTypes.FirstOrDefault() : stat.RebarTypes.FirstOrDefault(type => _typeNames[type] == saved.RebarTypeName));
                    stat.SpacingText = previous?.SpacingText ?? (saved?.Spacing ?? 200).ToString("G", CultureInfo.CurrentCulture);
                    // RGB equality does not establish the demand of a replacement calculation map.
                    stat.IsSelected = false;
                    stat.RequiredAreaText = string.Empty;
                    stat.PropertyChanged += Color_PropertyChanged;
                    _allColors.Add(stat);
                }
                if (_context.Warnings != null) _warnings.RemoveAll(warning => _context.Warnings.Contains(warning));
                _context = context;
                if (_context.Warnings != null) _warnings.AddRange(_context.Warnings.Where(warning => !string.IsNullOrWhiteSpace(warning)));
                RefreshDetails();
                _hasCadSource = true;
                ZoneSettingsPanel.IsEnabled = true;
                ColorStatsDataGrid.IsEnabled = true;
                ComboBox_DemandMeaning.IsEnabled = true;
                UpdateZoneEndAvailability();
                _colors.Clear();
                foreach (ColorStat stat in _allColors) _colors.Add(stat);
                PopulateSourceGroups();
                Button_SelectUnderlay.Content = "Заменить";
                UnderlayName.Text = string.IsNullOrWhiteSpace(sourceName) ? "Подложка выбрана" : sourceName;
                ComponentsTabs.SelectedItem = ZonesTab;
                PreviewCanvas.Fit();
            }
            finally { _initialized = true; }
            InvalidatePlan();
        }

        private void SourceGroup_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateSourceGroup();
        }

        private void UpdateSourceGroup()
        {
            if (_colors == null) return;
            var choice = ComboBox_SourceGroup.SelectedItem as SourceGroupChoice;
            _context.Faces = choice?.Group.Faces ?? new List<FaceInput>();
            var visibleColors = new HashSet<string>(_context.Faces.Where(face => face != null)
                .Select(face => face.ColorKey), StringComparer.Ordinal);
            _colors.Clear();
            foreach (ColorStat color in _allColors.Where(color => visibleColors.Contains(color.Color)))
                _colors.Add(color);
            PreviewCanvas.SetContext(_context);
            InvalidatePlan();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            PreviewCanvas.Fit();
            ResumePreview();
        }

        private void ComponentsTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // SelectionChanged also bubbles from the type, direction and source editors.
            if (!ReferenceEquals(e.OriginalSource, ComponentsTabs)) return;
            UpdatePreviewScope();
            InvalidatePlan();
        }

        private void UpdatePreviewScope()
        {
            if (PreviewCanvas == null || PlanSummary == null || PreviewLegendBackground == null || PreviewLegendZones == null) return;
            bool zones = ReferenceEquals(ComponentsTabs.SelectedItem, ZonesTab);
            PreviewCanvas.SetScope(zones ? PreviewScope.Zones : PreviewScope.Background);
            PreviewLegendBackground.Visibility = zones ? Visibility.Collapsed : Visibility.Visible;
            PreviewLegendZones.Visibility = zones ? Visibility.Visible : Visibility.Collapsed;
            UpdatePreviewSummary();
        }

        private void UpdatePreviewSummary()
        {
            ReinforcementPlan displayed = PreviewCanvas.DisplayedPlan;
            if (displayed == null) { PlanSummary.Text = string.Empty; return; }
            int zoneSystems = displayed.Groups.Count(group => group.Placement?.Component == "Zones");
            PlanSummary.Text = string.Format(CultureInfo.CurrentCulture, "Новых стержней: {0:N0} · Наборов: {1:N0}", displayed.Bars.Count, displayed.Groups.Count - zoneSystems)
                + (zoneSystems > 0 ? string.Format(CultureInfo.CurrentCulture, " · Зон: {0:N0}", zoneSystems) : "")
                + (displayed.ReferenceBars.Count > 0 ? string.Format(CultureInfo.CurrentCulture, " · Учтено существующих: {0:N0}", displayed.ReferenceBars.Count) : "");
        }

        private void Parameter_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (ReferenceEquals(sender, TextBox_PerimeterSpacing))
            {
                if (_updatingPerimeterSpacing) return;
                _manualPerimeterSpacingText = TextBox_PerimeterSpacing.Text;
            }
            InvalidatePlan();
        }
        private void Parameter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            InvalidatePlan();
        }
        private void Parameter_Checked(object sender, RoutedEventArgs e)
        {
            if (ReferenceEquals(sender, CheckBox_ZoneEndsInPerimeter))
            {
                if (_updatingZoneEnds) return;
                _automaticZoneEndsPreference = CheckBox_ZoneEndsInPerimeter.IsChecked == true;
            }
            InvalidatePlan();
        }
        private void Background_PropertyChanged(object sender, PropertyChangedEventArgs e) { InvalidatePlan(); }
        private void DemandMeaning_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // WPF updates SelectedValue after SelectionChanged when assigned by value.
            // SelectedItem already identifies the new mode during this event.
            string meaning = (ComboBox_DemandMeaning.SelectedItem as DemandMeaningChoice)?.Key;
            if (RequiredAreaColumn != null)
                RequiredAreaColumn.Visibility = meaning == "Manual" ? Visibility.Collapsed : Visibility.Visible;
            UpdateZoneEndAvailability();
            InvalidatePlan();
        }
        private void UpdateZoneEndAvailability()
        {
            if (CheckBox_ZoneEndsInPerimeter == null) return;
            bool manual = (ComboBox_DemandMeaning.SelectedItem as DemandMeaningChoice)?.Key == "Manual";
            _updatingZoneEnds = true;
            try
            {
                CheckBox_ZoneEndsInPerimeter.IsEnabled = manual;
                CheckBox_ZoneEndsInPerimeter.IsChecked = manual && _automaticZoneEndsPreference;
            }
            finally { _updatingZoneEnds = false; }
        }
        private void Color_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(ColorStat.Spacing)) InvalidatePlan();
        }

        private void InvalidatePlan()
        {
            if (!_initialized || _closed || _selectingUnderlay) return;
            DiscardPreview();
            var source = new CancellationTokenSource();
            _calculation = source;
            PreviewParameters parameters;
            List<string> problems;
            if (!TryCaptureParameters(out parameters, out problems))
            {
                SetStatus("Проверьте параметры", "DangerBrush", false, problems);
                source.Dispose();
                _calculation = null;
                return;
            }
            SetStatus("Обновляем предпросмотр…", "WarningBrush", true, null);
            _ = CalculateLaterAsync(parameters, _revision, source);
        }

        private void DiscardPreview()
        {
            _revision++;
            _readyRevision = -1;
            _readyPlan = null;
            _readyParameters = null;
            AcceptedPlan = null;
            AcceptedParameters = null;
            Button_Confirm.IsEnabled = false;
            _calculation?.Cancel();
            PreviewCanvas.SetPlan(null);
            PlanSummary.Text = string.Empty;
            _planDetails.Clear();
            ContextWarningsExpander.IsExpanded = false;
            RefreshDetails();
            // Only enabled new layers are provisional evidence while the next calculation runs.
            // Existing-background proof belongs to the exact completed request and is discarded.
            UpdatePerimeterSpacingMode(null, null);
        }

        private bool UpdatePerimeterSpacingMode(ReinforcementPlan plan, PreviewParameters parameters)
        {
            bool HasLower(string direction) => plan == null
                ? _backgroundLayers.Any(layer => layer.Enabled && layer.Direction == direction)
                : plan.Bars.Concat(plan.ReferenceBars).Any(bar => bar.Placement?.Component == "Background"
                    && bar.SpecKey == "Background|" + direction);
            string Step(string direction)
            {
                double value;
                if (parameters != null)
                    value = parameters.BackgroundLayers.FirstOrDefault(layer => layer.Direction == direction)?.SpacingMm ?? double.NaN;
                else if (!TryNumber(_backgroundLayers.FirstOrDefault(layer => layer.Direction == direction)?.SpacingText, out value))
                    return "—";
                return value > 0 && !double.IsNaN(value) && !double.IsInfinity(value)
                    ? value.ToString("0.###", CultureInfo.CurrentCulture) : "—";
            }
            bool lowerX = HasLower("Низ X"), lowerY = HasLower("Низ Y");
            bool automatic = lowerX && lowerY;
            _updatingPerimeterSpacing = true;
            try
            {
                TextBox_PerimeterSpacing.IsEnabled = !automatic;
                TextBox_PerimeterSpacing.Text = automatic ? "X " + Step("Низ X") + " / Y " + Step("Низ Y") : _manualPerimeterSpacingText;
                TextBlock_PerimeterDistribution.Text = automatic ? "По нижнему фону"
                    : lowerX || lowerY ? (lowerX ? "X" : "Y") + " — по нижнему фону; остальные края — вручную"
                    : "Шаг задан вручную";
            }
            finally { _updatingPerimeterSpacing = false; }
            return automatic;
        }

        private bool TryCaptureParameters(out PreviewParameters parameters, out List<string> problems)
        {
            problems = new List<string>();
            bool zones = ReferenceEquals(ComponentsTabs.SelectedItem, ZonesTab);
            parameters = new PreviewParameters
            {
                CreationScope = zones ? ReinforcementCreationScope.Zones : ReinforcementCreationScope.Background,
                ConcreteClass = ComboBox_ConcreteClass.SelectedItem as string,
                Direction = ComboBox_Direction.SelectedItem as string,
                SourceGroupKey = (ComboBox_SourceGroup.SelectedItem as SourceGroupChoice)?.Group.Key,
                UseCutLengths = RadioButton_RoundCutLengths.IsChecked == true,
                IncludeZones = zones && _hasCadSource,
                AllowBarIntersections = true,
                AutomaticULegLength = true,
                UsePerimeterUForZoneEnds = zones && _hasCadSource && CheckBox_ZoneEndsInPerimeter.IsChecked == true,
                CalculationAxesConfirmed = zones && _hasCadSource && ((_context.SourceGroups?.Count ?? 0) == 0
                    || ComboBox_SourceGroup.SelectedItem is SourceGroupChoice),
                DemandMeaning = (ComboBox_DemandMeaning.SelectedItem as DemandMeaningChoice)?.Key
            };
            double top, bottom, edge, merge, round;
            if (!TryNumber(TextBox_TopCover.Text, out top) || top < 0) problems.Add("Защитный слой сверху: введите число от 0 мм.");
            if (!TryNumber(TextBox_BottomCover.Text, out bottom) || bottom < 0) problems.Add("Защитный слой снизу: введите число от 0 мм.");
            if (!TryNumber(TextBox_EdgeCover.Text, out edge) || edge < 0) problems.Add("Защитный слой на торце: введите число от 0 мм.");
            if (!TryNumber(TextBox_ZoneMergeTolerance.Text, out merge) || merge < 0)
            {
                if (parameters.IncludeZones) problems.Add("Объединение зон: введите допуск от 0 мм.");
                merge = 0;
            }
            if (!TryNumber(TextBox_RoundIncrement.Text, out round) || round <= 0)
            {
                if (parameters.IncludeZones && !parameters.UseCutLengths) problems.Add("Округление длины: введите число больше 0 мм.");
                round = 10;
            }
            parameters.TopCoverMm = top;
            parameters.BottomCoverMm = bottom;
            parameters.EdgeCoverMm = edge;
            parameters.LayerOffsetMm = 0;
            parameters.MergeToleranceMm = merge;
            parameters.RoundIncrementMm = round;
            if (parameters.ConcreteClass == null) problems.Add("Выберите класс бетона.");
            if (parameters.IncludeZones && parameters.Direction == null) problems.Add("Выберите направление зон.");
            foreach (BackgroundLayerStat layer in _backgroundLayers)
            {
                var captured = new PreviewBackgroundLayer { Enabled = !zones && layer.Enabled, Direction = layer.Direction };
                if (TryCaptureType(layer.SelectedRebarType, out string backgroundKey)) captured.SpecKey = backgroundKey;
                else if (captured.Enabled) problems.Add("Фон «" + layer.Direction + "»: выберите тип арматуры из списка.");
                if (TryNumber(layer.SpacingText, out double backgroundSpacing) && backgroundSpacing > 0) captured.SpacingMm = backgroundSpacing;
                else
                {
                    captured.SpacingMm = double.NaN;
                    if (captured.Enabled) problems.Add("Фон «" + layer.Direction + "»: шаг должен быть больше нуля.");
                }
                parameters.BackgroundLayers.Add(captured);
            }
            parameters.Perimeter = new PreviewPerimeterOptions
            {
                Enabled = !zones && CheckBox_PerimeterEnabled.IsChecked == true,
                Outer = CheckBox_PerimeterOuter.IsChecked == true,
                Openings = CheckBox_PerimeterOpenings.IsChecked == true,
                UseUbars = true,
                UseLongitudinalBars = false,
                // Existing lower rows are discovered by the calculation. An invalid unused
                // manual value must not prevent that discovery or full automatic framing.
                SpacingMm = TryNumber(_manualPerimeterSpacingText, out double manualSpacing) ? manualSpacing : double.NaN
            };
            if (TryCaptureType(ComboBox_PerimeterType.SelectedItem as RebarBarType, out string existingPerimeterKey))
                parameters.Perimeter.SpecKey = existingPerimeterKey;
            if (parameters.Perimeter.Enabled)
            {
                if (!parameters.Perimeter.Outer && !parameters.Perimeter.Openings) problems.Add("Обрамление: выберите наружный контур или отверстия.");
                if (!TryCaptureType(ComboBox_PerimeterType.SelectedItem as RebarBarType, out string key)) problems.Add("П-обрамление: выберите тип арматуры из списка.");
                else parameters.Perimeter.SpecKey = key;
            }
            if (zones && !_hasCadSource) problems.Add("Выберите подложку с зонами армирования.");
            if (parameters.IncludeZones)
            {
                if (_context.SourceGroups != null && _context.SourceGroups.Count > 0 && string.IsNullOrWhiteSpace(parameters.SourceGroupKey))
                    problems.Add("Выберите слой и высоту подложки из списка.");
                if (parameters.DemandMeaning != "Manual" && parameters.DemandMeaning != "AdditionalArea" && parameters.DemandMeaning != "TotalArea")
                    problems.Add("Выберите режим: ручное назначение, дополнительная или полная площадь арматуры.");
                foreach (ColorStat stat in _colors.Where(x => x.IsSelected))
                {
                    if (!TryCaptureType(stat.SelectedRebarType, out string key))
                    {
                        problems.Add(stat.Color + ": выберите тип арматуры из списка.");
                        continue;
                    }
                    if (!TryNumber(stat.SpacingText, out double spacing) || spacing <= 0)
                    {
                        problems.Add(stat.Color + ": шаг должен быть больше нуля.");
                        continue;
                    }
                    double? requiredArea = null;
                    if (parameters.DemandMeaning != "Manual")
                    {
                        if (!TryNumber(stat.RequiredAreaText, out double area) || area < 0)
                        {
                            problems.Add(stat.Color + ": введите требуемую площадь арматуры от 0 мм²/м.");
                            continue;
                        }
                        requiredArea = area;
                    }
                    parameters.Selections.Add(new PreviewSelection { ColorKey = stat.Color, SpecKey = key, SpacingMm = spacing, RequiredAreaMm2PerM = requiredArea });
                }
                if (!_colors.Any(x => x.IsSelected)) problems.Add("Выберите хотя бы один цвет для армирования.");
            }
            if (!zones && !parameters.BackgroundLayers.Any(layer => layer.Enabled) && !parameters.Perimeter.Enabled)
                problems.Add("Включите слой фона или П-обрамление.");
            if (_context.Calculate == null) problems.Add("Не удалось запустить расчёт. Закройте окно и повторно откройте команду.");
            return problems.Count == 0;
        }

        private bool TryCaptureType(RebarBarType type, out string key)
        {
            key = null;
            return type != null && _typeNames.TryGetValue(type, out string name) && _context.RebarTypeKeys != null &&
                _context.RebarTypeKeys.TryGetValue(name, out key) && !string.IsNullOrWhiteSpace(key);
        }

        private async Task CalculateLaterAsync(PreviewParameters parameters, int revision, CancellationTokenSource source)
        {
            CancellationToken token = source.Token;
            var calculate = _context.Calculate;
            try
            {
                await Task.Delay(300, token);
                ReinforcementPlan plan = await Task.Run(() => calculate(parameters, token), token);
                if (_closed || token.IsCancellationRequested || revision != _revision) return;
                if (plan == null) throw new InvalidOperationException("Расчёт не выполнен. Откройте команду заново.");
                bool automaticPerimeter = UpdatePerimeterSpacingMode(plan, parameters);
                // One lower direction can serve every selected oblique edge. Actual edge
                // evidence decides whether the manual value is used in this particular plan.
                bool needsManualPerimeter = plan.PerimeterUEdges.Any(edge => !edge.FollowsBackgroundRows)
                    || plan.PerimeterUEdges.Count == 0 && !automaticPerimeter;
                bool invalidManualPerimeter = needsManualPerimeter
                    && parameters.Perimeter.Enabled
                    && (!(parameters.Perimeter.SpacingMm > 0) || double.IsInfinity(parameters.Perimeter.SpacingMm));
                PreviewCanvas.SetPlan(plan);
                UpdatePreviewSummary();
                var diagnostics = new List<string>();
                _planDetails.Clear();
                foreach (var diagnostic in plan.Diagnostics.OrderByDescending(d => d.IsBlocking))
                {
                    string sourceLabel = DiagnosticSourceLabel(diagnostic.SourceId);
                    string message = diagnostic.Message
                        + (sourceLabel.Length == 0 ? "" : " [" + sourceLabel + "]");
                    // Only known informational results belong in details. Errors and new codes stay visible.
                    if (!diagnostic.IsBlocking && DetailDiagnosticCodes.Contains(diagnostic.Code))
                        _planDetails.Add(message);
                    else diagnostics.Add(message);
                }
                RefreshDetails();
                if (invalidManualPerimeter)
                    diagnostics.Insert(0, "П без фона: введите шаг больше нуля.");
                if (plan.CanCreate && !invalidManualPerimeter)
                {
                    _readyPlan = plan;
                    _readyParameters = parameters;
                    _readyRevision = revision;
                    Button_Confirm.IsEnabled = true;
                    bool hasAdoptedEdgeDetail = plan.Diagnostics.Any(d => d.Code == "ZONE_ENDS_IN_PERIMETER_U");
                    SetStatus("Готово к созданию",
                        hasAdoptedEdgeDetail ? "WarningBrush" : "SuccessBrush", false, diagnostics);
                }
                else
                {
                    if (diagnostics.Count == 0) diagnostics.Add("Нет стержней для создания. Проверьте выбор зон, диаметр и шаг.");
                    SetStatus("Создание недоступно", "DangerBrush", false, diagnostics);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception exception)
            {
                if (!_closed && !token.IsCancellationRequested && revision == _revision)
                    SetStatus("Не удалось рассчитать план", "DangerBrush", false, new[] { exception.Message });
            }
            finally
            {
                if (ReferenceEquals(_calculation, source)) _calculation = null;
                source.Dispose();
            }
        }

        private void SetStatus(string message, string brushKey, bool busy, IEnumerable<string> diagnostics)
        {
            StatusText.Text = message;
            StatusDot.Fill = (Brush)FindResource(brushKey);
            CalculationProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            DiagnosticsList.ItemsSource = (diagnostics ?? Enumerable.Empty<string>()).Distinct().ToList();
        }

        private void RefreshDetails()
        {
            var details = _warnings.Concat(_planDetails).Distinct().ToList();
            ContextWarningsList.ItemsSource = details;
            ContextWarningsExpander.Header = "Подробности";
            ContextWarningsExpander.Visibility = details.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private static string DiagnosticSourceLabel(string sourceId)
        {
            if (string.IsNullOrWhiteSpace(sourceId)) return string.Empty;
            int colorStart = sourceId.LastIndexOf("|RGB(", StringComparison.OrdinalIgnoreCase);
            if (colorStart >= 0) return sourceId.Substring(colorStart + 1);
            return Guid.TryParse(sourceId, out _) ? string.Empty : sourceId;
        }

        private static bool TryNumber(string text, out double number)
        {
            return double.TryParse((text ?? string.Empty).Trim().Replace(',', '.'), NumberStyles.Float,
                CultureInfo.InvariantCulture, out number) && !double.IsNaN(number) && !double.IsInfinity(number);
        }

        private void Button_Ok_Click(object sender, RoutedEventArgs e)
        {
            // No default button or Window key handler: Enter/Space in editors never accepts the dialog.
            if (_readyPlan == null || !_readyPlan.CanCreate || _readyRevision != _revision) return;
            AcceptedPlan = _readyPlan;
            AcceptedParameters = _readyParameters;
            _selectedColors = AcceptedParameters.IncludeZones ? _colors.Where(x => x.IsSelected).ToList() : new List<ColorStat>();
            ConcreteClass = _settings.ConcreteClass = AcceptedParameters.ConcreteClass;
            AdjacentElementsTolerance = _settings.AdjacentElementsTolerance;
            ZoneMergeTolerance = _settings.ZoneMergeTolerance = TextBox_ZoneMergeTolerance.Text;
            _settings.EdgeCoverMm = null; // Remove the obsolete global side-cover preference on the next save.
            _settings.LayerOffsetMm = "0";
            SelectedReinforcementDirection = _settings.SelectedReinforcementDirection = AcceptedParameters.Direction;
            UseCutLengths = _settings.UseCutLengths = AcceptedParameters.UseCutLengths;
            RoundIncrement = _settings.RoundIncrement = TextBox_RoundIncrement.Text;
            _settings.DemandMeaning = AcceptedParameters.DemandMeaning;
            _settings.AutomaticZoneEnds = _automaticZoneEndsPreference;
            _settings.AllowBarIntersections = AcceptedParameters.AllowBarIntersections;
            _settings.BackgroundLayers = _backgroundLayers.Select(layer => new BackgroundLayerSettings
            {
                Enabled = layer.Enabled, Direction = layer.Direction, SpacingMm = layer.SpacingText,
                RebarTypeName = layer.SelectedRebarType != null && _typeNames.TryGetValue(layer.SelectedRebarType, out string name) ? name : null
            }).ToList();
            _settings.Perimeter = new PerimeterReinforcementSettings
            {
                // Creation is scoped to the active tab; preferences still belong to their editors.
                Enabled = CheckBox_PerimeterEnabled.IsChecked == true, Outer = CheckBox_PerimeterOuter.IsChecked == true,
                Openings = CheckBox_PerimeterOpenings.IsChecked == true, UseUbars = true,
                UseLongitudinalBars = false,
                RebarTypeName = ComboBox_PerimeterType.SelectedItem is RebarBarType perimeterType && _typeNames.TryGetValue(perimeterType, out string perimeterName) ? perimeterName : null,
                SpacingMm = _manualPerimeterSpacingText, LegLengthMm = _settings.Perimeter.LegLengthMm
            };
            _settings.ColorSettings = _allColors.Select(stat => new ColorReinforcementSettings
            {
                Color = stat.Color,
                RebarTypeName = stat.SelectedRebarType != null && _typeNames.TryGetValue(stat.SelectedRebarType, out string name) ? name : null,
                Spacing = double.IsNaN(stat.Spacing) || double.IsInfinity(stat.Spacing) ? 200 : stat.Spacing,
                IsSelected = stat.IsSelected,
                RequiredAreaMm2PerM = stat.RequiredAreaText
            }).ToList();
            if (!_settings.TrySaveSettings(out string warning))
                MessageBox.Show(this, warning, "Настройки", MessageBoxButton.OK, MessageBoxImage.Warning);
            DialogResult = true;
        }

        private void Button_Cancel_Click(object sender, RoutedEventArgs e)
        {
            // Hide/ShowDialog reuse retains false, so assigning it again does not close
            // the window. Close also supports modeless hosts and cancels the worker in Closed.
            Close();
        }
        private void Fit_Click(object sender, RoutedEventArgs e) { PreviewCanvas.Fit(); }
        private void ZoomIn_Click(object sender, RoutedEventArgs e) { PreviewCanvas.Zoom(1.25); }
        private void ZoomOut_Click(object sender, RoutedEventArgs e) { PreviewCanvas.Zoom(0.8); }
        private void Window_Closed(object sender, EventArgs e)
        {
            _closed = true;
            _calculation?.Cancel();
            foreach (ColorStat stat in _allColors) stat.PropertyChanged -= Color_PropertyChanged;
            foreach (BackgroundLayerStat layer in _backgroundLayers) layer.PropertyChanged -= Background_PropertyChanged;
        }
        public List<ColorStat> GetSelectedColors() { return _selectedColors ?? new List<ColorStat>(); }
    }
}
