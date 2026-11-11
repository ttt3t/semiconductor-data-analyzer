using System.Windows;
using System.Windows.Controls;

namespace SemiconductorCsvAnalyzer.Services;

/// <summary>Modeless top-level windows, including option forms that return a result asynchronously.</summary>
public static class IndependentWindow
{
    private sealed class Completion
    {
        public readonly TaskCompletionSource<bool> Source = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Accepted;
        public bool KeepOpenOnAccept;
    }

    private static readonly DependencyProperty ConfiguredProperty = DependencyProperty.RegisterAttached(
        "Configured", typeof(bool), typeof(IndependentWindow), new PropertyMetadata(false));
    private static readonly DependencyProperty CompletionProperty = DependencyProperty.RegisterAttached(
        "Completion", typeof(Completion), typeof(IndependentWindow));

    public static void Configure(Window window)
    {
        window.Owner = null;
        window.ShowInTaskbar = true;
        window.Topmost = false;
        window.ResizeMode = ResizeMode.CanResize;
        window.MinWidth = window.MinHeight = 0;
        window.MaxWidth = window.MaxHeight = double.PositiveInfinity;
        if (window.WindowStartupLocation == WindowStartupLocation.CenterOwner)
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if ((bool)window.GetValue(ConfiguredProperty)) return;
        window.SetValue(ConfiguredProperty, true);
        // IsCancel still raises Click in a modeless window, but WPF's dialog command does not close it.
        window.AddHandler(Button.ClickEvent, new RoutedEventHandler((_, e) =>
        {
            if (e.OriginalSource is Button { IsCancel: true }) window.Close();
        }));
    }

    public static void Show(Window window)
    {
        Configure(window);
        window.Show();
    }

    public static Task<bool> ShowAsync(Window window, bool keepOpenOnAccept = false)
    {
        var completion = new Completion { KeepOpenOnAccept = keepOpenOnAccept };
        window.SetValue(CompletionProperty, completion);
        void Closed(object? sender, EventArgs e)
        {
            window.Closed -= Closed;
            window.ClearValue(CompletionProperty);
            completion.Source.TrySetResult(completion.Accepted);
        }
        window.Closed += Closed;
        try { Show(window); }
        catch
        {
            window.Closed -= Closed;
            window.ClearValue(CompletionProperty);
            throw;
        }
        return completion.Source.Task;
    }

    public static void Accept(Window window)
    {
        if (window.GetValue(CompletionProperty) is Completion completion)
        {
            completion.Accepted = true;
            if (completion.KeepOpenOnAccept)
            {
                window.IsEnabled = false;
                completion.Source.TrySetResult(true);
                return;
            }
            window.Close();
            if (window.IsVisible) completion.Accepted = false; // A pending operation vetoed closing.
        }
        else if ((bool)window.GetValue(ConfiguredProperty)) window.Close();
        else window.DialogResult = true; // Preserve shared forms used by the data manager.
    }
}
