using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;

namespace Athena.UI.Controls;

/// <summary>
/// NumericUpDown with a Windows UI Automation peer that marshals range values
/// as doubles. Avalonia 12.0.5's built-in peer raises decimal property values,
/// which the Win32 COM Variant marshaller does not support.
/// </summary>
public class SafeNumericUpDown : NumericUpDown
{
    protected override Type StyleKeyOverride => typeof(NumericUpDown);

    protected override AutomationPeer OnCreateAutomationPeer() =>
        new SafeNumericUpDownAutomationPeer(this);
}

internal sealed class SafeNumericUpDownAutomationPeer : ControlAutomationPeer, IRangeValueProvider
{
    public SafeNumericUpDownAutomationPeer(SafeNumericUpDown owner) : base(owner)
    {
        Owner.PropertyChanged += OwnerPropertyChanged;
    }

    public new SafeNumericUpDown Owner => (SafeNumericUpDown)base.Owner;

    public bool IsReadOnly => Owner.IsReadOnly;

    public double Maximum => (double)Owner.Maximum;

    public double Minimum => (double)Owner.Minimum;

    public double Value => Owner.Value.HasValue
        ? (double)Owner.Value.Value
        : (double)Math.Clamp(0m, Owner.Minimum, Owner.Maximum);

    public double SmallChange => (double)Owner.Increment;

    public double LargeChange => (double)Owner.Increment;

    public void SetValue(double value) => Owner.Value = (decimal)value;

    protected override AutomationControlType GetAutomationControlTypeCore() =>
        AutomationControlType.Spinner;

    protected override string GetClassNameCore() => nameof(NumericUpDown);

    private void OwnerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == NumericUpDown.MinimumProperty)
        {
            RaisePropertyChangedEvent(
                RangeValuePatternIdentifiers.MinimumProperty,
                ToAutomationNumber(e.OldValue),
                ToAutomationNumber(e.NewValue));
        }
        else if (e.Property == NumericUpDown.MaximumProperty)
        {
            RaisePropertyChangedEvent(
                RangeValuePatternIdentifiers.MaximumProperty,
                ToAutomationNumber(e.OldValue),
                ToAutomationNumber(e.NewValue));
        }
        else if (e.Property == NumericUpDown.ValueProperty)
        {
            RaisePropertyChangedEvent(
                RangeValuePatternIdentifiers.ValueProperty,
                ToAutomationNumber(e.OldValue),
                ToAutomationNumber(e.NewValue));
        }
        else if (e.Property == NumericUpDown.IsReadOnlyProperty)
        {
            RaisePropertyChangedEvent(
                RangeValuePatternIdentifiers.IsReadOnlyProperty,
                e.OldValue,
                e.NewValue);
        }
    }

    private static object? ToAutomationNumber(object? value) => value switch
    {
        decimal number => (double)number,
        _ => value
    };
}
