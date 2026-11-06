using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ClangdVS;
using StreamJsonRpc;

// The focused runner can call RunAsync without changing Program. Client lifecycle
// coverage uses a headless host adapter, without requiring running VS services.
internal static class FeatureSettingsApplyRegression
{
    internal static async Task RunAsync()
    {
        await TakeoverWaitsForRealLifecycleAsync();
        await TakeoverOffCancelsBusyWaitAsync();
        await TakeoverStartsInactiveClientAsync();
        await SupersededTakeoverDoesNotRestartAsync();
        await FeatureSettingsClientRestartRegression.RunAsync();
        await DeferredRestartWaitsAndKeepsLatestAsync();
        await DeferredRestartWithoutNewRequestAsync();
        await PermanentFailureIsNotRetriedAsync();
        await DisposeCancelsDeferredWaitAsync();
        await FailedWaitIsNotRetriedAsync();
        await TakeoverDuringDeferredWaitConsumesRestartAsync();
        await MergesLatestWhileWaitingForTakeoverAsync();
        await ReplaysChangesDuringRestartAsync();
        await PendingSurvivesFailureAsync();
        await DisposeCancelsPendingAsync();
        await DisposeCancelsActiveApplyAsync();
        await RestartReasonsAsync();
        await TakeoverConsumesPendingRestartReasonsAsync();
        await DisposeDoesNotReleaseInFlightRestartAsync();
    }

    private static async Task TakeoverWaitsForRealLifecycleAsync()
    {
        var settings = ClangdSettings.Instance;
        var previousTakeover = settings.TakeOverFromNativeIntelliSense;
        var previousInactive = settings.EnableInactiveRegions;
        settings.TakeOverFromNativeIntelliSense = true;
        settings.EnableInactiveRegions = false;
        try
        {
            using (var host = new TakeoverHost())
            using (var coordinator = new FeatureSettingsApplyCoordinator())
            {
                var initialize = Signal();
                var entered = Signal();
                var waiting = Signal();
                var starts = 0;
                var negotiatedInactive = false;
                host.Client.StartAsync += async (_, __) =>
                {
                    starts++;
                    var capability = settings.EnableInactiveRegions;
                    if (starts == 1)
                    {
                        entered.TrySetResult(true);
                        await initialize.Task;
                    }
                    host.PublishActive();
                    typeof(ClangdLanguageClient).GetMethod("RecordServerInitialized",
                        BindingFlags.Instance | BindingFlags.NonPublic).Invoke(host.Client, null);
                    negotiatedInactive = capability;
                };
                var manual = host.Client.RestartAsync();
                Task takeover = null;
                try
                {
                    await Within(entered.Task);
                    settings.TakeOverFromNativeIntelliSense = false;
                    coordinator.Request(true, false, false, false, true);
                    settings.TakeOverFromNativeIntelliSense = true;
                    settings.EnableInactiveRegions = true;
                    coordinator.Request(true, false, true, false, true);
                    var loads = 0;
                    takeover = SequencedTakeover(async () =>
                    {
                        await TakeoverRuntimeController.ApplyAsync(true, null, async () =>
                        {
                            try { await TakeoverRestart(host.Client); }
                            catch (FeatureSettingsApplyDeferredException)
                            {
                                waiting.TrySetResult(true);
                                throw;
                            }
                        }, () => { loads++; return Task.CompletedTask; });
                    });
                    await Within(Task.WhenAny(waiting.Task, takeover));
                    Assert(waiting.Task.IsCompleted && !takeover.IsCompleted && loads == 0,
                           "busy takeover used cached broker load instead of awaiting a real restart");
                    // Readiness must not retain the serializer needed by off/other transitions.
                    await Within(TakeoverTransitionSerializer.ExecuteAsync(() => Task.CompletedTask));
                    initialize.TrySetResult(true);
                    await Within(manual);
                    await Within(takeover);
                    var featureRestarts = 0;
                    await Within(coordinator.RunAsync(TakeoverTransitionSerializer.ExecuteAsync, (request, token) =>
                    {
                        if (request.RestartRequired)
                            featureRestarts++;
                        return Task.CompletedTask;
                    }, Unexpected));
                    Assert(starts == 2 && negotiatedInactive && featureRestarts == 0 && loads == 0,
                           "off/on lost inactive-region negotiation or caused a duplicate feature restart");
                }
                finally
                {
                    initialize.TrySetResult(true);
                    await Within(manual);
                    if (takeover != null)
                        await Within(takeover);
                }
            }
        }
        finally
        {
            settings.TakeOverFromNativeIntelliSense = previousTakeover;
            settings.EnableInactiveRegions = previousInactive;
        }
    }

