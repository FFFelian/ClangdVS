using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ClangdVS;
using StreamJsonRpc;

internal static class FeatureSettingsClientRestartRegression
{
    internal static async Task RunAsync()
    {
        var previousTakeover = ClangdSettings.Instance.TakeOverFromNativeIntelliSense;
        var previousTimeout = ClangdLanguageClient.ShutdownLockTimeout;
        var failures = new List<string>();
        try
        {
            ClangdSettings.Instance.TakeOverFromNativeIntelliSense = true;
            await Check("manual Stop busy preserves latest feature apply", () => BusyRestartAsync(false));
            await Check("cold-start initialization busy preserves feature apply", () => BusyRestartAsync(true));
            await Check("feature lifecycle waiter cancels and times out", WaiterIsBoundedAsync);
            await Check("dispose cancels real client deferred wait", DisposeCancelsWaitAsync);
            await Check("feature retry rechecks inactive and takeover state", RecheckAfterBusyAsync);
            await Check("feature restart waits for initialization and host completion", WaitsForHostAsync);
            await Check("feature permanent failures propagate without retry", PermanentFailuresAsync);
        }
        finally
        {
            ClangdSettings.Instance.TakeOverFromNativeIntelliSense = previousTakeover;
            ClangdLanguageClient.ShutdownLockTimeout = previousTimeout;
        }
        if (failures.Count != 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, failures));

