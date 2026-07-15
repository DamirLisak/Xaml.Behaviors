// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
using Avalonia.Xaml.Interactivity;

namespace Avalonia.Xaml.Interactions.Core;

internal static class ReversibleActionExecution
{
    public static void Execute(object? sender, ActionCollection? actions, object? parameter)
    {
        if (actions is null)
        {
            return;
        }

        foreach (var item in actions)
        {
            if (item is IReversibleAction reversibleAction)
            {
                reversibleAction.ExecuteReversibly(sender, parameter);
            }
            else if (item is IAction action)
            {
                action.Execute(sender, parameter);
            }
        }
    }
}