    private static async Task TakeoverOffCancelsBusyWaitAsync()
    {
        var settings = ClangdSettings.Instance;
        var previous = settings.TakeOverFromNativeIntelliSense;
        settings.TakeOverFromNativeIntelliSense = true;
        try
        {
            var waiting = Signal();
            var cancelled = Signal();
            var calls = 0;
            var run = SequencedTakeover(() =>
            {
                calls++;
                throw new FeatureSettingsApplyDeferredException(async token =>
                {
                    waiting.TrySetResult(true);
                    try { await Task.Delay(Timeout.Infinite, token); }
                    finally { cancelled.TrySetResult(true); }
                });
            });
            await Within(Task.WhenAny(waiting.Task, run));
            Assert(waiting.Task.IsCompleted, "takeover did not wait for busy lifecycle");
            settings.TakeOverFromNativeIntelliSense = false;
            ClangdSettings.NotifyTakeoverSettingChanged(false);
            await Within(run);
            Assert(cancelled.Task.IsCompleted && calls == 1,
                   "takeover off retained the busy wait or queued a restart");
        }
        finally { settings.TakeOverFromNativeIntelliSense = previous; }
    }

    private static async Task TakeoverStartsInactiveClientAsync()
    {
        var settings = ClangdSettings.Instance;
        var previous = settings.TakeOverFromNativeIntelliSense;
        settings.TakeOverFromNativeIntelliSense = true;
        try
        {
            using (var host = new TakeoverHost())
            {
                var starts = 0;
                host.Client.StartAsync += (_, __) =>
                {
                    starts++;
                    host.PublishActive();
                    typeof(ClangdLanguageClient).GetMethod("RecordServerInitialized",
                        BindingFlags.Instance | BindingFlags.NonPublic).Invoke(host.Client, null);
                    return Task.CompletedTask;
                };
                // Feature restart alone is a no-op here; takeover must start it.
                await Within(TakeoverRestart(host.Client));
                Assert(starts == 1 && host.Client.IsRunning, "takeover skipped an inactive client");
            }
        }
        finally { settings.TakeOverFromNativeIntelliSense = previous; }
    }

    private static async Task SupersededTakeoverDoesNotRestartAsync()
    {
        var settings = ClangdSettings.Instance;
        var previous = settings.TakeOverFromNativeIntelliSense;
        settings.TakeOverFromNativeIntelliSense = true;
        try
        {
            var waiting = Signal();
            var release = Signal();
            var sequence = 1L;
            var calls = 0;
            var restarts = 0;
            var run = SequencedTakeover(() =>
            {
                calls++;
                if (TakeoverApplyCoordinator.ShouldSkip(1, sequence))
                    return Task.CompletedTask;
                if (calls == 1)
                    throw new FeatureSettingsApplyDeferredException(async token =>
                    {
                        waiting.TrySetResult(true);
                        await release.Task;
                        token.ThrowIfCancellationRequested();
                    });
                restarts++;
                return Task.CompletedTask;
            });
            await Within(Task.WhenAny(waiting.Task, run));
            Assert(waiting.Task.IsCompleted, "superseded takeover never reached readiness wait");
            sequence++;
            release.TrySetResult(true);
            await Within(run);
            Assert(calls == 2 && restarts == 0, "readiness resumed an expired takeover snapshot");
        }
        finally { settings.TakeOverFromNativeIntelliSense = previous; }
    }

