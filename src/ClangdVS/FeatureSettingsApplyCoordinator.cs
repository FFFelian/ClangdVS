using System;
using System.Threading;
using System.Threading.Tasks;

namespace ClangdVS
{
    /// <summary>
    /// Explicitly signals that apply did not perform its restart because another
    /// lifecycle operation owns the client. The waiter must wait for lifecycle
    /// progress (or fail/cancel), never just complete immediately while still busy.
    /// Ordinary restart/initialization errors must not use this contract.
    /// </summary>
    internal sealed class FeatureSettingsApplyDeferredException : Exception
    {
        internal FeatureSettingsApplyDeferredException(Func<CancellationToken, Task> waitForReadyAsync)
            : base("Feature apply deferred until the active lifecycle operation completes.")
        {
            WaitForReadyAsync = waitForReadyAsync ?? throw new ArgumentNullException(nameof(waitForReadyAsync));
        }

        internal Func<CancellationToken, Task> WaitForReadyAsync { get; }
    }

    internal sealed class FeatureSettingsApply
    {
        internal FeatureSettingsApply(long version, bool diagnosticsEnabled, bool clearDiagnostics,
                                      bool restartRequired)
        {
            Version = version;
            DiagnosticsEnabled = diagnosticsEnabled;
            ClearDiagnostics = clearDiagnostics;
            RestartRequired = restartRequired;
        }

        internal long Version { get; }
        internal bool DiagnosticsEnabled { get; }
        internal bool ClearDiagnostics { get; }
        internal bool RestartRequired { get; }
    }

    /// <summary>
    /// One drain loop, with a latest-state snapshot and sticky restart reasons.
    /// Take the snapshot INSIDE the lifecycle serializer: changes received while
    /// waiting for takeover are merged, while changes during apply form the next pass.
    /// </summary>
    internal sealed class FeatureSettingsApplyCoordinator : IDisposable
    {
        private readonly object _gate = new object();
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private long _version;
        private long _takeoverVersion;
        private FeatureSettingsApply _pending;
        private bool _running;
        private bool _disposed;
        private bool _cancellationCompleted;
        private bool _cancellationDisposed;

        internal void Request(bool diagnosticsEnabled, bool diagnosticsChanged, bool inactiveRegionsChanged,
                              bool semanticHighlightingChanged, bool takeoverChanged)
        {
            lock (_gate)
            {
                if (_disposed)
                    return;
                // The takeover transition queued before this event negotiates all
                // settings itself, including any earlier unconsumed feature changes.
                var restart = !takeoverChanged &&
                              (_pending?.RestartRequired == true || diagnosticsChanged ||
                               inactiveRegionsChanged || semanticHighlightingChanged);
                var clear = !diagnosticsEnabled && (diagnosticsChanged || _pending?.ClearDiagnostics == true);
                _pending = new FeatureSettingsApply(++_version, diagnosticsEnabled, clear, restart);
                if (takeoverChanged)
                    _takeoverVersion = _version;
            }
        }

        internal Task RunAsync(Func<Func<Task>, Task> serialize,
                               Func<FeatureSettingsApply, CancellationToken, Task> apply,
                               Action<Exception> failed)
        {
            lock (_gate)
            {
                if (_disposed || _running || _pending == null)
                    return Task.CompletedTask;
                _running = true;
            }
            return DrainAsync(serialize, apply, failed);
        }

        private async Task DrainAsync(Func<Func<Task>, Task> serialize,
                                      Func<FeatureSettingsApply, CancellationToken, Task> apply,
                                      Action<Exception> failed)
        {
            var ownsRunner = true;
            try
            {
                while (true)
                {
                    lock (_gate)
                    {
                        if (_disposed || _pending == null)
                        {
                            // Release ownership atomically with the empty check, so
                            // a request at the idle boundary can start another runner.
                            _running = false;
                            ownsRunner = false;
                            return;
                        }
                    }

                    try
                    {
                        FeatureSettingsApply deferredSnapshot = null;
                        FeatureSettingsApplyDeferredException deferred = null;
                        CancellationToken waitToken = default;
                        await serialize(async () =>
                        {
                            FeatureSettingsApply snapshot;
                            CancellationToken token;
                            lock (_gate)
                            {
                                if (_disposed || _pending == null)
                                    return;
                                snapshot = _pending;
                                _pending = null;
                                token = _lifetime.Token;
                            }
                            token.ThrowIfCancellationRequested();
                            try
                            {
                                await apply(snapshot, token);
                            }
                            catch (FeatureSettingsApplyDeferredException ex)
                            {
                                deferredSnapshot = snapshot;
                                deferred = ex;
                                waitToken = token;
                            }
                        });
                        if (deferred != null)
                        {
                            // Waiting is outside the takeover serializer: takeover
                            // must be able to turn off while the client is busy.
                            // Only the explicit busy contract is replayable; a failed
                            // wait or ordinary apply failure is reported once.
                            await deferred.WaitForReadyAsync(waitToken);
                            waitToken.ThrowIfCancellationRequested();
                            lock (_gate)
                            {
                                if (!_disposed)
                                {
                                    var latest = _pending ?? deferredSnapshot;
                                    _pending = new FeatureSettingsApply(latest.Version, latest.DiagnosticsEnabled,
                                        !latest.DiagnosticsEnabled &&
                                        (latest.ClearDiagnostics || deferredSnapshot.ClearDiagnostics),
                                        latest.RestartRequired ||
                                        (deferredSnapshot.RestartRequired && _takeoverVersion <= deferredSnapshot.Version));
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        failed(ex);
                        // A failed restart must not discard changes received during
                        // it. Do not retry the failed version indefinitely, however.
                    }
                }
            }
            finally
            {
                lock (_gate)
                {
                    if (ownsRunner)
                        _running = false;
                    DisposeCancellationIfIdle();
                }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;
                _disposed = true;
                _pending = null;
            }
            try
            {
                _lifetime.Cancel();
            }
            finally
            {
                lock (_gate)
                {
                    _cancellationCompleted = true;
                    DisposeCancellationIfIdle();
                }
            }
        }

        // Called under _gate. An in-flight callback may still register with its
        // token, so dispose the source only after both cancellation and drain end.
        private void DisposeCancellationIfIdle()
        {
            if (_cancellationCompleted && !_running && !_cancellationDisposed)
            {
                _cancellationDisposed = true;
                _lifetime.Dispose();
            }
        }
    }
}
