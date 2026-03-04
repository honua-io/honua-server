using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FieldDataCollection.ViewModels;

/// <summary>
/// Base view model providing common functionality for data binding and property notification.
/// </summary>
public abstract class BaseViewModel : INotifyPropertyChanged
{
    private bool _isBusy;
    private string _title = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Gets or sets whether the view model is currently performing an operation.
    /// </summary>
    public bool IsBusy
    {
        get => _isBusy;
        set => SetProperty(ref _isBusy, value);
    }

    /// <summary>
    /// Gets whether the view model is not busy.
    /// </summary>
    public bool IsNotBusy => !IsBusy;

    /// <summary>
    /// Gets or sets the title for the view.
    /// </summary>
    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    /// <summary>
    /// Sets a property value and raises PropertyChanged if the value has changed.
    /// </summary>
    /// <typeparam name="T">Type of the property.</typeparam>
    /// <param name="backingStore">Reference to the backing field.</param>
    /// <param name="value">New value to set.</param>
    /// <param name="propertyName">Name of the property (automatically filled by compiler).</param>
    /// <returns>True if the property value changed.</returns>
    protected bool SetProperty<T>(ref T backingStore, T value, [CallerMemberName] string? propertyName = "")
    {
        if (EqualityComparer<T>.Default.Equals(backingStore, value))
            return false;

        backingStore = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    /// <summary>
    /// Raises the PropertyChanged event.
    /// </summary>
    /// <param name="propertyName">Name of the property that changed.</param>
    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = "")
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        // Also notify IsNotBusy when IsBusy changes
        if (propertyName == nameof(IsBusy))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsNotBusy)));
        }
    }

    /// <summary>
    /// Executes an async operation while setting IsBusy and handling exceptions.
    /// </summary>
    /// <param name="operation">The async operation to execute.</param>
    /// <param name="onError">Optional error handler.</param>
    protected async Task ExecuteAsync(Func<Task> operation, Action<Exception>? onError = null)
    {
        if (IsBusy)
            return;

        try
        {
            IsBusy = true;
            await operation();
        }
        catch (Exception ex)
        {
            onError?.Invoke(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Executes an async operation with return value while setting IsBusy and handling exceptions.
    /// </summary>
    /// <typeparam name="T">Return type of the operation.</typeparam>
    /// <param name="operation">The async operation to execute.</param>
    /// <param name="onError">Optional error handler.</param>
    /// <returns>Result of the operation or default value if exception occurred.</returns>
    protected async Task<T?> ExecuteAsync<T>(Func<Task<T>> operation, Action<Exception>? onError = null)
    {
        if (IsBusy)
            return default;

        try
        {
            IsBusy = true;
            return await operation();
        }
        catch (Exception ex)
        {
            onError?.Invoke(ex);
            return default;
        }
        finally
        {
            IsBusy = false;
        }
    }
}