    // Fallbacks exercise the old behavior before the helpers exist, so RED is a
    // lifecycle assertion failure rather than a missing-method compiler error.
    private static Task SequencedTakeover(Func<Task> apply)
    {
        var method = typeof(TakeoverRuntimeController).GetMethod("ApplySequencedAsync",
            BindingFlags.Static | BindingFlags.NonPublic);
        return method == null ? TakeoverTransitionSerializer.ExecuteAsync(apply) :
            (Task)method.Invoke(null, new object[] { apply });
    }

    private static Task TakeoverRestart(ClangdLanguageClient client)
    {
        var method = typeof(TakeoverRuntimeController).GetMethod("RestartAsync",
            BindingFlags.Static | BindingFlags.NonPublic);
        return method == null ? client.RestartAsync() : (Task)method.Invoke(null, new object[] { client });
    }

    private sealed class TakeoverHost : IDisposable
    {
        internal readonly ClangdLanguageClient Client = new ClangdLanguageClient();
        private readonly List<Process> _processes = new List<Process>();
        private readonly JsonRpc _rpc = new JsonRpc(new MemoryStream(), new MemoryStream());

        internal void PublishActive()
        {
            var process = Process.Start(new ProcessStartInfo {
                FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                Arguments = "/d /c set /p takeoverRegression=",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
            });
            _processes.Add(process);
            Set("_serverProcess", process);
            Set("_rpc", _rpc);
        }

        private void Set(string name, object value) => typeof(ClangdLanguageClient)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(Client, value);

        public void Dispose()
        {
            Set("_serverProcess", null);
            Set("_rpc", null);
            foreach (var process in _processes)
            {
                try { if (!process.HasExited) process.Kill(); }
                catch (InvalidOperationException) { }
                process.Dispose();
            }
            _rpc.Dispose();
        }
    }

    private static async Task DeferredRestartWaitsAndKeepsLatestAsync()
    {
        using (var coordinator = new FeatureSettingsApplyCoordinator())
        using (var serializer = new SemaphoreSlim(1, 1))
        {
            var waiting = Signal();
            var released = Signal();
            var calls = new List<FeatureSettingsApply>();
            var errors = new List<Exception>();
            coordinator.Request(false, true, true, false, false);
            var run = coordinator.RunAsync(async work =>
            {
                await serializer.WaitAsync();
                try { await work(); }
                finally { serializer.Release(); }
            }, (request, token) =>
            {
                calls.Add(request);
                if (calls.Count == 1)
                    throw new FeatureSettingsApplyDeferredException(async cancellation =>
                    {
                        waiting.TrySetResult(true);
                        await released.Task;
                        cancellation.ThrowIfCancellationRequested();
                    });
                return Task.CompletedTask;
            }, errors.Add);
            await Within(Task.WhenAny(waiting.Task, run));
            Assert(waiting.Task.IsCompleted && !run.IsCompleted,
                   "busy restart was discarded instead of awaiting lifecycle progress");
            Assert(await serializer.WaitAsync(0), "lifecycle wait blocked a later takeover transition");
            serializer.Release();
            coordinator.Request(true, false, false, false, false);
            Assert(calls.Count == 1, "busy restart was retried before lifecycle progress");
            released.SetResult(true);
            await Within(run);
            Assert(calls.Count == 2 && calls[1].Version == 2 && calls[1].DiagnosticsEnabled &&
                   !calls[1].ClearDiagnostics && calls[1].RestartRequired && errors.Count == 0,
                   "deferred restart lost its reason or overwrote the newer settings");
        }
    }

    private static async Task DeferredRestartWithoutNewRequestAsync()
    {
        using (var coordinator = new FeatureSettingsApplyCoordinator())
        {
            var calls = 0;
            coordinator.Request(false, true, false, false, false);
            await Within(coordinator.RunAsync(Direct, (request, token) =>
            {
                if (++calls == 1)
                    throw new FeatureSettingsApplyDeferredException(_ => Task.CompletedTask);
                Assert(request.Version == 1 && request.ClearDiagnostics && request.RestartRequired,
                       "deferred version was not preserved");
                return Task.CompletedTask;
            }, Unexpected));
            Assert(calls == 2, "deferred apply required another user edit to run");
        }
    }

