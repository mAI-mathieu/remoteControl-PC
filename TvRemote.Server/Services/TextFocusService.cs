using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace TvRemote.Services;

public sealed record TextFocusState(bool Editable, bool Password = false, long Revision = 0);
public interface ITextFocusService
{
    TextFocusState Current { get; }
    event Action<TextFocusState>? Changed;
    void RequestRefresh();
}

public sealed class TextFocusService(ILogger<TextFocusService> logger) : ITextFocusService, IHostedService, IDisposable
{
    private readonly AutoResetEvent refresh = new(false);
    private readonly CancellationTokenSource stop = new();
    private TextFocusState current = new(false);
    private Thread? worker;
    private int[]? previousId;
    private int disposed;
    public TextFocusState Current => Volatile.Read(ref current);
    public event Action<TextFocusState>? Changed;
    public void RequestRefresh()
    {
        if (Volatile.Read(ref disposed) != 0 || stop.IsCancellationRequested) return;
        try { refresh.Set(); } catch (ObjectDisposedException) { }
    }
    public Task StartAsync(CancellationToken cancellationToken)
    {
        worker = new Thread(MonitorFocus) { IsBackground = true, Name = "TV Remote text focus" };
        worker.SetApartmentState(ApartmentState.MTA); worker.Start();
        return Task.CompletedTask;
    }
    private void MonitorFocus()
    {
        AutomationFocusChangedEventHandler handler = (_, _) => RequestRefresh();
        var subscribed = false;
        try
        {
            Automation.AddAutomationFocusChangedEventHandler(handler); subscribed = true;
            ReadFocus();
            while (WaitHandle.WaitAny([stop.Token.WaitHandle, refresh]) == 1) ReadFocus();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException)
        { logger.LogWarning("Automatic text focus detection unavailable ({Reason})", ex.GetType().Name); }
        finally
        {
            if (subscribed)
            {
                try { Automation.RemoveAutomationFocusChangedEventHandler(handler); }
                catch (Exception ex) when (ex is COMException or InvalidOperationException) { }
            }
        }
    }
    private void ReadFocus()
    {
        try
        {
            var element = AutomationElement.FocusedElement;
            var editable = false; var password = false; int[]? id = null;
            if (element != null)
            {
                var info = element.Current;
                id = element.GetRuntimeId();
                if (info.ProcessId != Environment.ProcessId && info.IsEnabled && info.IsKeyboardFocusable && info.HasKeyboardFocus)
                {
                    var kind = info.ControlType;
                    if (kind == ControlType.Edit || kind == ControlType.Document || kind == ControlType.ComboBox)
                    {
                        // Only read capability metadata. Never read names, values or document text.
                        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var value)) editable = !((ValuePattern)value).Current.IsReadOnly;
                        else if (element.TryGetCurrentPattern(TextPattern.Pattern, out var text))
                            editable = ((TextPattern)text).DocumentRange.GetAttributeValue(TextPattern.IsReadOnlyAttribute) is false;
                        else editable = info.IsPassword && kind == ControlType.Edit;
                    }
                    password = editable && info.IsPassword;
                }
            }
            var before = Current;
            if (editable == before.Editable && password == before.Password && (id == null ? previousId == null : previousId != null && id.SequenceEqual(previousId))) return;
            previousId = id;
            var state = new TextFocusState(editable, password, before.Revision + 1);
            Volatile.Write(ref current, state); Changed?.Invoke(state);
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            var before = Current;
            if (!before.Editable) return;
            previousId = null;
            var state = new TextFocusState(false, Revision: before.Revision + 1);
            Volatile.Write(ref current, state); Changed?.Invoke(state);
        }
    }
    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref disposed) != 0) return Task.CompletedTask;
        stop.Cancel();
        // A hung accessibility provider must not hang server shutdown.
        worker?.Join(1000); return Task.CompletedTask;
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stop.Cancel();
        if (worker == null || !worker.IsAlive) { refresh.Dispose(); stop.Dispose(); }
    }
}
