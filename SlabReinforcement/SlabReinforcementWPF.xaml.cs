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
        private readonly PreviewContext _context;
        private readonly SlabReinforcementSettings _settings;
        private readonly List<ColorStat> _allColors;
        private readonly ObservableCollection<ColorStat> _colors;
        private readonly ObservableCollection<BackgroundLayerStat> _backgroundLayers = new ObservableCollection<BackgroundLayerStat>();
        private readonly bool _hasCadSource;
        private readonly Dictionary<RebarBarType, string> _typeNames = new Dictionary<RebarBarType, string>();
        private readonly List<string> _warnings = new List<string>();
        private CancellationTokenSource _calculation;
        private int _revision;
        private int _readyRevision = -1;
        private bool _initialized;
        private bool _closed;
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

        public SlabReinforcementWPF(List<ColorStat> colorStats, List<RebarBarType> availableRebarTypes)
            : this(colorStats, availableRebarTypes, new PreviewContext()) { }

        public SlabReinforcementWPF(List<ColorStat> colorStats, List<RebarBarType> availableRebarTypes, PreviewContext context)
        {
            _context = context ?? new PreviewContext();
            _hasCadSource = (_context.SourceGroups?.Count ?? 0) > 0 || (_context.Faces?.Count ?? 0) > 0;
            _settings = SlabReinforcementSettings.LoadSettings();
            InitializeComponent();
            // Size only the initial window to the usable desktop; do not constrain later resizing.
            Rect workArea = SystemParameters.WorkArea;
            Width = Math.Max(MinWidth, Math.Min(Width, workArea.Width));
            Height = Math.Max(MinHeight, Math.Min(Height, workArea.Height));
            if (!string.IsNullOrWhiteSpace(_settings.Warning)) _warnings.Add(_settings.Warning);
            if (_context.Warnings != null) _warnings.AddRange(_context.Warnings.Where(x => !string.IsNullOrWhiteSpace(x)));
            HostDescription.Text = string.IsNullOrWhiteSpace(_context.HostName) ? "Плита и исходные зоны КЭ" : _context.HostName;
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
            TextBox_EdgeCover.Text = _settings.EdgeCoverMm ?? _context.SideCoverMm.ToString("G", CultureInfo.CurrentCulture);
            TextBox_LayerOffset.Text = _settings.LayerOffsetMm;
            TextBox_ZoneMergeTolerance.Text = _settings.ZoneMergeTolerance;
            TextBox_RoundIncrement.Text = _settings.RoundIncrement;
            RadioButton_RoundIncrement.IsChecked = !_settings.UseCutLengths;
            RadioButton_RoundCutLengths.IsChecked = _settings.UseCutLengths;
            ContextWarningsList.ItemsSource = _warnings.Distinct().ToList();
            ContextWarningsExpander.Header = "Сведения: " + _warnings.Distinct().Count().ToString(CultureInfo.CurrentCulture);
            ContextWarningsExpander.Visibility = _warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

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
            TextBox_PerimeterSpacing.Text = perimeter.SpacingMm ?? "200";
            TextBox_PerimeterLeg.Text = perimeter.LegLengthMm ?? "500";
            ComboBox_DemandMeaning.ItemsSource = new[]
            {
                new DemandMeaningChoice { Key = "Manual", DisplayName = "Ручное назначение" },
                new DemandMeaningChoice { Key = "AdditionalArea", DisplayName = "Дополнительная площадь, мм²/м" },
                new DemandMeaningChoice { Key = "TotalArea", DisplayName = "Полная площадь, мм²/м" }
            };
            ComboBox_DemandMeaning.SelectedValue = _settings.DemandMeaning;
            if (ComboBox_DemandMeaning.SelectedIndex < 0) ComboBox_DemandMeaning.SelectedIndex = 0;
            CheckBox_IncludeZones.IsChecked = _hasCadSource;
            CheckBox_IncludeZones.IsEnabled = _hasCadSource;
            // This is a new source/edge relationship on every invocation; never restore the acknowledgment.
            CheckBox_CalculationAxesConfirmed.IsChecked = false;
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
                    DisplayName = string.Format(CultureInfo.CurrentCulture, "{0} · {1} {2:0.###} мм · КЭ: {3:N0}",
                        string.IsNullOrWhiteSpace(group.Layer) ? "Без слоя" : group.Layer,
                        _context.SourceElevationLabel, group.ElevationMm, group.Faces?.Count ?? 0)
                }).ToList();
            ComboBox_SourceGroup.ItemsSource = sourceChoices;
            SourceGroupPanel.Visibility = sourceChoices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (sourceChoices.Count == 1) ComboBox_SourceGroup.SelectedIndex = 0;
            else if (sourceChoices.Count > 1) UpdateSourceGroup();
            else PreviewCanvas.SetContext(_context);
            _initialized = true;
        }

        private void SourceGroup_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_initialized) CheckBox_CalculationAxesConfirmed.IsChecked = false;
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
            InvalidatePlan();
        }

        private void Parameter_TextChanged(object sender, TextChangedEventArgs e) { InvalidatePlan(); }
        private void Parameter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_initialized && ReferenceEquals(sender, ComboBox_Direction)) CheckBox_CalculationAxesConfirmed.IsChecked = false;
            InvalidatePlan();
        }
        private void Parameter_Checked(object sender, RoutedEventArgs e) { InvalidatePlan(); }
        private void Background_PropertyChanged(object sender, PropertyChangedEventArgs e) { InvalidatePlan(); }
        private void DemandMeaning_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (RequiredAreaColumn != null)
                RequiredAreaColumn.Visibility = (ComboBox_DemandMeaning.SelectedValue as string) == "Manual" ? Visibility.Collapsed : Visibility.Visible;
            InvalidatePlan();
        }
        private void Color_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(ColorStat.Spacing)) InvalidatePlan();
        }

        private void InvalidatePlan()
        {
            if (!_initialized || _closed) return;
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
            SetStatus("Пересчёт предпросмотра…", "WarningBrush", true, null);
            _ = CalculateLaterAsync(parameters, _revision, source);
        }

        private bool TryCaptureParameters(out PreviewParameters parameters, out List<string> problems)
        {
            problems = new List<string>();
            parameters = new PreviewParameters
            {
                ConcreteClass = ComboBox_ConcreteClass.SelectedItem as string,
                Direction = ComboBox_Direction.SelectedItem as string,
                SourceGroupKey = (ComboBox_SourceGroup.SelectedItem as SourceGroupChoice)?.Group.Key,
                UseCutLengths = RadioButton_RoundCutLengths.IsChecked == true,
                IncludeZones = CheckBox_IncludeZones.IsChecked == true,
                CalculationAxesConfirmed = CheckBox_CalculationAxesConfirmed.IsChecked == true,
                DemandMeaning = ComboBox_DemandMeaning.SelectedValue as string
            };
            double edge, merge, round, layerOffset;
            if (!TryNumber(TextBox_EdgeCover.Text, out edge) || edge < 0) problems.Add("Отступ от края должен быть неотрицательным числом.");
            if (!TryNumber(TextBox_ZoneMergeTolerance.Text, out merge) || merge < 0) problems.Add("Допуск объединения должен быть неотрицательным числом.");
            if (!TryNumber(TextBox_LayerOffset.Text, out layerOffset) || layerOffset < 0) problems.Add("Смещение слоя должно быть неотрицательным числом.");
            if (!TryNumber(TextBox_RoundIncrement.Text, out round) || round <= 0)
            {
                if (!parameters.UseCutLengths) problems.Add("Кратность округления должна быть больше нуля.");
                round = 10;
            }
            parameters.EdgeCoverMm = edge;
            parameters.LayerOffsetMm = layerOffset;
            parameters.MergeToleranceMm = merge;
            parameters.RoundIncrementMm = round;
            if (parameters.ConcreteClass == null || parameters.Direction == null) problems.Add("Выберите класс бетона и направление.");
            foreach (BackgroundLayerStat layer in _backgroundLayers)
            {
                var captured = new PreviewBackgroundLayer { Enabled = layer.Enabled, Direction = layer.Direction };
                if (layer.Enabled)
                {
                    if (!TryCaptureType(layer.SelectedRebarType, out string key)) problems.Add("Фон «" + layer.Direction + "»: выберите доступный тип арматуры.");
                    else captured.SpecKey = key;
                    if (!TryNumber(layer.SpacingText, out double spacing) || spacing <= 0) problems.Add("Фон «" + layer.Direction + "»: шаг должен быть больше нуля.");
                    else captured.SpacingMm = spacing;
                }
                parameters.BackgroundLayers.Add(captured);
            }
            parameters.Perimeter = new PreviewPerimeterOptions
            {
                Enabled = CheckBox_PerimeterEnabled.IsChecked == true,
                Outer = CheckBox_PerimeterOuter.IsChecked == true,
                Openings = CheckBox_PerimeterOpenings.IsChecked == true,
                UseUbars = true,
                UseLongitudinalBars = false
            };
            if (parameters.Perimeter.Enabled)
            {
                if (!parameters.Perimeter.Outer && !parameters.Perimeter.Openings) problems.Add("Обрамление: выберите наружный контур или отверстия.");
                if (!TryCaptureType(ComboBox_PerimeterType.SelectedItem as RebarBarType, out string key)) problems.Add("Обрамление: выберите доступный тип арматуры.");
                else parameters.Perimeter.SpecKey = key;
                if (!TryNumber(TextBox_PerimeterSpacing.Text, out double spacing) || spacing <= 0) problems.Add("Обрамление: шаг П-стержней должен быть больше нуля.");
                else parameters.Perimeter.SpacingMm = spacing;
                if (!TryNumber(TextBox_PerimeterLeg.Text, out double leg) || leg <= 0) problems.Add("Обрамление: длина лапки должна быть больше нуля.");
                else parameters.Perimeter.LegLengthMm = leg;
            }
            if (parameters.IncludeZones)
            {
                if (!_hasCadSource) problems.Add("Для зон нужна расчётная подложка.");
                if (_context.SourceGroups != null && _context.SourceGroups.Count > 0 && string.IsNullOrWhiteSpace(parameters.SourceGroupKey))
                    problems.Add("Выберите слой и плоскость расчётной подложки.");
                if (!parameters.CalculationAxesConfirmed) problems.Add("Подтвердите соответствие расчётных осей и выбранного направления.");
                if (parameters.DemandMeaning != "Manual" && parameters.DemandMeaning != "AdditionalArea" && parameters.DemandMeaning != "TotalArea")
                    problems.Add("Выберите смысл значений подложки.");
                foreach (ColorStat stat in _colors.Where(x => x.IsSelected))
                {
                    if (!TryCaptureType(stat.SelectedRebarType, out string key))
                    {
                        problems.Add(stat.Color + ": выберите доступный тип арматуры.");
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
                            problems.Add(stat.Color + ": задайте неотрицательную площадь, мм²/м.");
                            continue;
                        }
                        requiredArea = area;
                    }
                    parameters.Selections.Add(new PreviewSelection { ColorKey = stat.Color, SpecKey = key, SpacingMm = spacing, RequiredAreaMm2PerM = requiredArea });
                }
                if (!_colors.Any(x => x.IsSelected)) problems.Add("Выберите хотя бы один цвет для армирования.");
            }
            if (!parameters.IncludeZones && !parameters.BackgroundLayers.Any(layer => layer.Enabled) && !parameters.Perimeter.Enabled)
                problems.Add("Включите фон, обрамление или зоны.");
            if (_context.Calculate == null) problems.Add("Сервис расчёта предпросмотра не подключён.");
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
            try
            {
                await Task.Delay(300, token);
                ReinforcementPlan plan = await Task.Run(() => _context.Calculate(parameters, token), token);
                if (_closed || token.IsCancellationRequested || revision != _revision) return;
                if (plan == null) throw new InvalidOperationException("Расчёт не вернул план армирования.");
                PreviewCanvas.SetPlan(plan);
                PlanSummary.Text = string.Format(CultureInfo.CurrentCulture, "Стержней: {0:N0} · Наборов: {1:N0}", plan.Bars.Count, plan.Groups.Count);
                var diagnostics = plan.Diagnostics.Select(d => (d.IsBlocking ? "Ошибка: " : "") +
                    d.Message + (string.IsNullOrWhiteSpace(d.SourceId) ? "" : " [" + d.SourceId + "]")).ToList();
                if (plan.CanCreate)
                {
                    _readyPlan = plan;
                    _readyParameters = parameters;
                    _readyRevision = revision;
                    Button_Confirm.IsEnabled = true;
                    SetStatus("План готов к созданию", "SuccessBrush", false, diagnostics);
                }
                else
                {
                    if (diagnostics.Count == 0) diagnostics.Add("Для выбранных параметров нет стержней, которые можно создать.");
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
            _settings.EdgeCoverMm = TextBox_EdgeCover.Text;
            _settings.LayerOffsetMm = TextBox_LayerOffset.Text;
            SelectedReinforcementDirection = _settings.SelectedReinforcementDirection = AcceptedParameters.Direction;
            UseCutLengths = _settings.UseCutLengths = AcceptedParameters.UseCutLengths;
            RoundIncrement = _settings.RoundIncrement = TextBox_RoundIncrement.Text;
            _settings.DemandMeaning = AcceptedParameters.DemandMeaning;
            _settings.BackgroundLayers = _backgroundLayers.Select(layer => new BackgroundLayerSettings
            {
                Enabled = layer.Enabled, Direction = layer.Direction, SpacingMm = layer.SpacingText,
                RebarTypeName = layer.SelectedRebarType != null && _typeNames.TryGetValue(layer.SelectedRebarType, out string name) ? name : null
            }).ToList();
            _settings.Perimeter = new PerimeterReinforcementSettings
            {
                Enabled = AcceptedParameters.Perimeter.Enabled, Outer = AcceptedParameters.Perimeter.Outer,
                Openings = AcceptedParameters.Perimeter.Openings, UseUbars = AcceptedParameters.Perimeter.UseUbars,
                UseLongitudinalBars = AcceptedParameters.Perimeter.UseLongitudinalBars,
                RebarTypeName = ComboBox_PerimeterType.SelectedItem is RebarBarType perimeterType && _typeNames.TryGetValue(perimeterType, out string perimeterName) ? perimeterName : null,
                SpacingMm = TextBox_PerimeterSpacing.Text, LegLengthMm = TextBox_PerimeterLeg.Text
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

        private void Button_Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; }
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