        async Task Check(string name, Func<Task> test)
        {
            try
            {
                await test();
                Console.WriteLine("PASS: " + name);
            }
            catch (Exception ex)
            {
                failures.Add(name + ": " + ex.GetBaseException().Message);
                Console.WriteLine("FAIL: " + failures[failures.Count - 1]);
            }
        }
    }

    private static async Task BusyRestartAsync(bool coldStart)
    {
        using (var host = new Host())
        using (var coordinator = new FeatureSettingsApplyCoordinator())
        using (var serializer = new SemaphoreSlim(1, 1))
        {
            var release = Signal();
            var entered = Signal();
            var waiting = Signal();
            var calls = new List<FeatureSettingsApply>();
            var errors = new List<Exception>();
            host.PublishActive();
            if (coldStart)
            {
                SetField(host.Client, "_initCount", 1);
                host.BeforeInitialize = async () =>
                {
                    entered.TrySetResult(true);
                    await release.Task;
                };
            }
            else
            {
                host.BeforeStop = async () =>
                {
                    entered.TrySetResult(true);
                    await release.Task;
                };
            }
            var preceding = coldStart ? ColdRestart(host.Client) : host.Client.RestartAsync();
            Task run = null;
            try
            {
                await Within(entered.Task);
                // During Stop/initialization, IsRunning may be false. The busy
                // contract must win over that transient state.
                host.UnpublishRpc();
                coordinator.Request(false, true, true, false, false);
                run = coordinator.RunAsync(async work =>
                {
                    await serializer.WaitAsync();
                    try { await work(); }
                    finally { serializer.Release(); }
                }, async (request, token) =>
                {
                    calls.Add(request);
                    try { await FeatureRestart(host.Client); }
                    catch (FeatureSettingsApplyDeferredException deferred)
                    {
                        // Preserve the real client's waiter and observe its entry.
                        throw new FeatureSettingsApplyDeferredException(cancellation =>
                        {
                            var wait = deferred.WaitForReadyAsync(cancellation);
                            waiting.TrySetResult(true);
                            return wait;
                        });
                    }
                }, errors.Add);
                await Within(Task.WhenAny(waiting.Task, run));
                Assert(waiting.Task.IsCompleted && !run.IsCompleted && errors.Count == 0,
                       "busy client discarded feature restart instead of waiting");
                Assert(await serializer.WaitAsync(0), "client wait retained takeover serializer");
                serializer.Release();
                coordinator.Request(true, false, false, false, false);
                await Failure<InvalidOperationException>(host.Client.RestartAsync());
                Assert(calls.Count == 1 && host.Starts == (coldStart ? 1 : 0),
                       "busy apply retried without lifecycle progress");
            }
            finally
            {
                release.TrySetResult(true);
                await Within(preceding);
                if (run != null)
                    await Within(run);
            }
            Assert(calls.Count == 2 && calls[1].Version == 2 && calls[1].DiagnosticsEnabled &&
                   !calls[1].ClearDiagnostics && calls[1].RestartRequired && errors.Count == 0,
                   "real client deferral lost pending restart reason or latest settings");
            Assert(host.Starts == 2 && host.Stops == 2, "deferred apply did not perform exactly one replacement");
        }
    }

    private static async Task WaiterIsBoundedAsync()
    {
        using (var host = new Host())
        {
            var release = Signal();
            host.BeforeStop = () => release.Task;
            var preceding = host.Client.RestartAsync();
            try
            {
                var deferred = await Failure<FeatureSettingsApplyDeferredException>(FeatureRestart(host.Client));
                using (var cancellation = new CancellationTokenSource())
                {
                    var wait = deferred.WaitForReadyAsync(cancellation.Token);
                    Assert(!wait.IsCompleted, "busy lifecycle waiter completed immediately");
                    cancellation.Cancel();
                    await Failure<OperationCanceledException>(wait);
                }
                var previous = ClangdLanguageClient.ShutdownLockTimeout;
                try
                {
                    ClangdLanguageClient.ShutdownLockTimeout = TimeSpan.FromMilliseconds(50);
                    await Failure<TimeoutException>(deferred.WaitForReadyAsync(CancellationToken.None));
                }
                finally { ClangdLanguageClient.ShutdownLockTimeout = previous; }
                Assert(host.Stops == 1 && host.Starts == 0, "waiter disturbed owning restart");
                release.TrySetResult(true);
                await Within(preceding);
                await Within(deferred.WaitForReadyAsync(CancellationToken.None));
                Assert(LifecycleLock(host.Client).CurrentCount == 1, "waiter leaked lifecycle ownership");
            }
            finally
            {
                release.TrySetResult(true);
                await Within(preceding);
            }
        }
    }

    private static async Task DisposeCancelsWaitAsync()
    {
        using (var host = new Host())
        using (var coordinator = new FeatureSettingsApplyCoordinator())
        {
            var release = Signal();
            var waiting = Signal();
            var errors = new List<Exception>();
            host.BeforeStop = () => release.Task;
            var preceding = host.Client.RestartAsync();
            coordinator.Request(true, false, true, false, false);
            var run = coordinator.RunAsync(work => work(), async (request, token) =>
            {
                try { await FeatureRestart(host.Client); }
                catch (FeatureSettingsApplyDeferredException deferred)
                {
                    throw new FeatureSettingsApplyDeferredException(cancellation =>
                    {
                        var wait = deferred.WaitForReadyAsync(cancellation);
                        waiting.TrySetResult(true);
                        return wait;
                    });
                }
            }, errors.Add);
            try
            {
                await Within(Task.WhenAny(waiting.Task, run));
                Assert(waiting.Task.IsCompleted && !run.IsCompleted, "real deferred wait was not entered");
                coordinator.Dispose();
                await Within(run);
                Assert(!preceding.IsCompleted && errors.Count == 0 && host.Starts == 0,
                       "dispose disturbed owning restart or reported cancellation as failure");
            }
            finally
            {
                coordinator.Dispose();
                release.TrySetResult(true);
                await Within(preceding);
                await Within(run);
            }
            Assert(host.Starts == 1 && host.Stops == 1, "dispose replayed pending client restart");
        }
    }

    private static async Task RecheckAfterBusyAsync()
    {
        foreach (var disableTakeover in new[] { false, true })
        using (var host = new Host())
        {
            host.PublishActive();
            var release = Signal();
            host.BeforeStop = () => release.Task;
            var shutdown = host.Client.ShutdownAsync();
            try
            {
                var deferred = await Failure<FeatureSettingsApplyDeferredException>(FeatureRestart(host.Client));
                release.TrySetResult(true);
                await Within(shutdown);
                if (disableTakeover)
                {
                    host.PublishActive();
                    ClangdSettings.Instance.TakeOverFromNativeIntelliSense = false;
                }
                await Within(deferred.WaitForReadyAsync(CancellationToken.None));
                var version = Field(host.Client, "_lifecycleVersion");
                await Within(FeatureRestart(host.Client));
                Assert(host.Starts == 0 && host.Stops == 1 &&
                       Equals(version, Field(host.Client, "_lifecycleVersion")),
                       "inactive/takeover-disabled feature retry cycled lifecycle");
                if (disableTakeover)
                    await Failure<InvalidOperationException>(host.Client.RestartAsync());
                Assert(LifecycleLock(host.Client).CurrentCount == 1, "no-op leaked lifecycle lock");
            }
            finally
            {
                ClangdSettings.Instance.TakeOverFromNativeIntelliSense = true;
                release.TrySetResult(true);
                await Within(shutdown);
            }
        }
    }

    private static async Task WaitsForHostAsync()
    {
        using (var host = new Host())
        {
            host.PublishActive();
            var initialize = Signal();
            var finishHost = Signal();
            var started = Signal();
            var initialized = Signal();
            host.BeforeInitialize = async () =>
            {
                started.TrySetResult(true);
                await initialize.Task;
            };
            host.AfterInitialize = async () =>
            {
                initialized.TrySetResult(true);
                await finishHost.Task;
            };
            var restart = FeatureRestart(host.Client);
            try
            {
                await Within(started.Task);
                Assert(!restart.IsCompleted, "feature restart returned before initialization");
                await Failure<InvalidOperationException>(host.Client.RestartAsync());
                initialize.TrySetResult(true);
                await Within(initialized.Task);
                Assert(!restart.IsCompleted, "feature restart returned before Start host completed");
                var deferred = await Failure<FeatureSettingsApplyDeferredException>(FeatureRestart(host.Client));
                Assert(deferred != null, "second feature restart did not defer");
            }
            finally
            {
                initialize.TrySetResult(true);
                finishHost.TrySetResult(true);
                await Within(restart);
            }
            Assert(host.Stops == 1 && host.Starts == 1 && LifecycleLock(host.Client).CurrentCount == 1,
                   "feature restart overlapped or retained lifecycle ownership");
        }
    }

    private static async Task PermanentFailuresAsync()
    {
        using (var host = new Host(false))
        {
            host.PublishActive();
            await Failure<InvalidOperationException>(FeatureRestart(host.Client));
            Assert(host.Stops == 0, "missing host stopped active client");
        }
        using (var host = new Host())
        using (var coordinator = new FeatureSettingsApplyCoordinator())
        {
            host.PublishActive();
            host.BeforeInitialize = () => host.Client.OnServerInitializeFailedAsync(null);
            var errors = new List<Exception>();
            coordinator.Request(true, false, true, false, false);
            await Within(coordinator.RunAsync(work => work(), (request, token) => FeatureRestart(host.Client), errors.Add));
            Assert(errors.Count == 1 && errors[0] is InvalidOperationException && host.Starts == 1 && host.Stops == 1,
                   "permanent client initialization failure was swallowed or retried");
        }
    }

    // Baseline fallback exercises the old real lifecycle, so RED is an observable
    // behavior failure rather than a compiler error for the not-yet-added API.
    private static Task FeatureRestart(ClangdLanguageClient client)
    {
        var method = typeof(ClangdLanguageClient).GetMethod("RestartForFeatureSettingsAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        return method == null ? client.RestartAsync() : (Task)method.Invoke(client, null);
    }

    private static Task ColdRestart(ClangdLanguageClient client) =>
        (Task)typeof(ClangdLanguageClient).GetMethod("RestartAsync", BindingFlags.Instance | BindingFlags.NonPublic,
            null, new[] { typeof(int?) }, null).Invoke(client, new[] { Field(client, "_lifecycleVersion") });

    // Only replaces the external broker/process-publication boundary. Stop,
    // initialization attribution, locks, waiter and restart completion are real.
    private sealed class Host : IDisposable
    {
        internal readonly ClangdLanguageClient Client = new ClangdLanguageClient();
        private readonly List<Process> _processes = new List<Process>();
        private readonly JsonRpc _rpc = new JsonRpc(new MemoryStream(), new MemoryStream());
        internal int Stops;
        internal int Starts;
        internal Func<Task> BeforeStop = () => Task.CompletedTask;
        internal Func<Task> BeforeInitialize = () => Task.CompletedTask;
        internal Func<Task> AfterInitialize = () => Task.CompletedTask;

        internal Host(bool subscribeStart = true)
        {
            Client.StopAsync += async (_, __) => { Stops++; await BeforeStop(); };
            if (subscribeStart)
                Client.StartAsync += async (_, __) =>
                {
                    Starts++;
                    await BeforeInitialize();
                    PublishActive();
                    typeof(ClangdLanguageClient).GetMethod("RecordServerInitialized",
                        BindingFlags.Instance | BindingFlags.NonPublic).Invoke(Client, null);
                    await AfterInitialize();
                };
        }

        internal void PublishActive()
        {
            // set /p waits on redirected stdin; no clangd install/VS host or child
            // process is needed, and IsRunning checks a genuine OS process.
            var process = Process.Start(new ProcessStartInfo {
                FileName = Path.Combine(System.Environment.SystemDirectory, "cmd.exe"),
                Arguments = "/d /c set /p featureRegression=",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
            });
            _processes.Add(process);
            SetField(Client, "_serverProcess", process);
            SetField(Client, "_rpc", _rpc);
        }

        internal void UnpublishRpc() => SetField(Client, "_rpc", null);

        public void Dispose()
        {
            SetField(Client, "_serverProcess", null);
            UnpublishRpc();
            foreach (var process in _processes)
            {
                try { if (!process.HasExited) process.Kill(); }
                catch (InvalidOperationException) { } // Already disposed by real Stop.
                process.Dispose();
            }
            _rpc.Dispose();
        }
    }

    private static object Field(ClangdLanguageClient client, string name) =>
        typeof(ClangdLanguageClient).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(client);
    private static void SetField(ClangdLanguageClient client, string name, object value) =>
        typeof(ClangdLanguageClient).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(client, value);
    private static SemaphoreSlim LifecycleLock(ClangdLanguageClient client) => (SemaphoreSlim)Field(client, "_lifecycleLock");
    private static TaskCompletionSource<bool> Signal() =>
        new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Within(Task task)
    {
        if (await Task.WhenAny(task, Task.Delay(5000)) != task)
            throw new WatchdogException();
        await task;
    }
    // Never let the test watchdog satisfy an expected client exception assertion.
    private sealed class WatchdogException : Exception
    {
        internal WatchdogException() : base("client feature regression watchdog expired") { }
    }
    private static async Task<T> Failure<T>(Task task) where T : Exception
    {
        try { await Within(task); }
        catch (T error) { return error; }
        throw new InvalidOperationException("expected " + typeof(T).Name + " from real client lifecycle");
    }
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
