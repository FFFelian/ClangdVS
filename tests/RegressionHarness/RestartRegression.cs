using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;
using ClangdVS;
using Microsoft.VisualStudio.LanguageServer.Client;
using Moq;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;

public static class RestartRegression
{
    public static async Task RunAsync()
    {
        // SDK adapter for the baseline public callback: avoid ThreadHelper's
        // attempt to acquire VS services in a process with no VS host.
        var contextField =
            typeof(ThreadHelper).GetField("_joinableTaskContextCache", BindingFlags.Static | BindingFlags.NonPublic);
        Assert(contextField != null, "SDK ThreadHelper context adapter is unavailable");
        var previousContext = contextField.GetValue(null);
        var settings = ClangdSettings.Instance;
        var previousRecovery = settings.RestartAfterCrash;
        var previousTakeover = settings.TakeOverFromNativeIntelliSense;
        using (var context = new JoinableTaskContext())
        {
            contextField.SetValue(null, context);
            settings.RestartAfterCrash = true;
            settings.TakeOverFromNativeIntelliSense = true;
            try
            {
                await RunTestsAsync();
            }
            finally
            {
                settings.RestartAfterCrash = previousRecovery;
                settings.TakeOverFromNativeIntelliSense = previousTakeover;
                contextField.SetValue(null, previousContext);
            }
        }
    }