    private static async Task PermanentFailureIsNotRetriedAsync()
    {
        using (var coordinator = new FeatureSettingsApplyCoordinator())
        {
            var calls = 0;
            var errors = new List<Exception>();
            coordinator.Request(true, false, true, false, false);
            await Within(coordinator.RunAsync(Direct, (request, token) =>
            {
                calls++;
                throw new InvalidOperationException("missing Start host / initialization failure");
            }, errors.Add));
            Assert(calls == 1 && errors.Count == 1, "permanent error was blindly retried");
        }
    }

    private static async Task DisposeCancelsDeferredWaitAsync()
    {
        var coordinator = new FeatureSettingsApplyCoordinator();
        var waiting = Signal();
        var calls = 0;
        coordinator.Request(true, false, true, false, false);
        var run = coordinator.RunAsync(Direct, (request, token) =>
        {
            calls++;
            throw new FeatureSettingsApplyDeferredException(async cancellation =>
            {
                waiting.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, cancellation);
            });
        }, Unexpected);
        await Within(Task.WhenAny(waiting.Task, run));
        Assert(waiting.Task.IsCompleted, "deferred wait was not started");
        coordinator.Dispose();
        await Within(run);
        Assert(calls == 1, "dispose retried a deferred restart");
    }

    private static async Task FailedWaitIsNotRetriedAsync()
    {
        using (var coordinator = new FeatureSettingsApplyCoordinator())
        {
            var calls = 0;
            var waits = 0;
            var errors = new List<Exception>();
            var timeout = new TimeoutException("lifecycle did not complete");
            coordinator.Request(true, false, true, false, false);
            await Within(coordinator.RunAsync(Direct, (request, token) =>
            {
                calls++;
                throw new FeatureSettingsApplyDeferredException(_ =>
                {
                    waits++;
                    throw timeout;
                });
            }, errors.Add));
            Assert(calls == 1 && waits == 1 && errors.Count == 1 && errors[0] == timeout,
                   "failed lifecycle wait was swallowed or blindly retried");
        }
    }

    private static async Task TakeoverDuringDeferredWaitConsumesRestartAsync()
    {
        using (var coordinator = new FeatureSettingsApplyCoordinator())
        {
            var waiting = Signal();
            var released = Signal();
            var calls = 0;
            coordinator.Request(false, true, true, false, false);
            var run = coordinator.RunAsync(Direct, (request, token) =>
            {
                if (++calls == 1)
                    throw new FeatureSettingsApplyDeferredException(async cancellation =>
                    {
                        waiting.TrySetResult(true);
                        await released.Task;
                    });
                Assert(request.Version == 2 && !request.RestartRequired && request.ClearDiagnostics,
                       "deferred restart was replayed after takeover consumed its reasons");
                return Task.CompletedTask;
            }, Unexpected);
            await Within(Task.WhenAny(waiting.Task, run));
            Assert(waiting.Task.IsCompleted, "deferred wait was not started");
            coordinator.Request(false, false, false, false, true);
            released.SetResult(true);
            await Within(run);
            Assert(calls == 2, "takeover lost deferred cleanup");
        }
    }

    private static async Task MergesLatestWhileWaitingForTakeoverAsync()
    {
        using (var coordinator = new FeatureSettingsApplyCoordinator())
        using (var gate = new SemaphoreSlim(1, 1))
        {
            // Model an in-flight takeover holding the same serializer as features.
            await gate.WaitAsync();
            var waiting = Signal();
            var calls = new List<FeatureSettingsApply>();
            Func<Func<Task>, Task> serialize = async work =>
            {
                waiting.TrySetResult(true);
                await gate.WaitAsync();
                try { await work(); }
                finally { gate.Release(); }
            };
            coordinator.Request(false, true, false, false, false);
            var run = coordinator.RunAsync(serialize, (apply, token) =>
            {
                calls.Add(apply);
                return Task.CompletedTask;
            }, Unexpected);
            await Within(waiting.Task);
            coordinator.Request(true, true, false, false, false);
            coordinator.Request(true, false, true, false, false);
            Assert(calls.Count == 0, "feature apply overlapped takeover");
            gate.Release();
            await Within(run);
            Assert(calls.Count == 1, "queued changes were not merged at the serializer boundary");
            Assert(calls[0].Version == 3 && calls[0].DiagnosticsEnabled &&
                   !calls[0].ClearDiagnostics && calls[0].RestartRequired,
                   "merge lost the latest state or an earlier restart reason");
        }
    }

    private static async Task ReplaysChangesDuringRestartAsync()
    {
        using (var coordinator = new FeatureSettingsApplyCoordinator())
        {
            var entered = Signal();
            var release = Signal();
            var calls = new List<FeatureSettingsApply>();
            var active = 0;
            Func<FeatureSettingsApply, CancellationToken, Task> apply = async (request, token) =>
            {
                Assert(Interlocked.Increment(ref active) == 1, "concurrent feature applies");
                calls.Add(request);
                if (calls.Count == 1)
                {
                    entered.SetResult(true);
                    await release.Task;
                }
                Interlocked.Decrement(ref active);
            };
            coordinator.Request(true, false, true, false, false);
            var run = coordinator.RunAsync(Direct, apply, Unexpected);
            await Within(entered.Task);
            coordinator.Request(false, true, false, false, false);
            // An unrelated event must not erase the pending diagnostics change.
            coordinator.Request(false, false, false, false, false);
            await Within(coordinator.RunAsync(Direct, apply, Unexpected));
            Assert(calls.Count == 1, "another runner started while restart was in flight");
            release.SetResult(true);
            await Within(run);
            Assert(calls.Count == 2 && calls[1].Version == 3 &&
                   calls[1].ClearDiagnostics && calls[1].RestartRequired,
                   "changes during restart were not applied after it finished");
            coordinator.Request(true, false, false, true, false);
            await Within(coordinator.RunAsync(Direct, apply, Unexpected));
            Assert(calls.Count == 3 && calls[2].Version == 4 && calls[2].RestartRequired,
                   "worker did not restart after reaching idle");
        }
    }

    private static async Task PendingSurvivesFailureAsync()
    {
        using (var coordinator = new FeatureSettingsApplyCoordinator())
        {
            var entered = Signal();
            var release = Signal();
            var calls = 0;
            var errors = new List<Exception>();
            var failure = new InvalidOperationException("restart failed");
            coordinator.Request(true, false, true, false, false);
            var run = coordinator.RunAsync(Direct, async (request, token) =>
            {
                if (++calls == 1)
                {
                    entered.SetResult(true);
                    await release.Task;
                    throw failure;
                }
                Assert(request.ClearDiagnostics, "latest diagnostics state lost after failure");
            }, errors.Add);
            await Within(entered.Task);
            coordinator.Request(false, true, false, false, false);
            release.SetResult(true);
            await Within(run);
            Assert(calls == 2 && errors.Count == 1 && errors[0] == failure,
                   "failed apply swallowed either its error or pending work");
        }
    }

    private static async Task DisposeCancelsPendingAsync()
    {
        var coordinator = new FeatureSettingsApplyCoordinator();
        var entered = Signal();
        var release = Signal();
        var calls = 0;
        coordinator.Request(false, true, false, false, false);
        var run = coordinator.RunAsync(async work =>
        {
            entered.SetResult(true);
            await release.Task;
            await work();
        }, (request, token) =>
        {
            calls++;
            return Task.CompletedTask;
        }, Unexpected);
        await Within(entered.Task);
        coordinator.Dispose();
        coordinator.Dispose();
        coordinator.Request(true, false, false, true, false);
        release.SetResult(true);
        await Within(run);
        await coordinator.RunAsync(Direct, (request, token) =>
        {
            calls++;
            return Task.CompletedTask;
        }, Unexpected);
        Assert(calls == 0, "disposed coordinator applied pending or newly received work");
    }

    private static async Task DisposeCancelsActiveApplyAsync()
    {
        var coordinator = new FeatureSettingsApplyCoordinator();
        var entered = Signal();
        var calls = 0;
        coordinator.Request(true, false, true, false, false);
        var run = coordinator.RunAsync(Direct, async (request, token) =>
        {
            calls++;
            entered.SetResult(true);
            await Task.Delay(Timeout.Infinite, token);
        }, Unexpected);
        await Within(entered.Task);
        coordinator.Request(false, true, false, false, false);
        coordinator.Dispose();
        await Within(run);
        Assert(calls == 1, "dispose replayed work after cancelling active apply");
    }

    private static async Task RestartReasonsAsync()
    {
        for (var reason = 0; reason < 4; reason++)
        {
            foreach (var takeoverChanged in new[] { false, true })
            {
                using (var coordinator = new FeatureSettingsApplyCoordinator())
                {
                    FeatureSettingsApply observed = null;
                    coordinator.Request(false, reason == 1, reason == 2, reason == 3, takeoverChanged);
                    await coordinator.RunAsync(Direct, (request, token) =>
                    {
                        observed = request;
                        return Task.CompletedTask;
                    }, Unexpected);
                    Assert(observed != null && observed.RestartRequired == (reason != 0 && !takeoverChanged),
                           "wrong restart decision for diagnostics/inactive regions/semantic tokens/takeover");
                    Assert(observed.ClearDiagnostics == (reason == 1),
                           "takeover de-duplication suppressed diagnostic cleanup");
                }
            }
        }
    }

    private static async Task TakeoverConsumesPendingRestartReasonsAsync()
    {
        using (var coordinator = new FeatureSettingsApplyCoordinator())
        {
            FeatureSettingsApply observed = null;
            Func<FeatureSettingsApply, CancellationToken, Task> apply = (request, token) =>
            {
                observed = request;
                return Task.CompletedTask;
            };
            coordinator.Request(false, true, true, true, false);
            coordinator.Request(false, false, false, false, true);
            await coordinator.RunAsync(Direct, apply, Unexpected);
            Assert(observed.Version == 2 && !observed.RestartRequired && observed.ClearDiagnostics,
                   "same-Apply takeover must consume queued restart reasons, but preserve cleanup");

            coordinator.Request(false, true, false, false, true);
            coordinator.Request(false, false, false, true, false);
            await coordinator.RunAsync(Direct, apply, Unexpected);
            Assert(observed.Version == 4 && observed.RestartRequired,
                   "a later feature change was incorrectly covered by the earlier takeover");
        }
    }

    private static async Task DisposeDoesNotReleaseInFlightRestartAsync()
    {
        var coordinator = new FeatureSettingsApplyCoordinator();
        var entered = Signal();
        var finishRestart = Signal();
        var serializerReleased = false;
        var calls = 0;
        coordinator.Request(true, false, true, false, false);
        var run = coordinator.RunAsync(async work =>
        {
            try { await work(); }
            finally { serializerReleased = true; }
        }, async (request, token) =>
        {
            calls++;
            entered.SetResult(true);
            // Like ClangdLanguageClient.RestartAsync, this operation cannot cancel.
            await finishRestart.Task;
        }, Unexpected);
        await Within(entered.Task);
        coordinator.Request(false, true, false, false, false);
        coordinator.Dispose();
        Assert(!run.IsCompleted && !serializerReleased,
               "dispose released the lifecycle serializer before restart finished");
        finishRestart.SetResult(true);
        await Within(run);
        Assert(serializerReleased && calls == 1, "disposed apply replayed pending restart");
    }

    private static Task Direct(Func<Task> work) => work();
    private static TaskCompletionSource<bool> Signal() =>
        new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Unexpected(Exception error) => throw new InvalidOperationException("unexpected apply error", error);
    private static async Task Within(Task task)
    {
        Assert(await Task.WhenAny(task, Task.Delay(5000)) == task, "feature apply timed out");
        await task;
    }
    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
