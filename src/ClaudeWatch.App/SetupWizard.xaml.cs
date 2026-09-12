using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClaudeWatch.Core;

namespace ClaudeWatch.App;

/// <summary>
/// The questions asked once, after installing. Kept to four, and every one of
/// them changes what the app does: a wizard that asks things the app could work
/// out for itself is just a delay with buttons.
/// </summary>
public partial class SetupWizard : Window
{
    private readonly MainViewModel _model;
    private readonly List<StackPanel> _steps = new();

    private int _step;

    public SetupWizard(MainViewModel model)
    {
        _model = model;
        InitializeComponent();
        DataContext = model;

        Loaded += (_, _) =>
        {
            _steps.Add(StepLanguage);
            _steps.Add(StepApps);
            _steps.Add(StepZone);
            _steps.Add(StepKey);

            WantClaude.IsChecked = model.Edit.ByKey(ServiceProfile.ClaudeKey)?.Enabled ?? true;
            WantChatGpt.IsChecked = model.Edit.ByKey(ServiceProfile.ChatGptKey)?.Enabled ?? false;

            Show(0);
        };
    }

    private void Show(int index)
    {
        _step = Math.Clamp(index, 0, _steps.Count - 1);

        for (var i = 0; i < _steps.Count; i++)
        {
            _steps[i].Visibility = i == _step ? Visibility.Visible : Visibility.Collapsed;
        }

        BackButton.Visibility = _step == 0 ? Visibility.Collapsed : Visibility.Visible;
        NextButton.Content = _step == _steps.Count - 1 ? _model.L["Wiz_Finish"] : _model.L["Wiz_Next"];
        StepDots.Text = $"{_step + 1} / {_steps.Count}";
        AppsWarning.Text = string.Empty;
    }

    private void OnBack(object sender, RoutedEventArgs e) => Show(_step - 1);

    private void OnNext(object sender, RoutedEventArgs e)
    {
        // Leaving with nothing ticked would install a guard that guards nothing.
        if (_steps[_step] == StepApps && WantClaude.IsChecked != true && WantChatGpt.IsChecked != true)
        {
            AppsWarning.Text = _model.L["Wiz_Apps_Sub"];
            return;
        }

        if (_step < _steps.Count - 1)
        {
            Show(_step + 1);
            return;
        }

        Finish();
    }

    private void OnSkip(object sender, RoutedEventArgs e) => Finish();

    private void Finish()
    {
        try
        {
            if (_model.Edit.ByKey(ServiceProfile.ClaudeKey) is { } claude)
            {
                claude.Enabled = WantClaude.IsChecked == true;
            }

            if (_model.Edit.ByKey(ServiceProfile.ChatGptKey) is { } chatgpt)
            {
                chatgpt.Enabled = WantChatGpt.IsChecked == true;
            }

            // Land on one the user actually uses, rather than whatever was first.
            var firstOn = _model.Edit.Services.FirstOrDefault(s => s.Enabled);
            if (firstOn is not null)
            {
                _model.Edit.ActiveServiceKey = firstOn.Key;
            }

            _model.Edit.SetupCompleted = true;
            _model.CompleteSetup();
        }
        catch
        {
            // A wizard that throws on the way out must not block the app.
        }

        DialogResult = true;
        Close();
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch
        {
        }
    }
}