    private static async Task RunTestsAsync()
    {
        var failures = new List<string>();
        await Check("no Start subscriber fails",
                    async () =>
                    {
                        var client = new ClangdLanguageClient();
                        var stops = 0;
                        client.StopAsync += (_, __) =>
                        {
                            stops++;
                            return Task.CompletedTask;
                        };
                        await ExpectFailure<InvalidOperationException>(client.RestartAsync());
                        Assert(stops == 0, "missing host unnecessarily stopped the current server");
                    });
        await Check("initialization failure is propagated even when broker swallows it",
                    async () =>
                    {
                        var client = new ClangdLanguageClient();
                        var cause = new InvalidOperationException("simulated initialize failure");
                        var info = new Mock<ILanguageClientInitializationInfo>();
                        info.SetupGet(x => x.InitializationException).Returns(cause);
                        client.StartAsync += async (_, __) =>
                        { await client.OnServerInitializeFailedAsync(info.Object); };
                        var restart = client.RestartAsync();
                        var error = await ExpectFailure<InvalidOperationException>(restart);
                        Assert(error.Message.Contains(cause.Message), "initialize failure details lost");
                    });
        await Check("failure callback completes restart even if Start handler is still waiting",
                    async () =>
                    {
                        var client = new ClangdLanguageClient();
                        var release = Signal();
                        client.StartAsync += async (_, __) =>
                        {
                            await client.OnServerInitializeFailedAsync(null);
                            await release.Task;
                        };
                        try
                        {
                            await ExpectFailure<InvalidOperationException>(client.RestartAsync());
                        }
                        finally
                        {
                            release.SetResult(true);
                        }
                    });
        await Check("unattributed old callback without fresh activation cannot complete restart",
                    async () =>
                    {
                        var client = new ClangdLanguageClient();
                        var initialize = Signal();
                        Task callback = null;
                        client.StartAsync += (_, __) =>
                        {
                            callback = CompleteInitializationAsync(client, initialize.Task);
                            return Task.CompletedTask;
                        };
                        var restart = client.RestartAsync();
                        Initialized(client); // Outside the replacement Start's execution context.
                        try
                        {
                            Assert(!restart.IsCompleted && InitCount(client) == 0,
                                   "unattributed stale initialization was accepted");
                        }
                        finally
                        {
                            initialize.SetResult(true);
                        }
                        await callback;
                        await Within(restart);
                    });
        await Check("Start completion waits for this initialization",
                    async () =>
                    {
                        var client = new ClangdLanguageClient();
                        var started = Signal();
                        var initialize = Signal();
                        Task callback = null;
                        client.StartAsync += (_, __) =>
                        {
                            callback = CompleteInitializationAsync(client, initialize.Task);
                            started.SetResult(true);
                            return Task.CompletedTask;
                        };
                        var restart = client.RestartAsync();
                        await started.Task;
                        try
                        {
                            Assert(!restart.IsCompleted, "Restart returned before initialize/initialized callback");
                        }
                        finally
                        {
                            initialize.SetResult(true);
                        }
                        await callback;
                        await Within(restart);
                        Assert(InitCount(client) == 1, "successful restart did not count a real initialization");
                    });
        await Check("SDK callback on an independent context follows fresh activation",
                    async () =>
                    {
                        var client = new ClangdLanguageClient();
                        client.StartAsync += (_, __) => Task.CompletedTask;
                        var restart = client.RestartAsync();
                        Assert(!restart.IsCompleted, "Start alone completed the restart");
                        // Headless adapter for ActivateAsync's publication, avoiding clangd
                        // path resolution, DTE and a real process. Callback runs outside
                        // Start's execution context, as permitted by the SDK sequence.
                        typeof(ClangdLanguageClient)
                            .GetField("_initializationPending", BindingFlags.Instance | BindingFlags.NonPublic)
                            ?.SetValue(client, true);
                        Initialized(client);
                        await Within(restart);
                        Assert(InitCount(client) == 1, "independently dispatched SDK callback was ignored");
                    });
        await Check("callbacks during Stop cannot initialize the replacement",
                    async () =>
                    {
                        var client = new ClangdLanguageClient();
                        var stopped = Signal();
                        var releaseStop = Signal();
                        var started = Signal();
                        var initialize = Signal();
                        Task callback = null;
                        client.StopAsync += async (_, __) =>
                        {
                            Initialized(client);
                            stopped.SetResult(true);
                            await releaseStop.Task;
                        };
                        client.StartAsync += (_, __) =>
                        {
                            callback = CompleteInitializationAsync(client, initialize.Task);
                            started.SetResult(true);
                            return Task.CompletedTask;
                        };
                        var restart = client.RestartAsync();
                        await stopped.Task;
                        Assert(!restart.IsCompleted && InitCount(client) == 0,
                               "Stop callback counted as new initialization");
                        releaseStop.SetResult(true);
                        await started.Task;
                        try
                        {
                            Assert(!restart.IsCompleted, "old initialization satisfied the restart");
                        }
                        finally
                        {
                            initialize.SetResult(true);
                        }
                        await callback;
                        await Within(restart);
                    });
        await Check("overlapping restarts fail without a second Stop/Start",
                    async () =>
                    {
                        var client = new ClangdLanguageClient();
                        var stopped = Signal();
                        var release = Signal();
                        var stops = 0;
                        var starts = 0;
                        client.StopAsync += async (_, __) =>
                        {
                            stops++;
                            stopped.TrySetResult(true);
                            await release.Task;
                        };
                        client.StartAsync += (_, __) =>
                        {
                            starts++;
                            Initialized(client);
                            return Task.CompletedTask;
                        };
                        var first = client.RestartAsync();
                        await stopped.Task;
                        var second = client.RestartAsync();
                        try
                        {
                            await ExpectFailure<InvalidOperationException>(second);
                        }
                        finally
                        {
                            release.TrySetResult(true);
                        }
                        await Within(first);
                        Assert(stops == 1 && starts == 1, "overlap cycled the broker twice");
                    });
        await Check(
            "missing initialize callback times out and retry rejects a late old callback",
            async () =>
            {
                var client = new ClangdLanguageClient();
                using var timedOutProcess = new Process();
                var initializeOld = Signal();
                Task oldCallback = null;
                var calls = 0;
                var newStarted = Signal();
                var initializeNew = Signal();
                Task newCallback = null;
                client.StartAsync += (_, __) =>
                {
                    if (++calls == 1)
                    {
                        PublishProcess(client, timedOutProcess);
                        oldCallback = CompleteInitializationAsync(client, initializeOld.Task);
                    }
                    else
                    {
                        newCallback = CompleteInitializationAsync(client, initializeNew.Task);
                        newStarted.SetResult(true);
                    }
                    return Task.CompletedTask;
                };
                var hungClient = new ClangdLanguageClient();
                var releaseHungStart = Signal();
                hungClient.StartAsync += (_, __) => releaseHungStart.Task;
                var hungRestart = hungClient.RestartAsync();
                var elapsed = Stopwatch.StartNew();
                try
                {
                    await ExpectFailure<TimeoutException>(client.RestartAsync(), 35000);
                    await ExpectFailure<TimeoutException>(hungRestart, 5000);
                    Assert(elapsed.ElapsedMilliseconds >= 29000 && elapsed.ElapsedMilliseconds < 35000,
                           "restart did not honor its bounded 30-second initialization wait");
                    Console.WriteLine("Initialization timeout (including hung Start): " + elapsed.ElapsedMilliseconds +
                                      " ms");
                }
                finally
                {
                    releaseHungStart.SetResult(true);
                }
                Exited(client, timedOutProcess);
                Assert((bool)Field(client, "_expectingExit"), "initialization timeout left cleanup exit unexpected");
                Assert((int)Field(client, "_crashCount") == 0, "timeout cleanup scheduled crash recovery");
                SetField(client, "_serverProcess", null);
                var retry = client.RestartAsync();
                await newStarted.Task;
                initializeOld.SetResult(true);
                await oldCallback;
                try
                {
                    Assert(!retry.IsCompleted && InitCount(client) == 0, "late old callback completed the retry");
                }
                finally
                {
                    initializeNew.SetResult(true);
                }
                await newCallback;
                await Within(retry);
                Assert(InitCount(client) == 1, "late callback inflated the real initialization count");
            });
        await Check("Start exception remains a failed restart and next attempt works",
                    async () =>
                    {
                        var client = new ClangdLanguageClient();
                        var calls = 0;
                        client.StartAsync += (_, __) =>
                        {
                            if (++calls == 1)
                                throw new InvalidOperationException("broker start failed");
                            Initialized(client);
                            return Task.CompletedTask;
                        };
                        await ExpectFailure<InvalidOperationException>(client.RestartAsync());
                        await Within(client.RestartAsync());
                    });
        await Check("late first-load callback cannot initialize a manual restart",
                    async () =>
                    {
                        var client = new ClangdLanguageClient();
                        var initializeOld = Signal();
                        var initializeNew = Signal();
                        Task oldCallback = null;
                        Task newCallback = null;
                        var calls = 0;
                        client.StartAsync += (_, __) =>
                        {
                            if (++calls == 1)
                                oldCallback = CompleteInitializationAsync(client, initializeOld.Task);
                            else
                                newCallback = CompleteInitializationAsync(client, initializeNew.Task);
                            return Task.CompletedTask;
                        };
                        await client.OnLoadedAsync();
                        await client.OnLoadedAsync();
                        Assert(calls == 1, "first load lost its idempotence");
                        var restart = client.RestartAsync();
                        initializeOld.SetResult(true);
                        await oldCallback;
                        await client.OnServerInitializeFailedAsync(
                            null); // An untagged stale failure, without fresh activation.
                        try
                        {
                            Assert(!restart.IsCompleted && InitCount(client) == 0,
                                   "first-load callback completed manual restart");
                        }
                        finally
                        {
                            initializeNew.SetResult(true);
                        }
                        await newCallback;
                        await Within(restart);
                    });
        await Check("obsolete cold-start work cannot cycle a manually restarted client",
                    async () =>
                    {
                        var client = new ClangdLanguageClient();
                        var starts = 0;
                        client.StartAsync += (_, __) =>
                        {
                            starts++;
                            Initialized(client);
                            return Task.CompletedTask;
                        };
                        await Within(client.RestartAsync());
                        var method = typeof(ClangdLanguageClient)
                                         .GetMethod("RestartAsync", BindingFlags.Instance | BindingFlags.NonPublic,
                                                    null, new[] { typeof(int?) }, null);
                        Assert(method != null, "cold-start lifecycle version check is missing");
                        await Within((Task)method.Invoke(client, new object[] { (int?)0 }));
                        Assert(starts == 1 && InitCount(client) == 1,
                               "obsolete cold-start work performed another restart");
                    });
        await Check("failed Stop never starts a replacement and a later restart can recover",
                    async () =>
                    {
                        var client = new ClangdLanguageClient();
                        var stops = 0;
                        var starts = 0;
                        client.StopAsync += (_, __) =>
                            ++stops == 1 ? Task.FromException(new InvalidOperationException("broker Stop failed"))
                                         : Task.CompletedTask;
                        client.StartAsync += (_, __) =>
                        {
                            starts++;
                            Initialized(client);
                            return Task.CompletedTask;
                        };
                        await ExpectFailure<InvalidOperationException>(client.RestartAsync());
                        Assert(starts == 0, "replacement started despite failed Stop");
                        await Within(client.RestartAsync());
                        Assert(starts == 1, "failed Stop prevented recovery");
                    });
        await Check("Shutdown waits for restart and ignores initialization while stopping",
                    async () =>
                    {
                        var client = new ClangdLanguageClient();
                        var initialize = Signal();
                        Task callback = null;
                        var stops = 0;
                        client.StopAsync += (_, __) =>
                        {
                            stops++;
                            Initialized(client);
                            return Task.CompletedTask;
                        };
                        client.StartAsync += (_, __) =>
                        {
                            callback = CompleteInitializationAsync(client, initialize.Task);
                            return Task.CompletedTask;
                        };
                        var restart = client.RestartAsync();
                        var shutdown = client.ShutdownAsync();
                        try
                        {
                            Assert(stops == 1 && !shutdown.IsCompleted, "Shutdown interrupted an in-flight restart");
                        }
                        finally
                        {
                            initialize.SetResult(true);
                        }
                        await callback;
                        await Within(restart);
                        await Within(shutdown);
                        Initialized(client);
                        Assert(stops == 2 && InitCount(client) == 1,
                               "Stop/Shutdown callback counted as initialization");
                    });
        await Check("public SDK initialized callback completes the actual restart",
                    async () =>
                    {
                        var client = new ClangdLanguageClient();
                        var starts = 0;
                        client.StartAsync += async (_, __) =>
                        {
                            if (++starts == 1)
                                Initialized(client);
                            else
                                await client.OnServerInitializedAsync();
                        };
                        await Within(client.RestartAsync());
                        await Within(client.RestartAsync());
                        Assert(InitCount(client) == 2,
                               "public SDK callback failed to record the replacement initialization");
                    });
        await Check("initial-load failure followed by host exit does not auto-restart",
                    async () =>
                    {
                        var client = new ClangdLanguageClient();
                        using (var process = new Process())
                        {
                            var starts = 0;
                            client.StartAsync += async (_, __) =>
                            {
                                starts++;
                                PublishProcess(client, process);
                                await client.OnServerInitializeFailedAsync(null);
                            };
                            await client.OnLoadedAsync();
                            Exited(client, process);
                            Assert((bool)Field(client, "_expectingExit"),
                                   "initial failure did not mark host Stop exit expected");
                            Assert(Field(client, "_currentStartContext") == null,
                                   "failed initial Start context was retained");
                            Assert((int)Field(client, "_crashCount") == 0 && starts == 1,
                                   "host cleanup of initial failure entered crash recovery");
                            Assert(Field(client, "_serverProcess") == null, "expected exit retained the dead process");
                        }
                    });
        await Check(
            "restart failure after waiter cleanup does not auto-restart on host exit",
            async () =>
            {
                var client = new ClangdLanguageClient();
                using (var process = new Process())
                {
                    client.StartAsync += async (_, __) =>
                    {
                        PublishProcess(client, process);
                        await client.OnServerInitializeFailedAsync(null);
                    };
                    await ExpectFailure<InvalidOperationException>(client.RestartAsync());
                    Assert(Field(client, "_restartAttempt") == null, "test must exit after restart waiter cleanup");
                    Exited(client, process);
                    Assert((bool)Field(client, "_expectingExit"), "restart failure did not mark host cleanup expected");
                    Assert((int)Field(client, "_crashCount") == 0, "failed restart exit entered crash recovery");
                    Assert(Field(client, "_serverProcess") == null, "expected exit retained the dead process");
                }
            });
        await Check("late failure and old process exit cannot mark or clear the new session",
                    async () =>
                    {
                        var client = new ClangdLanguageClient();
                        using (var oldProcess = new Process()) using (var newProcess = new Process())
                        {
                            var releaseOldFailure = Signal();
                            var initializeNew = Signal();
                            Task oldFailure = null;
                            Task newCallback = null;
                            var starts = 0;
                            client.StartAsync += (_, __) =>
                            {
                                if (++starts == 1)
                                    oldFailure = FailInitializationAsync(client, releaseOldFailure.Task);
                                else
                                {
                                    PublishProcess(client, newProcess);
                                    newCallback = CompleteInitializationAsync(client, initializeNew.Task);
                                }
                                return Task.CompletedTask;
                            };
                            await client.OnLoadedAsync();
                            var restart = client.RestartAsync();
                            releaseOldFailure.SetResult(true);
                            await oldFailure;
                            Exited(client, oldProcess);
                            try
                            {
                                Assert(!(bool)Field(client, "_expectingExit") && !restart.IsCompleted,
                                       "late old failure marked the replacement as failed/expected exit");
                                Assert(ReferenceEquals(Field(client, "_serverProcess"), newProcess),
                                       "old exit cleared new process");
                            }
                            finally
                            {
                                initializeNew.SetResult(true);
                            }
                            await newCallback;
                            await Within(restart);
                            SetField(client, "_serverProcess", null);
                        }
                    });
        await Check("real crash recovery owns lifecycle and waits for initialization",
                    async () =>
                    {
                        var client = new ClangdLanguageClient();
                        using (var process = new Process())
                        {
                            var stopped = Signal();
                            var releaseStop = Signal();
                            var started = Signal();
                            var initialize = Signal();
                            Task callback = null;
                            var stops = 0;
                            var starts = 0;
                            client.StopAsync += async (_, __) =>
                            {
                                stops++;
                                stopped.TrySetResult(true);
                                await releaseStop.Task;
                            };
                            client.StartAsync += (_, __) =>
                            {
                                starts++;
                                callback = CompleteInitializationAsync(client, initialize.Task);
                                started.TrySetResult(true);
                                return Task.CompletedTask;
                            };
                            PublishProcess(client, process);
                            Exited(client, process);
                            // Baseline directly fires Start without Stop; observe either event
                            // so red is a behavior failure rather than a hanging test.
                            await Within(Task.WhenAny(stopped.Task, started.Task));
                            try
                            {
                                Assert(stops == 1, "crash recovery bypassed RestartAsync Stop lifecycle");
                            }
                            finally
                            {
                                releaseStop.TrySetResult(true);
                                if (stops == 0)
                                    initialize.TrySetResult(true);
                            }
                            await Within(started.Task);
                            var recoveryAttempt = Field(client, "_restartAttempt");
                            var overlap = client.RestartAsync();
                            var shutdown = client.ShutdownAsync();
                            try
                            {
                                Assert(recoveryAttempt != null,
                                       "crash recovery did not publish an initialization waiter");
                                await ExpectFailure<InvalidOperationException>(overlap);
                                Assert(starts == 1 && stops == 1 && !shutdown.IsCompleted,
                                       "manual restart/shutdown interrupted recovery initialization");
                            }
                            finally
                            {
                                initialize.TrySetResult(true);
                            }
                            await callback;
                            await Within(shutdown);
                            Assert(starts == 1 && stops == 2, "serialized recovery performed duplicate Start/Stop");
                        }
                    });
        await Check("late recovery is invalidated by manual restart and shutdown",
                    async () =>
                    {
                        var client = new ClangdLanguageClient();
                        var starts = 0;
                        var stops = 0;
                        var releaseStop = Signal();
                        client.StopAsync += async (_, __) =>
                        {
                            stops++;
                            await releaseStop.Task;
                        };
                        client.StartAsync += (_, __) =>
                        {
                            starts++;
                            Initialized(client);
                            return Task.CompletedTask;
                        };
                        var beforeManual = (int)Field(client, "_lifecycleVersion");
                        var manual = client.RestartAsync();
                        await Within(RecoverAfterCrash(client, beforeManual));
                        Assert(stops == 1 && starts == 0, "late recovery overlapped manual Stop");
                        releaseStop.SetResult(true);
                        await Within(manual);
                        await Within(RecoverAfterCrash(client, beforeManual));
                        Assert(starts == 1, "late crash recovery restarted a manually replaced session");
                        var beforeShutdown = (int)Field(client, "_lifecycleVersion");
                        releaseStop = Signal();
                        var shutdown = client.ShutdownAsync();
                        await Within(RecoverAfterCrash(client, beforeShutdown));
                        Assert(stops == 2 && starts == 1, "late recovery overlapped shutdown Stop");
                        releaseStop.SetResult(true);
                        await Within(shutdown);
                        await Within(RecoverAfterCrash(client, beforeShutdown));
                        Assert(starts == 1, "late crash recovery restarted a shut down session");
                    });
        await Check("expected failed-session exit cannot suppress a replacement's real crash",
                    async () =>
                    {
                        var client = new ClangdLanguageClient();
                        using (var failedProcess = new Process()) using (var newProcess = new Process())
                        {
                            var starts = 0;
                            var recoveryStarted = Signal();
                            var initializeRecovery = Signal();
                            Task recoveryCallback = null;
                            client.StartAsync += async (_, __) =>
                            {
                                if (++starts == 1)
                                {
                                    PublishProcess(client, failedProcess);
                                    await client.OnServerInitializeFailedAsync(null);
                                }
                                else if (starts == 2)
                                {
                                    PublishProcess(client, newProcess);
                                    Initialized(client);
                                }
                                else
                                {
                                    recoveryCallback = CompleteInitializationAsync(client, initializeRecovery.Task);
                                    recoveryStarted.TrySetResult(true);
                                }
                            };
                            await ExpectFailure<InvalidOperationException>(client.RestartAsync());
                            Exited(client, failedProcess);
                            await Within(client.RestartAsync());
                            Exited(client, failedProcess); // Old process identity, after the replacement is running.
                            Assert(!(bool)Field(client, "_expectingExit") &&
                                       ReferenceEquals(Field(client, "_serverProcess"), newProcess),
                                   "old expected exit affected the replacement process");
                            Exited(client, newProcess);
                            await Within(recoveryStarted.Task);
                            Assert((int)Field(client, "_crashCount") == 1 && starts == 3,
                                   "replacement real crash was incorrectly treated as expected exit");
                            initializeRecovery.SetResult(true);
                            await recoveryCallback;
                            await Within(client.ShutdownAsync());
                        }
                    });
        if (failures.Count != 0)
            throw new Exception(string.Join(Environment.NewLine, failures));

        async Task Check(string name, Func<Task> test)
        {
            try
            {
                await test();
                Console.WriteLine("PASS: " + name);
            }
            catch (Exception ex)
            {
                var failure = name + " — " + ex.GetBaseException().Message;
                failures.Add(failure);
                Console.WriteLine("FAIL: " + failure);
            }
        }
    }

