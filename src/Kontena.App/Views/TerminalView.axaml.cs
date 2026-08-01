using System;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Exclr8.Terminal;
using Kontena.App.ViewModels;
using Kontena.Sdk.Models;
using Kontena.Sdk;
using Kontena.Core.Orchestration;

namespace Kontena.App.Views;

/// <summary>
/// Hosts the Exclr8 <see cref="TerminalControl"/> and bridges it to an engine
/// <see cref="IExecSession"/>: engine output is written into the terminal, user
/// keystrokes (<c>Input</c>) and resizes (<c>Resized</c>) go back to the session.
/// The session starts lazily the first time the Terminal tab is shown and is
/// torn down when the page goes away.
/// </summary>
[SuppressMessage("Reliability", "CA1001",
    Justification = "Control lifetime is bound to DetachedFromVisualTree, which tears the session and CTS down; controls are not IDisposable.")]
public partial class TerminalView : UserControl
{
    private ITerminalHost? _vm;
    private IExecSession? _session;
    private CancellationTokenSource? _cts;
    private bool _started;

    public TerminalView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += (_, _) => Teardown();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null)
            _vm.PropertyChanged -= OnVmPropertyChanged;

        _vm = DataContext as ITerminalHost;

        if (_vm is not null)
        {
            _vm.PropertyChanged += OnVmPropertyChanged;

            // Exclr8's font props are plain CLR properties, so set them in code.
            Term.FontFamily = _vm.TerminalFontFamily;
            Term.FontSize = _vm.TerminalFontSize;
            Term.EnableLigatures = _vm.TerminalLigatures;
            ShellLabel.Text = _vm.ShellLabel;
        }

        MaybeStart();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ITerminalHost.IsTerminalSelected)
            or nameof(ITerminalHost.CanOpenTerminal))
            MaybeStart();
    }

    private void MaybeStart()
    {
        if (_started || _vm is null || !_vm.IsTerminalSelected)
            return;

        if (!_vm.CanOpenTerminal)
        {
            SetStatus("start the container to open a shell", ok: false);
            return;
        }

        _started = true;
        _ = StartAsync();
    }

    private async Task StartAsync()
    {
        try
        {
            _cts = new CancellationTokenSource();
            _session = await _vm!.OpenExecSessionAsync(_cts.Token);

            Term.Input += OnInput;
            Term.Resized += OnResized;

            // Match the PTY to the control's current grid, then start pumping output.
            await _session.ResizeAsync(Term.Buffer.Cols, Term.Buffer.Rows, _cts.Token);

            SetStatus("connected", ok: true);
            Term.Focus();

            _ = PumpAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            SetStatus("failed to connect", ok: false);
            Term.Write(System.Text.Encoding.UTF8.GetBytes($"\r\n[failed to start session: {ex.Message}]\r\n"));
        }
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var chunk in _session!.ReadOutputAsync(ct))
                Term.Write(chunk.Span);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch
        {
            // engine hiccup — fall through to the ended message
        }

        if (!ct.IsCancellationRequested)
        {
            SetStatus("session ended", ok: false);
            Term.Write(System.Text.Encoding.UTF8.GetBytes("\r\n[session ended]\r\n"));
        }
    }

    private async void OnInput(object? sender, ReadOnlyMemory<byte> data)
    {
        try
        {
            if (_session is not null)
                await _session.WriteAsync(data, _cts?.Token ?? default);
        }
        catch
        {
            // input after the session closed — ignore
        }
    }

    private async void OnResized(object? sender, (int Cols, int Rows) size)
    {
        try
        {
            if (_session is not null)
                await _session.ResizeAsync(size.Cols, size.Rows, _cts?.Token ?? default);
        }
        catch
        {
            // resize is best-effort
        }
    }

    private void OnReconnectClick(object? sender, RoutedEventArgs e)
    {
        // Reconnect means "give me a new one", so this is the case that ends a session the page would
        // otherwise keep.
        Teardown(discard: true);
        Term.PrepareForNewSession();
        _started = false;
        SetStatus("connecting…", ok: false);
        MaybeStart();
    }

    private void Teardown(bool discard = false)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        Term.Input -= OnInput;
        Term.Resized -= OnResized;

        var session = _session;
        var vm = _vm;
        _session = null;

        // Handed back rather than disposed here: whether a session outlives its view is the page's
        // decision, not the view's. A container shell ends; a shell on this machine keeps running.
        if (session is not null && vm is not null)
            _ = vm.ReleaseExecSessionAsync(session, discard).AsTask();
    }

    private void SetStatus(string text, bool ok)
    {
        Dispatcher.UIThread.Post(() =>
        {
            StatusText.Text = text;
            StatusDot.Fill = ok
                ? new SolidColorBrush(Color.Parse("#34D399"))
                : new SolidColorBrush(Color.Parse("#5C6675"));
        });
    }
}
