namespace Crs.Web.Services;

/// <summary>
/// Imperative toast notifications shown by <c>ToastHost</c>.
/// </summary>
public sealed class ToastService
{
    public const int DefaultDurationMs = 1600;
    public const int ExitDurationMs = 200;

    private readonly List<ToastItem> _toasts = new();
    private int _nextId;

    public event Action? OnChanged;

    public IReadOnlyList<ToastItem> Toasts => _toasts;

    public void Show(string message, ToastVariant variant = ToastVariant.Success, int durationMs = DefaultDurationMs)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var id = ++_nextId;
        _toasts.RemoveAll(toast => toast.Leaving);
        _toasts.Add(new ToastItem
        {
            Id = id,
            Message = message.Trim(),
            Variant = variant,
            Leaving = false
        });
        OnChanged?.Invoke();

        _ = DismissAfterAsync(id, durationMs);
    }

    private async Task DismissAfterAsync(int id, int durationMs)
    {
        await Task.Delay(Math.Max(0, durationMs));

        var toast = _toasts.FirstOrDefault(item => item.Id == id);
        if (toast is null || toast.Leaving)
        {
            return;
        }

        toast.Leaving = true;
        OnChanged?.Invoke();

        await Task.Delay(ExitDurationMs);

        if (_toasts.RemoveAll(item => item.Id == id) > 0)
        {
            OnChanged?.Invoke();
        }
    }
}
