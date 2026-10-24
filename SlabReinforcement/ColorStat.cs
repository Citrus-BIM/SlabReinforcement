using Autodesk.Revit.DB.Structure;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Media;

public class ColorStat : INotifyPropertyChanged
{
    private bool _isSelected;
    private double _spacing;
    private string _spacingText = "200";
    private RebarBarType _selectedRebarType;
    private string _requiredAreaText = string.Empty;

    public string Color { get; set; }
    public SolidColorBrush ColorBrush { get; set; } 

    public List<RebarBarType> RebarTypes { get; set; }

    public string RequiredAreaText
    {
        get => _requiredAreaText;
        set
        {
            if (_requiredAreaText == value) return;
            _requiredAreaText = value;
            OnPropertyChanged(nameof(RequiredAreaText));
        }
    }

    public double Spacing
    {
        get => _spacing;
        set
        {
            if (_spacing != value)
            {
                _spacing = value;
                _spacingText = value.ToString("G", CultureInfo.CurrentCulture);
                OnPropertyChanged(nameof(Spacing));
                OnPropertyChanged(nameof(SpacingText));
            }
        }
    }

    // Keep invalid text so a preview is invalidated before a cell loses focus.
    public string SpacingText
    {
        get => _spacingText;
        set
        {
            if (_spacingText == value) return;
            _spacingText = value;
            double parsed;
            _spacing = double.TryParse((value ?? string.Empty).Replace(',', '.'), NumberStyles.Float,
                CultureInfo.InvariantCulture, out parsed) ? parsed : double.NaN;
            OnPropertyChanged(nameof(SpacingText));
            OnPropertyChanged(nameof(Spacing));
        }
    }

    public RebarBarType SelectedRebarType
    {
        get => _selectedRebarType;
        set
        {
            if (_selectedRebarType != value)
            {
                _selectedRebarType = value;
                OnPropertyChanged(nameof(SelectedRebarType));
            }
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged(nameof(IsSelected));
            }
        }
    }

    public event PropertyChangedEventHandler PropertyChanged;

    protected virtual void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

// UI-only editors. Revit names and immutable keys are captured on the UI thread.
public sealed class BackgroundLayerStat : INotifyPropertyChanged
{
    private bool _enabled;
    private RebarBarType _selectedRebarType;
    private string _spacingText = "200";
    public string Direction { get; set; }
    public List<RebarBarType> RebarTypes { get; set; }
    public bool Enabled
    {
        get => _enabled;
        set { if (_enabled == value) return; _enabled = value; Changed(nameof(Enabled)); }
    }
    public RebarBarType SelectedRebarType
    {
        get => _selectedRebarType;
        set { if (_selectedRebarType == value) return; _selectedRebarType = value; Changed(nameof(SelectedRebarType)); }
    }
    public string SpacingText
    {
        get => _spacingText;
        set { if (_spacingText == value) return; _spacingText = value; Changed(nameof(SpacingText)); }
    }
    public event PropertyChangedEventHandler PropertyChanged;
    private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
