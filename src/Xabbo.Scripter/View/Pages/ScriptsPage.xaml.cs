using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

using Xabbo.Scripter.ViewModel;

namespace Xabbo.Scripter.View.Pages;

/// <summary>
/// Interaction logic for ScriptsPage.xaml
/// </summary>
public partial class ScriptsPage : Page
{
    public ScriptsViewManager Manager { get; }

    public ScriptsPage(ScriptsViewManager manager)
    {
        Manager = manager;
        DataContext = manager;

        InitializeComponent();
    }

    private void DragablzItem_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element ||
            element.DataContext is not ScriptViewModel scriptViewModel)
        {
            return;
        }

        if (e.ChangedButton == MouseButton.Middle)
        {
            e.Handled = true;

            Manager.CloseScript(scriptViewModel);
        }
    }

    private void DragablzItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element ||
            element.DataContext is not ScriptViewModel scriptViewModel)
        {
            return;
        }

        FrameworkElement? closeButton = FindCloseButton(element);
        if (closeButton is null || !closeButton.IsVisible)
            return;

        Point position = e.GetPosition(closeButton);
        if (position.X >= 0 && position.Y >= 0 &&
            position.X <= closeButton.ActualWidth && position.Y <= closeButton.ActualHeight)
        {
            e.Handled = true;

            Manager.CloseScript(scriptViewModel);
        }
    }

    private static FrameworkElement? FindCloseButton(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);

            if (child is FrameworkElement element && element.Tag is "close")
                return element;

            FrameworkElement? found = FindCloseButton(child);
            if (found is not null)
                return found;
        }

        return null;
    }
}
