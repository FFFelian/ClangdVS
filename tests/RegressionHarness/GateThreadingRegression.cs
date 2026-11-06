using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ClangdVS;
using Microsoft.VisualStudio.Threading;

public static class GateThreadingRegression
{
    public static Task RunAsync()
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var uiThread = new Thread(
            () =>
            {
                // This context deliberately does not pump ordinary posted work. Only
                // JTF's dependency-aware synchronous wait may run the owner's UI work.
                SynchronizationContext.SetSynchronizationContext(new NoPumpContext());
                using (var context = new JoinableTaskContext(Thread.CurrentThread, SynchronizationContext.Current))
                {
                    try
                    {
                        BackgroundOwnerCanBeJoinedFromUi(context.Factory);
                        RetryAndConcurrentSuccess(context.Factory);
                        FailedWaitersCannotResetSuccessfulRetry(context.Factory);
                        ReentrantLookupSharesPublishedAttempt(context.Factory);
                        completion.SetResult(true);
                    }
                    catch (Exception ex)
                    {
                        completion.SetException(ex);
                    }
                }
            });
        uiThread.IsBackground = true;
        uiThread.SetApartmentState(ApartmentState.STA);
        uiThread.Start();
        return CompleteWithinTimeoutAsync(completion.Task);
    }

    private static async Task CompleteWithinTimeoutAsync(Task completion)
    {
        // A guard for regressions in the other scenarios, not an ordering delay.
        if (await Task.WhenAny(completion, Task.Delay(15000)).ConfigureAwait(false) != completion)
            throw new TimeoutException("JTF gate regression thread did not finish");
        await completion.ConfigureAwait(false);
    }

    private static void BackgroundOwnerCanBeJoinedFromUi(JoinableTaskFactory factory)
    {
        var ensure = CreateGate(factory);
        using (var needsUi = new ManualResetEventSlim()) using (var watchdog = new CancellationTokenSource())
        {
            var calls = 0;
            var watchdogFired = 0;
            using (var timer = new Timer(
                       _ =>
                       {
                           Interlocked.Exchange(ref watchdogFired, 1);
                           watchdog.Cancel();
                       },
                       null, 5000, Timeout.Infinite))
            {
                Func<Task<bool>> load = async () =>
                {
                    Interlocked.Increment(ref calls);
                    needsUi.Set();
                    await factory.SwitchToMainThreadAsync(watchdog.Token);
                    Assert(factory.Context.IsOnMainThread, "load did not reach the injected UI thread");
                    return true;
                };
                var owner = Task.Run(() => ensure(load));
                Assert(needsUi.Wait(5000), "background owner did not start");
                Exception error = null;
                bool loaded = false;
                try
                {
                    loaded = factory.Run(() => ensure(load));
                }
                catch (Exception ex)
                {
                    error = ex;
                }
                // On red, cancellation releases the blocked owner and prevents
                // hanging the test process. It is never a success mechanism.
                try
                {
                    factory.Run(async () => await owner);
                }
                catch (OperationCanceledException)
                {
                }
                Console.WriteLine($"JTF contention: watchdog={watchdogFired}, loads={calls}, loaded={loaded}");
                Assert(watchdogFired == 0, "UI waiter could not join the background load (watchdog=1)");
                if (error != null)
                    throw error;
                Assert(loaded && calls == 1, "UI contention duplicated or lost the successful load");
            }
        }
    }

    private static void RetryAndConcurrentSuccess(JoinableTaskFactory factory)
    {
        var ensure = CreateGate(factory);
        factory.Run(
            async () =>
            {
                Assert(!await ensure(() => Task.FromResult(false)), "false load cached as success");
                var threw = false;
                try
                {
                    await ensure(() => throw new InvalidOperationException("expected load failure"));
                }
                catch (InvalidOperationException)
                {
                    threw = true;
                }
                Assert(threw, "synchronous failure was swallowed");
                var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var calls = 0;
                Func<Task<bool>> load = async () =>
                {
                    Interlocked.Increment(ref calls);
                    started.TrySetResult(true);
                    return await release.Task;
                };
                var first = ensure(load);
                var second = ensure(load);
                await started.Task;
                release.SetResult(true);
                Assert(await first && await second && calls == 1, "retry or concurrent success failed");
                Assert(await ensure(() => throw new Exception("successful load repeated")), "success was not cached");
            });
        Console.WriteLine("PASS: false/exception retry, concurrent single load, successful cache");
    }

    private static void ReentrantLookupSharesPublishedAttempt(JoinableTaskFactory factory)
    {
        var ensure = CreateGate(factory);
        Task<bool> nested = null;
        var calls = 0;
        factory.Run(async () =>
                    {
                        Assert(await ensure(() =>
                                            {
                                                Interlocked.Increment(ref calls);
                                                // Reenter during the synchronous prefix, without waiting on our
                                                // own completion. The attempt must already be published.
                                                nested = ensure(() => throw new Exception("reentrant duplicate load"));
                                                return Task.FromResult(true);
                                            }),
                               "outer load failed");
                        Assert(await nested && calls == 1, "reentrant lookup did not share the attempt");
                    });
        Console.WriteLine("PASS: synchronous reentrant lookup shares the published task");
    }

    private static void FailedWaitersCannotResetSuccessfulRetry(JoinableTaskFactory factory)
    {
        var ensure = CreateGate(factory);
        factory.Run(async () =>
                    {
                        var release =
                            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        var started =
                            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        var failures = 0;
                        Func<Task<bool>> fail = async () =>
                        {
                            failures++;
                            started.TrySetResult(true);
                            return await release.Task;
                        };
                        var first = ensure(fail);
                        var lateWaiter = ensure(fail);
                        await started.Task;
                        release.SetException(new InvalidOperationException("shared failure"));
                        try
                        {
                            await first;
                            throw new Exception("failure reported success");
                        }
                        catch (InvalidOperationException)
                        {
                        }

                        var successes = 0;
                        Assert(await ensure(() =>
                                            {
                                                successes++;
                                                return Task.FromResult(true);
                                            }),
                               "retry failed");
                        try
                        {
                            await lateWaiter;
                            throw new Exception("old waiter lost the shared failure");
                        }
                        catch (InvalidOperationException)
                        {
                        }
                        Assert(await ensure(() => throw new Exception("old failed waiter reset the new success")),
                               "new success lost");
                        Assert(failures == 1 && successes == 1, "failure/retry executed more than once");
                    });
        Console.WriteLine("PASS: old failed waiters preserve the successful retry");
    }

    private static Func<Func<Task<bool>>, Task<bool>> CreateGate(JoinableTaskFactory factory)
    {
        var type = typeof(ClangdVSPackage).Assembly.GetType("ClangdVS.BrokerLoadGate", true);
        var constructor = type.GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                                              null, new[] { typeof(JoinableTaskFactory) }, null);
        Assert(constructor != null, "gate must accept the injected JoinableTaskFactory");
        var gate = constructor.Invoke(new object[] { factory });
        var method =
            type.GetMethod("EnsureLoadedAsync", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        return load => (Task<bool>)method.Invoke(gate, new object[] { load });
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class NoPumpContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object state)
        {
        }
    }

#if GATE_THREADING_STANDALONE
    public static int Main()
    {
        try
        {
            RunAsync().GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL: " + ex.GetBaseException().Message);
            return 1;
        }
    }
#endif
}
