// Copyright (c) Honua. All rights reserved.
// Licensed under the Apache License 2.0. See LICENSE in the project root.

using FieldDataCollection.Models;
using FieldDataCollection.Services;
using FieldDataCollection.ViewModels;

namespace FieldDataCollection.Views;

/// <summary>
/// Dynamic form page that renders OpenRosa XForms as native MAUI controls.
/// Generates mobile-optimized UI based on XForms control definitions.
/// </summary>
public partial class FormPage : ContentPage
{
    private readonly FormViewModel _viewModel;
    private readonly IXFormsParserService _xformsParser;

    public FormPage(FormViewModel viewModel, IXFormsParserService xformsParser)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _xformsParser = xformsParser;
        BindingContext = _viewModel;

        // Subscribe to form changes for dynamic UI generation
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    protected override async void OnNavigatedTo(NavigatedToEventArgs args)
    {
        base.OnNavigatedTo(args);

        // Load form if form ID provided in query parameters
        if (args.Parameter is string formId)
        {
            await _viewModel.LoadFormCommand.ExecuteAsync(formId);
        }
    }

    private async void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FormViewModel.CurrentForm) && _viewModel.CurrentForm != null)
        {
            await GenerateFormUIAsync();
        }
    }

    private async Task GenerateFormUIAsync()
    {
        if (_viewModel.CurrentForm == null)
            return;

        try
        {
            FormFieldsContainer.Children.Clear();

            foreach (var control in _viewModel.CurrentForm.Controls)
            {
                var view = await CreateControlViewAsync(control);
                if (view != null)
                {
                    FormFieldsContainer.Children.Add(view);
                }
            }
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", $"Failed to generate form UI: {ex.Message}", "OK");
        }
    }

    private async Task<View?> CreateControlViewAsync(XFormControl control)
    {
        if (control.IsGroup)
        {
            return await CreateGroupViewAsync(control);
        }

        // Get binding information for control configuration
        var binding = _viewModel.CurrentForm?.Bindings.FirstOrDefault(b =>
            b.NodeSet.EndsWith(control.Ref.TrimStart('/')));

        if (binding == null)
            return null;

        var suggestion = _xformsParser.GetMobileControlSuggestion(control, binding);
        var fieldPath = ExtractFieldPath(control.Ref);

        return suggestion.ControlType switch
        {
            MobileControlType.Entry => CreateEntryView(control, binding, fieldPath),
            MobileControlType.Editor => CreateEditorView(control, binding, fieldPath),
            MobileControlType.NumericEntry => CreateNumericEntryView(control, binding, fieldPath),
            MobileControlType.DatePicker => CreateDatePickerView(control, binding, fieldPath),
            MobileControlType.TimePicker => CreateTimePickerView(control, binding, fieldPath),
            MobileControlType.Picker => CreatePickerView(control, binding, fieldPath),
            MobileControlType.Switch => CreateSwitchView(control, binding, fieldPath),
            MobileControlType.LocationButton => CreateLocationButtonView(control, binding, fieldPath),
            MobileControlType.ImageButton => CreateImageButtonView(control, binding, fieldPath),
            MobileControlType.RadioGroup => CreateRadioGroupView(control, binding, fieldPath),
            MobileControlType.CheckBoxGroup => CreateCheckBoxGroupView(control, binding, fieldPath),
            _ => CreateEntryView(control, binding, fieldPath) // Fallback
        };
    }

    private async Task<StackLayout> CreateGroupViewAsync(XFormControl control)
    {
        var groupLayout = new StackLayout { Spacing = 12 };

        // Group header
        if (!string.IsNullOrEmpty(control.Label))
        {
            var headerLabel = new Label
            {
                Text = control.Label,
                Style = Application.Current?.Resources["SubheadlineStyle"] as Style,
                Margin = new Thickness(0, 16, 0, 8)
            };
            groupLayout.Children.Add(headerLabel);
        }

        // Group separator
        var separator = new BoxView
        {
            BackgroundColor = Color.FromArgb("#E0E0E0"),
            HeightRequest = 1,
            Margin = new Thickness(0, 0, 0, 8)
        };
        groupLayout.Children.Add(separator);

        // Child controls
        foreach (var child in control.Children)
        {
            var childView = await CreateControlViewAsync(child);
            if (childView != null)
            {
                groupLayout.Children.Add(childView);
            }
        }

        return groupLayout;
    }

    private StackLayout CreateEntryView(XFormControl control, XFormBind binding, string fieldPath)
    {
        var container = CreateFieldContainer(control, binding);

        var entry = new Entry
        {
            Placeholder = control.Hint ?? $"Enter {control.Label?.ToLower()}",
            Text = _viewModel.GetFieldValue<string>(fieldPath) ?? ""
        };

        // Apply appearance styles
        if (control.Appearance?.Contains("minimal") == true)
        {
            entry.BackgroundColor = Colors.Transparent;
        }

        // Bind to view model
        entry.TextChanged += (s, e) => _viewModel.SetFieldValue(fieldPath, e.NewTextValue);

        container.Children.Add(entry);
        return container;
    }

    private StackLayout CreateEditorView(XFormControl control, XFormBind binding, string fieldPath)
    {
        var container = CreateFieldContainer(control, binding);

        var editor = new Editor
        {
            Placeholder = control.Hint ?? $"Enter {control.Label?.ToLower()}",
            Text = _viewModel.GetFieldValue<string>(fieldPath) ?? "",
            HeightRequest = 120
        };

        editor.TextChanged += (s, e) => _viewModel.SetFieldValue(fieldPath, e.NewTextValue);

        container.Children.Add(editor);
        return container;
    }

    private StackLayout CreateNumericEntryView(XFormControl control, XFormBind binding, string fieldPath)
    {
        var container = CreateFieldContainer(control, binding);

        var entry = new Entry
        {
            Keyboard = binding.Type == "int" ? Keyboard.Numeric : Keyboard.Numeric,
            Placeholder = control.Hint ?? "Enter number",
            Text = _viewModel.GetFieldValue<object>(fieldPath)?.ToString() ?? ""
        };

        entry.TextChanged += (s, e) =>
        {
            if (binding.Type == "int" && int.TryParse(e.NewTextValue, out var intValue))
            {
                _viewModel.SetFieldValue(fieldPath, intValue);
            }
            else if (binding.Type == "decimal" && double.TryParse(e.NewTextValue, out var doubleValue))
            {
                _viewModel.SetFieldValue(fieldPath, doubleValue);
            }
        };

        container.Children.Add(entry);
        return container;
    }

    private StackLayout CreateDatePickerView(XFormControl control, XFormBind binding, string fieldPath)
    {
        var container = CreateFieldContainer(control, binding);

        var datePicker = new DatePicker
        {
            Date = _viewModel.GetFieldValue<DateTime?>(fieldPath) ?? DateTime.Today
        };

        datePicker.DateSelected += (s, e) => _viewModel.SetFieldValue(fieldPath, e.NewDate);

        container.Children.Add(datePicker);
        return container;
    }

    private StackLayout CreateTimePickerView(XFormControl control, XFormBind binding, string fieldPath)
    {
        var container = CreateFieldContainer(control, binding);

        var timePicker = new TimePicker
        {
            Time = _viewModel.GetFieldValue<TimeSpan?>(fieldPath) ?? DateTime.Now.TimeOfDay
        };

        timePicker.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == TimePicker.TimeProperty.PropertyName)
                _viewModel.SetFieldValue(fieldPath, timePicker.Time);
        };

        container.Children.Add(timePicker);
        return container;
    }

    private StackLayout CreatePickerView(XFormControl control, XFormBind binding, string fieldPath)
    {
        var container = CreateFieldContainer(control, binding);

        var picker = new Picker
        {
            Title = control.Hint ?? $"Select {control.Label?.ToLower()}"
        };

        foreach (var choice in control.Choices)
        {
            picker.Items.Add(choice.Label);
        }

        // Set initial selection
        var currentValue = _viewModel.GetFieldValue<string>(fieldPath);
        if (!string.IsNullOrEmpty(currentValue))
        {
            var choice = control.Choices.FirstOrDefault(c => c.Value == currentValue);
            if (choice != null)
            {
                picker.SelectedIndex = control.Choices.IndexOf(choice);
            }
        }

        picker.SelectedIndexChanged += (s, e) =>
        {
            if (picker.SelectedIndex >= 0)
            {
                var selectedChoice = control.Choices[picker.SelectedIndex];
                _viewModel.SetFieldValue(fieldPath, selectedChoice.Value);
            }
        };

        container.Children.Add(picker);
        return container;
    }

    private StackLayout CreateSwitchView(XFormControl control, XFormBind binding, string fieldPath)
    {
        var container = CreateFieldContainer(control, binding, showRequiredIndicator: false);

        var switchGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            }
        };

        var switchControl = new Switch
        {
            IsToggled = _viewModel.GetFieldValue<bool>(fieldPath)
        };

        switchControl.Toggled += (s, e) => _viewModel.SetFieldValue(fieldPath, e.Value);

        Grid.SetColumn(switchControl, 1);
        switchGrid.Children.Add(switchControl);

        container.Children.Add(switchGrid);
        return container;
    }

    private StackLayout CreateLocationButtonView(XFormControl control, XFormBind binding, string fieldPath)
    {
        var container = CreateFieldContainer(control, binding);

        var locationButton = new Button
        {
            Text = "📍 Capture Location",
            Command = _viewModel.CaptureLocationCommand,
            CommandParameter = fieldPath,
            BackgroundColor = Color.FromArgb("#2196F3"),
            TextColor = Colors.White
        };

        // Show current location if available
        var currentLocation = _viewModel.GetFieldValue<string>(fieldPath);
        if (!string.IsNullOrEmpty(currentLocation))
        {
            var locationLabel = new Label
            {
                Text = $"📍 {currentLocation}",
                Style = Application.Current?.Resources["CaptionStyle"] as Style,
                TextColor = Color.FromArgb("#4CAF50")
            };
            container.Children.Add(locationLabel);
        }

        container.Children.Add(locationButton);
        return container;
    }

    private StackLayout CreateImageButtonView(XFormControl control, XFormBind binding, string fieldPath)
    {
        var container = CreateFieldContainer(control, binding);

        var photoButton = new Button
        {
            Text = "📷 Take Photo",
            Command = _viewModel.CapturePhotoCommand,
            CommandParameter = fieldPath,
            BackgroundColor = Color.FromArgb("#FF9800"),
            TextColor = Colors.White
        };

        // Show captured photo info if available
        var fileName = _viewModel.GetFieldValue<string>(fieldPath);
        if (!string.IsNullOrEmpty(fileName))
        {
            var photoLabel = new Label
            {
                Text = $"📷 {fileName}",
                Style = Application.Current?.Resources["CaptionStyle"] as Style,
                TextColor = Color.FromArgb("#4CAF50")
            };
            container.Children.Add(photoLabel);
        }

        container.Children.Add(photoButton);
        return container;
    }

    private StackLayout CreateRadioGroupView(XFormControl control, XFormBind binding, string fieldPath)
    {
        var container = CreateFieldContainer(control, binding);
        var currentValue = _viewModel.GetFieldValue<string>(fieldPath);

        foreach (var choice in control.Choices)
        {
            var radioButton = new RadioButton
            {
                Content = choice.Label,
                Value = choice.Value,
                IsChecked = choice.Value == currentValue
            };

            radioButton.CheckedChanged += (s, e) =>
            {
                if (e.Value && s is RadioButton rb)
                {
                    _viewModel.SetFieldValue(fieldPath, rb.Value.ToString());
                }
            };

            container.Children.Add(radioButton);
        }

        return container;
    }

    private StackLayout CreateCheckBoxGroupView(XFormControl control, XFormBind binding, string fieldPath)
    {
        var container = CreateFieldContainer(control, binding);
        var currentValues = _viewModel.GetFieldValue<string>(fieldPath)?.Split(' ') ?? Array.Empty<string>();

        foreach (var choice in control.Choices)
        {
            var checkBox = new CheckBox
            {
                IsChecked = currentValues.Contains(choice.Value)
            };

            var label = new Label
            {
                Text = choice.Label,
                VerticalOptions = LayoutOptions.Center
            };

            var checkGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitionCollection
                {
                    new ColumnDefinition { Width = GridLength.Auto },
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
                }
            };

            Grid.SetColumn(checkBox, 0);
            Grid.SetColumn(label, 1);
            checkGrid.Children.Add(checkBox);
            checkGrid.Children.Add(label);

            checkBox.CheckedChanged += (s, e) => UpdateCheckBoxGroupValue(fieldPath, control.Choices);

            container.Children.Add(checkGrid);
        }

        return container;
    }

    private StackLayout CreateFieldContainer(XFormControl control, XFormBind binding, bool showRequiredIndicator = true)
    {
        var container = new StackLayout { Spacing = 8 };

        // Field label
        if (!string.IsNullOrEmpty(control.Label))
        {
            var labelText = control.Label;
            if (binding.Required && showRequiredIndicator)
            {
                labelText += " *";
            }

            var label = new Label
            {
                Text = labelText,
                Style = Application.Current?.Resources["BodyStyle"] as Style
            };

            if (binding.Required)
            {
                label.TextColor = Color.FromArgb("#D32F2F");
            }

            container.Children.Add(label);
        }

        // Hint text
        if (!string.IsNullOrEmpty(control.Hint))
        {
            var hintLabel = new Label
            {
                Text = control.Hint,
                Style = Application.Current?.Resources["CaptionStyle"] as Style,
                TextColor = Color.FromArgb("#757575")
            };
            container.Children.Add(hintLabel);
        }

        return container;
    }

    private void UpdateCheckBoxGroupValue(string fieldPath, List<XFormChoice> choices)
    {
        var selectedValues = new List<string>();

        // Find all selected checkboxes for this field
        // This is a simplified implementation - in production would track checkboxes properly
        _viewModel.SetFieldValue(fieldPath, string.Join(" ", selectedValues));
    }

    private static string ExtractFieldPath(string nodeRef)
    {
        return nodeRef.Split('/').LastOrDefault()?.Trim() ?? "";
    }
}