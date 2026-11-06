using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ClangdVS;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Utilities;
using Moq;

internal static class HostDocumentBindingRegression
{
    internal static async Task RunAsync()
    {
        var type = typeof(ClangdVSPackage).Assembly.GetType("ClangdVS.ClangdHoverDiagnostics");
        var method = type?.GetMethod("GetHostDocumentOpenState", BindingFlags.Static | BindingFlags.NonPublic);
        Assert(method != null, "typed host document-open state inspection is missing");
        var properties = new PropertyCollection();
        var buffer = new Mock<ITextBuffer>(MockBehavior.Strict);
        buffer.SetupGet(b => b.Properties).Returns(properties);
        var extensionClient = new object();
        bool ? State() => (bool?)method.Invoke(null, new[] { buffer.Object, extensionClient });
        Assert(State() == null, "missing context was treated as an unopened document");
        var client =
            new HoverDiagnosticsRegression.FakeClient { Name = "clangd", Client = extensionClient, IsActive = true };
        var collection = new HoverDiagnosticsRegression.FakeClientsCollection { clients = new[] { client } };
        var context = new Microsoft.VisualStudio.LanguageServer.Client.LanguageServiceClientManager.PerDocumentContext(
            collection);
        properties.AddProperty(context.GetType(), context);
        Assert(State() == false, "known active unopened clangd document was not detected");
        client.IsDocumentOpen = true;
        Assert(State() == true, "opened clangd document was not detected");
        client.Client = new object();
        Assert(State() == null, "another client's open state was used for this extension");
        client.Client = extensionClient;
        client.IsActive = false;
        Assert(State() == null, "inactive host client qualified for repair");
        client.IsActive = true;
        client.IsDisposed = true;
        Assert(State() == null, "disposed host client qualified for repair");
        client.IsDisposed = false;
        client.ShutdownToken = new CancellationToken(true);
        Assert(State() == null, "shutting-down host client qualified for repair");
        client.ShutdownToken = CancellationToken.None;
        client.ThrowOnOpenRead = true;
        Assert(State() == null, "unreadable host state qualified for repair");
        collection.clients = Array.Empty<HoverDiagnosticsRegression.FakeClient>();
        Assert(State() == null, "empty cache qualified for repair");
        collection.clients = new[] { client };
        client.ThrowOnOpenRead = false;
        client.IsDocumentOpen = false;
        var snapshot = new Mock<ITextSnapshot>().Object;
        buffer.SetupGet(b => b.CurrentSnapshot).Returns(snapshot);
        var queue = type.GetMethod("QueueHostDidOpen", BindingFlags.Static | BindingFlags.NonPublic);
        Assert(queue != null, "host notification adapter is missing");
        client.ThrowOnOpenRead = true;
        Assert(!(bool)queue.Invoke(null, new[] { buffer.Object, extensionClient }), "unknown state was sent didOpen");
        client.ThrowOnOpenRead = false;
        var otherClient =
            new HoverDiagnosticsRegression.FakeClient { Client = new object(), IsActive = true, IsDocumentOpen = true };
        collection.clients = new[] { otherClient, client };
        Assert((bool)queue.Invoke(null, new[] { buffer.Object, extensionClient }), "host didOpen was not enqueued");
        Assert(client.Notifications == 1 && ReferenceEquals(client.OpenSnapshot, snapshot),
               "host adapter failed to preserve the current unsaved snapshot");
        Assert(!(bool)queue.Invoke(null, new[] { buffer.Object, extensionClient }),
               "already-open document was enqueued again");
        Assert(client.Notifications == 1, "duplicate host didOpen notification");
        Assert(otherClient.Notifications == 0, "repair notified an unrelated language client");
        var delayed =
            new HoverDiagnosticsRegression.FakeClient { Client = extensionClient, IsActive = true, DeferOpen = true };
        collection.clients = new[] { delayed };
        Assert((bool)queue.Invoke(null, new[] { buffer.Object, extensionClient }),
               "delayed host notification was not enqueued");
        Assert(State() == false, "delayed host queue incorrectly opened the document synchronously");
        for (var attempt = 2; attempt <= 6; attempt++)
        {
            await Task.Delay(250);
            Assert(!(bool)queue.Invoke(null, new[] { buffer.Object, extensionClient }),
                   "pending host didOpen was enqueued again at retry " + attempt);
        }
        Assert(delayed.Notifications == 1, "delayed host queue received duplicate notifications");
        Console.WriteLine("PASS: six 250ms scans enqueue once while host IsDocumentOpen stays false");
        var replacement =
            new HoverDiagnosticsRegression.FakeClient { Client = extensionClient, IsActive = true, DeferOpen = true };
        collection.clients = new[] { replacement };
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 16).Select(
            _ => Task.Run(() => (bool)queue.Invoke(null, new[] { buffer.Object, extensionClient }))));
        Assert(concurrent.Count(result => result) == 1 && replacement.Notifications == 1,
               "concurrent repair either duplicated enqueue or inherited the previous host client's reservation");
        Console.WriteLine(
            "PASS: 16 concurrent scans enqueue once; a replacement host client has an independent reservation");
        var ambiguous = new HoverDiagnosticsRegression.FakeClient { Client = extensionClient, IsActive = true,
                                                                    DeferOpen = true, ThrowAfterEnqueue = true };
        collection.clients = new[] { ambiguous };
        var threw = false;
        try
        {
            queue.Invoke(null, new[] { buffer.Object, extensionClient });
        }
        catch (TargetInvocationException)
        {
            threw = true;
        }
        Assert(threw && ambiguous.Notifications == 1, "accepted-then-throwing enqueue was not exercised");
        Assert(!(bool)queue.Invoke(null, new[] { buffer.Object, extensionClient }),
               "ambiguous enqueue failure released reservation and duplicated a notification");
        Assert(ambiguous.Notifications == 1, "ambiguous enqueue was repeated");
        Console.WriteLine("PASS: enqueue that accepts then throws is not retried ambiguously");
        Assert(properties.ContainsProperty(context.GetType()), "inspection removed host properties");
        var repair = type.GetMethod("RepairHostDocumentBindingsAsync", BindingFlags.Static | BindingFlags.NonPublic);
        var currentChecks = 0;
        Func<bool> current = () =>
        {
            currentChecks++;
            return true;
        };
        await(Task) repair.Invoke(null, new object[] { extensionClient, 2, 4, current });
        Assert(currentChecks == 0, "later initialization entered cold-start repair");
        Func<bool> stale = () =>
        {
            currentChecks++;
            return false;
        };
        await(Task) repair.Invoke(null, new object[] { extensionClient, 1, 4, stale });
        Assert(currentChecks == 1, "obsolete initialization reached shell services");
        Assert(repair.ReturnType == typeof(Task<bool>),
               "repair has no explicit success result for direct didOpen fallback");
        Assert(!await(Task<bool>) repair.Invoke(null, new object[] { extensionClient, 2, 4, current }),
               "later initialization claimed a successful host repair and suppressed existing resend");
        Assert(!await(Task<bool>) repair.Invoke(null, new object[] { extensionClient, 1, 4, stale }),
               "stale initialization claimed success");
        var synchronize =
            typeof(ClangdLanguageClient)
                .GetMethod("SynchronizeInitializedDocumentsAsync", BindingFlags.Static | BindingFlags.NonPublic);
        Assert(synchronize != null, "initialization document-sync fallback routing is missing");
        var resends = 0;
        Func<Task> resend = () =>
        {
            resends++;
            return Task.CompletedTask;
        };
        Func<bool> live = () => true;
        async Task Sync(Func<bool> liveSession, Func<Task<bool>> hostRepair) =>
            await(Task) synchronize.Invoke(null, new object[] { liveSession, hostRepair, resend });
        await Sync(live, () => Task.FromResult(true));
        Assert(resends == 0, "confirmed host binding still triggered duplicate direct didOpen");
        await Sync(live, () => Task.FromResult(false));
        Assert(resends == 1, "unconfirmed/unsupported host binding suppressed server didOpen fallback");
        var stillCurrent = true;
        await Sync(() => stillCurrent, () =>
                                       {
                                           stillCurrent = false;
                                           return Task.FromResult(false);
                                       });
        Assert(resends == 1, "obsolete repair completion resent didOpen into a replacement session");
        var invoked = false;
        await Sync(() => false, () =>
                                {
                                    invoked = true;
                                    return Task.FromResult(false);
                                });
        Assert(!invoked && resends == 1, "stale initialization entered document synchronization");
        Console.WriteLine("PASS: confirmed host binding skips direct resend; unknown/failure falls back; stale " +
                          "completion cannot resend");
        Console.WriteLine("PASS: binding repair distinguishes this active client's open/unopened/unknown state and " +
                          "rejects obsolete clients");
    }

    private static void Assert(bool value, string message)
    {
        if (!value)
            throw new Exception(message);
    }
}
