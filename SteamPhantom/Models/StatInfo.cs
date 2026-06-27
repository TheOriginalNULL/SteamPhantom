using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SteamPhantom.Models;

public enum StatType { Int, Float }

public partial class StatInfo : ObservableObject
{
    public string Apiname { get; }
    public string DisplayName { get; }
    public StatType Type { get; }
    public double DefaultValue { get; }

    private double _originalValue;

    [ObservableProperty]
    private string _valueText = "0";

    public bool IsDirty
    {
        get
        {
            if (!TryParse(ValueText, out var current)) return false;
            // Float tolerance to avoid silly micro-deltas
            return Type == StatType.Int
                ? (long)current != (long)_originalValue
                : Math.Abs(current - _originalValue) > 1e-6;
        }
    }

    public double ParsedValue => TryParse(ValueText, out var v) ? v : _originalValue;

    public string TypeLabel => Type == StatType.Int ? "int" : "float";

    public StatInfo(string apiname, string displayName, StatType type, double originalValue, double defaultValue)
    {
        Apiname = apiname;
        DisplayName = string.IsNullOrEmpty(displayName) ? apiname : displayName;
        Type = type;
        DefaultValue = defaultValue;
        _originalValue = originalValue;
        _valueText = Format(originalValue);
    }

    public void CommitOriginal()
    {
        if (TryParse(ValueText, out var v)) _originalValue = v;
        OnPropertyChanged(nameof(IsDirty));
    }

    public void ResetToOriginal()
    {
        ValueText = Format(_originalValue);
    }

    public void ResetToDefault()
    {
        ValueText = Format(DefaultValue);
    }

    partial void OnValueTextChanged(string value) => OnPropertyChanged(nameof(IsDirty));

    private bool TryParse(string s, out double value)
    {
        if (Type == StatType.Int)
        {
            if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
            { value = i; return true; }
            value = 0; return false;
        }
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private string Format(double v) => Type == StatType.Int
        ? ((long)v).ToString(CultureInfo.InvariantCulture)
        : v.ToString("0.###", CultureInfo.InvariantCulture);
}
