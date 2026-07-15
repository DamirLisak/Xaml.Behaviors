// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Avalonia.Data;

namespace Avalonia.Xaml.Interactivity;

/// <summary>
/// Coordinates reversible, typed property changes across action instances.
/// </summary>
/// <typeparam name="TTarget">The target type.</typeparam>
/// <typeparam name="TValue">The property value type.</typeparam>
public sealed class ReversiblePropertyChange<TTarget, TValue>
{
    private sealed class PropertyFrame(TValue value, IDisposable? temporaryValue)
    {
        public TValue Value { get; set; } = value;
        public IDisposable? TemporaryValue { get; set; } = temporaryValue;
    }

    private sealed class PropertyStack(
        TValue originalValue,
        AvaloniaProperty? avaloniaProperty)
    {
        public TValue OriginalValue { get; } = originalValue;
        public AvaloniaProperty? AvaloniaProperty { get; } = avaloniaProperty;
        public List<PropertyFrame> Frames { get; } = [];
    }

    private static readonly ConditionalWeakTable<object, Dictionary<string, PropertyStack>> s_propertyStacks = new();

    private readonly string _propertyName;
    private object? _target;
    private PropertyFrame? _frame;

    /// <summary>
    /// Initializes a new reversible property-change coordinator.
    /// </summary>
    /// <param name="propertyName">The target property name.</param>
    public ReversiblePropertyChange(string propertyName)
    {
        ArgumentException.ThrowIfNullOrEmpty(propertyName);
        _propertyName = propertyName;
    }

    /// <summary>
    /// Applies a value while preserving the state required to revert it.
    /// </summary>
    /// <param name="target">The target object.</param>
    /// <param name="value">The value to apply.</param>
    /// <param name="getter">Gets the current property value.</param>
    /// <param name="setter">Sets the property value.</param>
    /// <returns><c>true</c> when the reversible value was applied; otherwise <c>false</c>.</returns>
    public bool Apply(
        TTarget target,
        TValue value,
        Func<TTarget, TValue> getter,
        Action<TTarget, TValue> setter)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(getter);
        ArgumentNullException.ThrowIfNull(setter);

        var targetObject = (object)target;
        if (_frame is not null && ReferenceEquals(_target, targetObject))
        {
            if (TryGetStack(targetObject, out var existingStack))
            {
                var frameIndex = existingStack.Frames.IndexOf(_frame);
                if (frameIndex >= 0)
                {
                    _frame.Value = value;
                    if (frameIndex == existingStack.Frames.Count - 1)
                    {
                        if (existingStack.AvaloniaProperty is not null)
                        {
                            var replacement = SetTemporaryValue(
                                (AvaloniaObject)targetObject,
                                existingStack.AvaloniaProperty,
                                value);
                            _frame.TemporaryValue?.Dispose();
                            _frame.TemporaryValue = replacement;
                        }
                        else
                        {
                            setter(target, value);
                        }
                    }

                    return true;
                }
            }

            ClearState();
        }
        else if (_frame is not null)
        {
            Revert(setter);
        }

        AvaloniaProperty? avaloniaProperty = null;
        if (targetObject is AvaloniaObject avaloniaObject)
        {
            avaloniaProperty = AvaloniaPropertyRegistry.Instance.FindRegistered(avaloniaObject, _propertyName);
            if (avaloniaProperty?.IsDirect == true)
            {
                return false;
            }
        }

        var propertyStacks = s_propertyStacks.GetOrCreateValue(targetObject);
        if (!propertyStacks.TryGetValue(_propertyName, out var propertyStack))
        {
            propertyStack = new PropertyStack(getter(target), avaloniaProperty);
            propertyStacks.Add(_propertyName, propertyStack);
        }

        IDisposable? temporaryValue = null;
        if (propertyStack.AvaloniaProperty is not null)
        {
            temporaryValue = SetTemporaryValue(
                (AvaloniaObject)targetObject,
                propertyStack.AvaloniaProperty,
                value);
        }
        else
        {
            setter(target, value);
        }

        var frame = new PropertyFrame(value, temporaryValue);
        propertyStack.Frames.Add(frame);
        _target = targetObject;
        _frame = frame;
        return true;
    }

    /// <summary>
    /// Reverts the value previously applied by this coordinator.
    /// </summary>
    /// <param name="setter">Sets the target property value.</param>
    /// <returns><c>true</c> when an applied value was reverted; otherwise <c>false</c>.</returns>
    public bool Revert(Action<TTarget, TValue> setter)
    {
        ArgumentNullException.ThrowIfNull(setter);
        if (_target is not TTarget target ||
            _frame is null ||
            !TryGetStack(_target, out var propertyStack))
        {
            return false;
        }

        var frameIndex = propertyStack.Frames.IndexOf(_frame);
        if (frameIndex < 0)
        {
            ClearState();
            return false;
        }

        var isTopFrame = frameIndex == propertyStack.Frames.Count - 1;
        if (propertyStack.AvaloniaProperty is not null)
        {
            _frame.TemporaryValue?.Dispose();
            if (isTopFrame && frameIndex > 0)
            {
                var precedingFrame = propertyStack.Frames[frameIndex - 1];
                var replacement = SetTemporaryValue(
                    (AvaloniaObject)_target,
                    propertyStack.AvaloniaProperty,
                    precedingFrame.Value);
                precedingFrame.TemporaryValue?.Dispose();
                precedingFrame.TemporaryValue = replacement;
            }
        }
        else if (isTopFrame)
        {
            var restoredValue = frameIndex > 0
                ? propertyStack.Frames[frameIndex - 1].Value
                : propertyStack.OriginalValue;
            setter(target, restoredValue);
        }

        propertyStack.Frames.RemoveAt(frameIndex);
        if (propertyStack.Frames.Count == 0 &&
            s_propertyStacks.TryGetValue(_target, out var propertyStacks))
        {
            propertyStacks.Remove(_propertyName);
            if (propertyStacks.Count == 0)
            {
                s_propertyStacks.Remove(_target);
            }
        }

        ClearState();
        return true;
    }

    private bool TryGetStack(object target, out PropertyStack propertyStack)
    {
        if (s_propertyStacks.TryGetValue(target, out var propertyStacks) &&
            propertyStacks.TryGetValue(_propertyName, out var foundStack))
        {
            propertyStack = foundStack;
            return true;
        }

        propertyStack = null!;
        return false;
    }

    private static IDisposable SetTemporaryValue(
        AvaloniaObject target,
        AvaloniaProperty property,
        TValue value)
    {
        return target.SetValue(property, value, BindingPriority.Animation) ??
               throw new InvalidOperationException("The styled property did not provide a reversible value entry.");
    }

    private void ClearState()
    {
        _target = null;
        _frame = null;
    }
}
