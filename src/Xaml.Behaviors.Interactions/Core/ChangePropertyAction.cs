// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Xaml.Interactivity;

namespace Avalonia.Xaml.Interactions.Core;

/// <summary>
/// An action that will change a specified property to a specified value when invoked.
/// </summary>
[RequiresUnreferencedCode("This functionality is not compatible with trimming.")]
public class ChangePropertyAction : StyledElementAction, IReversibleAction
{
    private sealed class PropertyFrame(object? value, IDisposable? temporaryValue)
    {
        public object? Value { get; set; } = value;
        public IDisposable? TemporaryValue { get; set; } = temporaryValue;
    }

    private sealed class PropertyStack(
        object? originalValue,
        bool preserveValueSource,
        bool useTemporaryValues)
    {
        public object? OriginalValue { get; } = originalValue;
        public bool PreserveValueSource { get; } = preserveValueSource;
        public bool UseTemporaryValues { get; } = useTemporaryValues;
        public List<PropertyFrame> Frames { get; } = [];
    }

    private static readonly ConditionalWeakTable<object, Dictionary<string, PropertyStack>> s_propertyStacks = new();

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
        return ExecuteCore(sender, preserveValueSource: false);
    }

    /// <inheritdoc />
    public object? ExecuteReversibly(object? sender, object? parameter)
    {
        return ExecuteCore(sender, preserveValueSource: true);
    }

    private object ExecuteCore(object? sender, bool preserveValueSource)
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

        if (preserveValueSource &&
            _isApplied &&
            _propertyStack is not null &&
            _propertyFrame is not null)
        {
            var frameIndex = _propertyStack.Frames.IndexOf(_propertyFrame);
            if (frameIndex < 0)
            {
                ClearSnapshot();
                return false;
            }

            if (frameIndex < _propertyStack.Frames.Count - 1)
            {
                _propertyFrame.Value = Value;
                return true;
            }

            if (_propertyStack.UseTemporaryValues)
            {
                if (!PropertyHelper.TrySetTemporaryAvaloniaPropertyValue(
                        targetObject,
                        propertyName,
                        Value,
                        out var replacementTemporaryValue))
                {
                    return false;
                }

                _propertyFrame.TemporaryValue?.Dispose();
                _propertyFrame.TemporaryValue = replacementTemporaryValue;
                _propertyFrame.Value = Value;
                return true;
            }

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

        if (preserveValueSource &&
            !_isApplied &&
            PropertyHelper.IsDirectAvaloniaProperty(targetObject, propertyName))
        {
            return false;
        }

        if (preserveValueSource &&
            !_isApplied &&
            TryApplyStackedValue(targetObject, propertyName, Value))
        {
            return true;
        }

        if (!preserveValueSource)
        {
            return PropertyHelper.UpdatePropertyValue(
                targetObject,
                propertyName,
                Value,
                preserveValueSource: false);
        }

        if (!_isApplied)
        {
            ClearSnapshot();

            if (PropertyHelper.TryGetPropertyValue(
                    targetObject,
                    propertyName,
                    out var previousValue,
                    out var capturedPreserveValueSource))
            {
                _previousTargetObject = targetObject;
                _previousPropertyName = propertyName;
                _previousValue = previousValue;
                _preserveValueSource = capturedPreserveValueSource;
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

        if (_propertyStack is not null &&
            _propertyFrame is not null)
        {
            return RevertStackedValue(_previousTargetObject);
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

    private bool TryApplyStackedValue(object targetObject, string propertyName, object? value)
    {
        var propertyStacks = s_propertyStacks.GetOrCreateValue(targetObject);

        var isNewStack = !propertyStacks.TryGetValue(propertyName, out var propertyStack);
        if (isNewStack)
        {
            if (!PropertyHelper.TryGetPropertyValue(
                    targetObject,
                    propertyName,
                    out var originalValue,
                    out var capturedPreserveValueSource))
            {
                if (propertyStacks.Count == 0)
                {
                    s_propertyStacks.Remove(targetObject);
                }

                return false;
            }

            IDisposable? initialTemporaryValue = null;
            var useTemporaryValues = capturedPreserveValueSource &&
                                     PropertyHelper.TrySetTemporaryAvaloniaPropertyValue(
                                         targetObject,
                                         propertyName,
                                         value,
                                         out initialTemporaryValue);
            propertyStack = new PropertyStack(
                originalValue,
                capturedPreserveValueSource,
                useTemporaryValues);

            if (useTemporaryValues)
            {
                var initialFrame = new PropertyFrame(value, initialTemporaryValue);
                propertyStack.Frames.Add(initialFrame);
                propertyStacks.Add(propertyName, propertyStack);
                SetAppliedState(targetObject, propertyName, propertyStack, initialFrame);
                return true;
            }
        }

        IDisposable? frameTemporaryValue = null;
        var updated = propertyStack!.UseTemporaryValues
            ? PropertyHelper.TrySetTemporaryAvaloniaPropertyValue(
                targetObject,
                propertyName,
                value,
                out frameTemporaryValue)
            : PropertyHelper.UpdatePropertyValue(
                targetObject,
                propertyName,
                value,
                propertyStack.PreserveValueSource);
        if (!updated)
        {
            return false;
        }

        var propertyFrame = new PropertyFrame(value, frameTemporaryValue);
        propertyStack.Frames.Add(propertyFrame);
        if (isNewStack)
        {
            propertyStacks.Add(propertyName, propertyStack);
        }

        SetAppliedState(targetObject, propertyName, propertyStack, propertyFrame);
        return true;
    }

    private void SetAppliedState(
        object targetObject,
        string propertyName,
        PropertyStack propertyStack,
        PropertyFrame propertyFrame)
    {
        _previousTargetObject = targetObject;
        _previousPropertyName = propertyName;
        _previousValue = propertyStack.OriginalValue;
        _preserveValueSource = propertyStack.PreserveValueSource;
        _propertyStack = propertyStack;
        _propertyFrame = propertyFrame;
        _isApplied = true;
    }

    private bool RevertStackedValue(object targetObject)
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
        if (propertyStack.UseTemporaryValues)
        {
            propertyFrame.TemporaryValue?.Dispose();
            if (wasTopFrame && frameIndex > 0)
            {
                var precedingFrame = propertyStack.Frames[frameIndex - 1];
                if (!PropertyHelper.TrySetTemporaryAvaloniaPropertyValue(
                        targetObject,
                        propertyName,
                        precedingFrame.Value,
                        out var replacementTemporaryValue))
                {
                    return false;
                }

                precedingFrame.TemporaryValue?.Dispose();
                precedingFrame.TemporaryValue = replacementTemporaryValue;
            }
        }
        else if (wasTopFrame)
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
            if (s_propertyStacks.TryGetValue(targetObject, out var propertyStacks))
            {
                propertyStacks.Remove(propertyName);
                if (propertyStacks.Count == 0)
                {
                    s_propertyStacks.Remove(targetObject);
                }
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