    private static async Task CompleteInitializationAsync(ClangdLanguageClient client, Task ready)
    {
        await ready.ConfigureAwait(false);
        Initialized(client);
    }

    private static async Task FailInitializationAsync(ClangdLanguageClient client, Task ready)
    {
        await ready.ConfigureAwait(false);
        await client.OnServerInitializeFailedAsync(null);
    }

    private static object Field(ClangdLanguageClient client, string name) =>
        typeof(ClangdLanguageClient).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(client);
    private static void SetField(ClangdLanguageClient client, string name,
                                 object value) => typeof(ClangdLanguageClient)
                                                      .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                                                      .SetValue(client, value);
    private static void PublishProcess(ClangdLanguageClient client, Process process)
    {
        // Identity-only adapter for StartServerAsync's publication; the Process
        // has no OS process/handle, and tests never pass it to KillServerProcess.
        SetField(client, "_serverProcess", process);
        SetField(client, "_expectingExit", false);
    }
    private static void Exited(ClangdLanguageClient client,
                               Process process) => typeof(ClangdLanguageClient)
                                                       .GetMethod("OnServerExited",
                                                                  BindingFlags.Instance | BindingFlags.NonPublic)
                                                       .Invoke(client, new object[] { process, 1 });
    private static Task RecoverAfterCrash(ClangdLanguageClient client, int version)
    {
        var method = typeof(ClangdLanguageClient)
                         .GetMethod("RestartAfterCrashAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        if (method == null)
        {
            // Pre-fix recovery directly invoked this existing Start helper.
            // Run that real branch for red instead of failing on a missing API.
            var type = typeof(ClangdLanguageClient);
            var context = Activator.CreateInstance(type.GetNestedType("RestartAttempt", BindingFlags.NonPublic), true);
            return InvokeOldRecoveryAsync();

            async Task InvokeOldRecoveryAsync()
            {
                // Match the pre-fix Task.Run wrapper's exception handling.
                try
                {
                    await(Task) type.GetMethod("InvokeStartAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                        .Invoke(client, new[] { Field(client, "StartAsync"), context });
                }
                catch (Exception)
                {
                }
            }
        }
        // Invoke the scheduled work after a manual operation to deterministically
        // model Task.Run's delayed execution without thread-pool timing guesses.
        return (Task)method.Invoke(client, new object[] { version });
    }

    private static void Initialized(ClangdLanguageClient client)
    {
        // Exercise the callback's actual state transition without scheduling
        // the unrelated VS/DTE background document resend in this headless runner.
        var method = typeof(ClangdLanguageClient)
                         .GetMethod("RecordServerInitialized", BindingFlags.Instance | BindingFlags.NonPublic);
        if (method != null)
        {
            method.Invoke(client, null);
            return;
        }
        client.OnServerInitializedAsync().GetAwaiter().GetResult();
    }

    private static int InitCount(ClangdLanguageClient client) => (int)typeof(ClangdLanguageClient)
                                                                     .GetField("_initCount", BindingFlags.Instance |
                                                                                                 BindingFlags.NonPublic)
                                                                     .GetValue(client);

    private static TaskCompletionSource<bool> Signal() =>
        new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Within(Task task, int milliseconds = 5000)
    {
        if (await Task.WhenAny(task, Task.Delay(milliseconds)) != task)
            throw new Exception("test watchdog expired; expected restart outcome was not observed");
        await task;
    }
    private static async Task<T> ExpectFailure<T>(Task task, int milliseconds = 5000)
        where T : Exception
    {
        try
        {
            await Within(task, milliseconds);
        }
        catch (Exception ex) when (ex.GetBaseException() is T)
        {
            return (T)ex.GetBaseException();
        }
        throw new Exception("Restart returned successfully; expected " + typeof(T).Name);
    }
    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

#if RESTART_REGRESSION_STANDALONE
    public static int Main()
    {
        try
        {
            RunAsync().GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.GetBaseException().Message);
            return 1;
        }
    }
#endif
}
