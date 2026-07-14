// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
using Avalonia.Xaml.Interactivity;

namespace Avalonia.Xaml.Interactions.Core;

/// <summary>
/// An action that will change a specified property to a specified value when invoked.
/// </summary>
[RequiresUnreferencedCode("This functionality is not compatible with trimming.")]
public class ChangePropertyAction : StyledElementAction, IReversibleAction
{
    private sealed class PropertyFrame(object? value)
    {
        public object? Value { get; set; } = value;
    }

    private sealed class PropertyStack(object? originalValue, bool preserveValueSource)
    {
        public object? OriginalValue { get; } = originalValue;
        public bool PreserveValueSource { get; } = preserveValueSource;
        public List<PropertyFrame> Frames { get; } = [];
    }

    private static readonly AttachedProperty<Dictionary<string, PropertyStack>?> PropertyStacksProperty =
        AvaloniaProperty.RegisterAttached<ChangePropertyAction, AvaloniaObject, Dictionary<string, PropertyStack>?>(
            "ReversiblePropertyStacks");

    private bool _isApplied;
    private object? _previousTargetObject;
    private string? _previousPropertyName;
    private object? _previousValue;
    private bool _preserveValueSource;
    private PropertyStack? _propertyStack;
    private PropertyFrame? _propertyFrame;

    /// <summary>
    /// Identifies the <seealso cref="PropertyName"/> avalonia property.
    /// </summary>
    public static readonly StyledProperty<string?> PropertyNameProperty =
        AvaloniaProperty.Register<ChangePropertyAction, string?>(nameof(PropertyName));

    /// <summary>
    /// Identifies the <seealso cref="TargetObject"/> avalonia property.
    /// </summary>
    public static readonly StyledProperty<object?> TargetObjectProperty =
        AvaloniaProperty.Register<ChangePropertyAction, object?>(nameof(TargetObject));

    /// <summary>
    /// Identifies the <seealso cref="Value"/> avalonia property.
    /// </summary>
    public static readonly StyledProperty<object?> ValueProperty =
        AvaloniaProperty.Register<ChangePropertyAction, object?>(nameof(Value));

    /// <summary>
    /// Gets or sets the name of the property to change. This is an avalonia property.
    /// </summary>
    public string? PropertyName
    {
        get => GetValue(PropertyNameProperty);
        set => SetValue(PropertyNameProperty, value);
    }

    /// <summary>
    /// Gets or sets the value to set. This is an avalonia property.
    /// </summary>
    public object? Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>
    /// Gets or sets the object whose property will be changed.
    /// If <seealso cref="TargetObject"/> is not set or cannot be resolved, the sender of <seealso cref="Execute"/> will be used. This is an avalonia property.
    /// </summary>
    [ResolveByName]
    public object? TargetObject
    {
        get => GetValue(TargetObjectProperty);
        set => SetValue(TargetObjectProperty, value);
    }

    /// <summary>
    /// Executes the action.
    /// </summary>
    /// <param name="sender">The <see cref="object"/> that is passed to the action by the behavior. Generally this is <seealso cref="Avalonia.Xaml.Interactivity.IBehavior.AssociatedObject"/> or a target object.</param>
    /// <param name="parameter">The value of this parameter is determined by the caller.</param>
    /// <returns>True if updating the property value succeeds; else false.</returns>
    public override object Execute(object? sender, object? parameter)
    {
        if (!IsEnabled)
        {
            return false;
        }

        var targetObject = GetValue(TargetObjectProperty) is not null ? TargetObject : sender;
        if (targetObject is null)
        {
            return false;
        }

        var propertyName = PropertyName;
        if (propertyName is null)
        {
            return false;
        }

        if (_isApplied &&
            _propertyStack is not null &&
            _propertyFrame is not null)
        {
            var updatedFrame = PropertyHelper.UpdatePropertyValue(
                targetObject,
                propertyName,
                Value,
                _propertyStack.PreserveValueSource);
            if (updatedFrame)
            {
                _propertyFrame.Value = Value;
            }

            return updatedFrame;
        }

        if (!_isApplied &&
            targetObject is AvaloniaObject avaloniaObject &&
            TryApplyStackedValue(avaloniaObject, propertyName, Value))
        {
            return true;
        }

        if (!_isApplied)
        {
            ClearSnapshot();

            if (PropertyHelper.TryGetPropertyValue(
                    targetObject,
                    propertyName,
                    out var previousValue,
                    out var preserveValueSource))
            {
                _previousTargetObject = targetObject;
                _previousPropertyName = propertyName;
                _previousValue = previousValue;
                _preserveValueSource = preserveValueSource;
            }
        }

        var updated = PropertyHelper.UpdatePropertyValue(
            targetObject,
            propertyName,
            Value,
            _preserveValueSource);
        if (updated)
        {
            _isApplied = _previousTargetObject is not null &&
                         _previousPropertyName is not null;
        }

        return updated;
    }

    /// <summary>
    /// Reverts the property value captured when this action was first applied.
    /// </summary>
    /// <param name="sender">The <see cref="object"/> that is passed to the action by the behavior.</param>
    /// <param name="parameter">The value of this parameter is determined by the caller.</param>
    /// <returns>True if reverting the property value succeeds; else false.</returns>
    public object Revert(object? sender, object? parameter)
    {
        if (!_isApplied ||
            _previousTargetObject is null ||
            _previousPropertyName is null)
        {
            return false;
        }

        if (_previousTargetObject is AvaloniaObject avaloniaObject &&
            _propertyStack is not null &&
            _propertyFrame is not null)
        {
            return RevertStackedValue(avaloniaObject);
        }

        var reverted = PropertyHelper.UpdatePropertyValue(
            _previousTargetObject,
            _previousPropertyName,
            _previousValue,
            _preserveValueSource);

        if (reverted)
        {
            ClearSnapshot();
        }

        return reverted;
    }

    private bool TryApplyStackedValue(AvaloniaObject targetObject, string propertyName, object? value)
    {
        var propertyStacks = targetObject.GetValue(PropertyStacksProperty);
        var isNewCollection = propertyStacks is null;
        propertyStacks ??= new Dictionary<string, PropertyStack>();

        var isNewStack = !propertyStacks.TryGetValue(propertyName, out var propertyStack);
        if (isNewStack)
        {
            if (!PropertyHelper.TryGetPropertyValue(
                    targetObject,
                    propertyName,
                    out var originalValue,
                    out var preserveValueSource))
            {
                return false;
            }

            propertyStack = new PropertyStack(originalValue, preserveValueSource);
        }

        var updated = PropertyHelper.UpdatePropertyValue(
            targetObject,
            propertyName,
            value,
            propertyStack!.PreserveValueSource);
        if (!updated)
        {
            return false;
        }

        var propertyFrame = new PropertyFrame(value);
        propertyStack.Frames.Add(propertyFrame);
        if (isNewStack)
        {
            propertyStacks.Add(propertyName, propertyStack);
        }

        if (isNewCollection)
        {
            targetObject.SetValue(PropertyStacksProperty, propertyStacks);
        }

        _previousTargetObject = targetObject;
        _previousPropertyName = propertyName;
        _previousValue = propertyStack.OriginalValue;
        _preserveValueSource = propertyStack.PreserveValueSource;
        _propertyStack = propertyStack;
        _propertyFrame = propertyFrame;
        _isApplied = true;
        return true;
    }

    private bool RevertStackedValue(AvaloniaObject targetObject)
    {
        var propertyName = _previousPropertyName!;
        var propertyStack = _propertyStack!;
        var propertyFrame = _propertyFrame!;
        var frameIndex = propertyStack.Frames.IndexOf(propertyFrame);
        if (frameIndex < 0)
        {
            ClearSnapshot();
            return false;
        }

        var wasTopFrame = frameIndex == propertyStack.Frames.Count - 1;
        if (wasTopFrame)
        {
            var restoredValue = frameIndex > 0
                ? propertyStack.Frames[frameIndex - 1].Value
                : propertyStack.OriginalValue;
            var reverted = PropertyHelper.UpdatePropertyValue(
                targetObject,
                propertyName,
                restoredValue,
                propertyStack.PreserveValueSource);
            if (!reverted)
            {
                return false;
            }
        }

        propertyStack.Frames.RemoveAt(frameIndex);
        if (propertyStack.Frames.Count == 0)
        {
            var propertyStacks = targetObject.GetValue(PropertyStacksProperty);
            propertyStacks?.Remove(propertyName);
            if (propertyStacks is { Count: 0 })
            {
                targetObject.ClearValue(PropertyStacksProperty);
            }
        }

        ClearSnapshot();
        return true;
    }

    private void ClearSnapshot()
    {
        _isApplied = false;
        _previousTargetObject = null;
        _previousPropertyName = null;
        _previousValue = null;
        _preserveValueSource = false;
        _propertyStack = null;
        _propertyFrame = null;
    }
}
