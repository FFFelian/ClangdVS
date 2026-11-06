using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ClangdVS;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.Text.Projection;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;
using Moq;
using Newtonsoft.Json.Linq;

internal static class InlayHintRegression
{
    internal static Task RunAsync()
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () => {
                try { await RunCoreAsync(); done.SetResult(true); }
                catch (Exception ex) { done.SetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return done.Task;
    }

    private static async Task RunCoreAsync()
    {
        var assembly = typeof(ClangdVSPackage).Assembly;
        var provider = assembly.GetType("ClangdVS.InlayHintTaggerProvider");
        Assert(provider != null && typeof(IViewTaggerProvider).IsAssignableFrom(provider),
               "inlay hints must use a per-view space-negotiating tagger provider");
        var listener = assembly.GetType("ClangdVS.InlayHintViewListener");
        foreach (var type in new[] { provider, listener })
            Assert(type.GetCustomAttributes(typeof(ContentTypeAttribute), false)
                       .Cast<ContentTypeAttribute>().Any(a => a.ContentTypes == "C/C++"),
                   "native-born C/C++ views must participate before takeover routing");
        ProviderAttachment(provider, listener);
        await ScrollCacheRegressionsAsync();

        using (var f = new Fixture())
        {
            f.Attach();
            await f.WaitCallsAsync(1);
            var initial = f.Calls[0];
            f.Edit();
            await Task.Delay(60);
            f.Layout(1, 2);
            f.Edit();
            await Task.Delay(60);
            f.Layout(2, 3);
            f.Edit();
            Assert(f.Calls.Count == 1 && initial.Token.IsCancellationRequested,
                   "edits were not debounced or did not cancel obsolete work");
            await f.WaitCallsAsync(2);
            var call = f.Calls[1];
            Assert(call.Start == 2 && call.End == 3 && call.Uri == "file:///C:/Work/inlay.cpp",
                   "request used projection line numbers instead of mapped document range");
            call.Result.SetResult(Hints());
            await f.WaitTagsAsync(2);
            var tags = f.Tags();
            Assert(tags[0].Span.Start.Position == 12 && tags[0].Span.Length == 0 &&
                   tags[0].Tag.Affinity == PositionAffinity.Predecessor,
                   "hint must insert at the exact UTF-16 source position without replacing text");
            Assert(((TextBlock)tags[0].Tag.Adornment).Text == "type: int" &&
                   ((TextBlock)tags[1].Tag.Adornment).Text == " arg: ",
                   "clangd label parts or padding were lost");
            tags[0].Tag.Adornment.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Assert(tags[0].Tag.Adornment.DesiredSize.Width > 0,
                   "adornment cannot negotiate width before entering the visual tree");
            Assert(f.MapUpCalls > 0, "source hint positions were not mapped to the view");
            var same = f.Tags();
            Assert(ReferenceEquals(tags[0].Tag, same[0].Tag), "layout queries recreated live WPF adornments");
            Assert(f.Tagger.GetTags(new NormalizedSnapshotSpanCollection(
                       new SnapshotSpan(f.Source.Object.CurrentSnapshot, 0, 4))).Count() == 0,
                   "GetTags returned hints outside the requested spans");
            f.Layout(2, 3);
            await Task.Delay(210);
            Assert(f.Calls.Count == 2, "adornment relayout caused a request loop");
            f.Enabled = false;
            f.Notify("SettingsChanged");
            await Task.Delay(30);
            Assert(f.Tags().Length == 0 && f.Changes > 0, "settings off did not invalidate tags");
            f.Enabled = true;
            f.Notify("SettingsChanged");
            await f.WaitCallsAsync(3);
            f.Close();
            Assert(f.Calls[2].Token.IsCancellationRequested, "view close did not cancel the request");
            f.Calls[2].Result.SetResult(Hints());
            await Task.Delay(30);
            Assert(f.Tags().Length == 0, "late result resurrected a closed view");
        }

        foreach (var reason in new[] { "edit", "silent-edit", "connection", "session", "disabled", "takeover", "scroll" })
        using (var f = new Fixture())
        {
            f.Attach();
            await f.WaitCallsAsync(1);
            var pending = f.Calls[0];
            if (reason == "edit") f.Edit();
            if (reason == "silent-edit") f.Edit(notify: false);
            if (reason == "connection") f.Connection = new object(); // Even without a lifecycle notification.
            if (reason == "session") f.Notify("SessionChanged"); // Same client, new RPC connection.
            if (reason == "disabled") { f.Enabled = false; f.Notify("SettingsChanged"); }
            if (reason == "takeover") { f.Takeover = false; f.ContentChanged(); }
            if (reason == "scroll") f.Layout(0, 1);
            pending.Result.SetResult(Hints()); // Deliberately ignore cancellation at the transport boundary.
            await Task.Delay(40);
            Assert(f.Tags().Length == 0, reason + " accepted a stale inlay response");
            if (reason != "connection" && reason != "silent-edit")
                Assert(pending.Token.IsCancellationRequested, reason + " failed to cancel obsolete work");
        }

        using (var f = new Fixture { Takeover = false })
        {
            f.Attach();
            await Task.Delay(190);
            Assert(f.Calls.Count == 0, "native view requested clangd hints without takeover");
            f.Takeover = true;
            f.ContentChanged();
            await f.WaitCallsAsync(1);
            f.Calls[0].Result.SetResult(Hints());
            await f.WaitTagsAsync(2);
            f.Mappable = false;
            Assert(f.Tags().Length == 0, "unmapped projection positions leaked hints");
        }
        using (var f = new Fixture { Disjoint = true })
        {
            f.Attach();
            await f.WaitCallsAsync(1);
            Assert(f.Calls[0].Start == 0 && f.Calls[0].End == 3, "disjoint projection range was truncated");
            f.Calls[0].Result.SetResult(Hints());
            await f.WaitTagsAsync(2); // Source line 2 lies in the projection gap.
            Assert(f.Tags().All(t => t.Span.Start.Position != 12), "projection gap acquired an inlay hint");
            f.Close();
            Assert(f.Tags().Length == 0, "closing a populated view retained adornment tags");
        }
        using (var f = new Fixture())
        {
            f.Attach();
            await f.WaitCallsAsync(1);
            f.Calls[0].Result.SetResult(Hints());
            await f.WaitTagsAsync(2);
            await Task.Run(() => f.Notify("SessionChanged"));
            Assert(f.Tags().Length == 0, "background lifecycle notification retained old-session tags");
            await f.WaitCallsAsync(2);
            f.Calls[1].Result.SetResult(Hints());
            await f.WaitTagsAsync(2);
        }
        using (var f = new Fixture(trailingEmptyLine: true) { ShowThroughEof = true })
        {
            f.Attach();
            await f.WaitCallsAsync(1);
            Assert(f.Calls[0].End == 5, "visible empty last document line was omitted from the request");
        }
        Console.WriteLine("PASS: inlay space negotiation, mapped range/positions, debounce, stale replies, cancellation and lifecycle");
    }

    private static async Task ScrollCacheRegressionsAsync()
    {
        var failures = new List<Exception>();
        foreach (var test in new Func<Task>[] { GuardRefillsBeforeCacheMissAsync, PendingGuardRefillAsync,
                     GuardBoundariesDoNotLoopAsync, FailedGuardRefillRetainsCacheAsync,
                     PrefetchNextScreenAsync, ScrollMissDispatchesImmediatelyAsync,
                     PendingPrefetchSurvivesScrollAsync, PrefetchCacheIsBoundedAsync, PrefetchLifecycleAsync,
                     ScrollPreservesIdentityAsync, CachedReturnCancelsPendingAsync,
                     FailedRequestAndInvalidLayoutAsync, EmptyCoverageAndFeedbackAsync, BoundedCacheAsync,
                     PublishedLifecycleAsync, CancelledTransportRetriesAsync, ChangedLabelAndProjectionCoverageAsync,
                     PendingProjectionMappingRestoredAsync })
        {
            try { await test(); Console.WriteLine("PASS: " + test.Method.Name); }
            catch (Exception ex)
            {
                Console.Error.WriteLine("FAIL: " + test.Method.Name + ": " + ex.Message);
                failures.Add(ex);
            }
        }
        if (failures.Count != 0) throw new AggregateException(failures);
    }

    private static async Task GuardRefillsBeforeCacheMissAsync()
    {
        foreach (var down in new[] { true, false })
        using (var f = new Fixture(lineCount: 4000, directSource: true))
        {
            f.Layout(1000, 1020);
            f.Attach();
            await f.WaitCallsAsync(1);
            var first = down ? 1129 : 871;
            f.Calls[0].Result.SetResult(new JArray(Hint(first + 5, "entering")));
            await f.WaitTagsAsync(1);
            await f.WaitSettledAsync();
            var original = f.Tags()[0];
            Assert(f.Calls[0].Start == 744 && f.Calls[0].End == 1276,
                   "source query must prefetch 256 lines on both sides");
            // Exactly 128 lines remaining is covered; 127 starts a refill.
            f.Layout(down ? 1128 : 872, down ? 1148 : 892);
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;
            Assert(f.Calls.Count == 1, "guard boundary refilled before hysteresis was exhausted");
            f.Layout(first, first + 20);
            var visible = f.Tagger.GetTags(new NormalizedSnapshotSpanCollection(new SnapshotSpan(
                f.Source.Object.CurrentSnapshot, first * 5, 20 * 5 + 4))).ToArray();
            Assert(visible.Length == 1 && ReferenceEquals(visible[0], original),
                   "background refill lost synchronous access to an already cached visible hint");
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;
            Assert(f.Calls.Count == 2, "guard exhaustion did not immediately refill while visible was cached");
            var refill = f.Calls[1];
            Assert(refill.Start == first - 256 && refill.End == first + 20 + 256,
                   "guard refill did not restore the 256-line margin");
            for (var i = 0; i < 20; i++) f.Layout(first + i, first + 20 + i);
            Assert(!refill.Token.IsCancellationRequested && f.Calls.Count == 2,
                   "small layouts cancelled useful pending prefetch");
            refill.Result.SetResult(new JArray(Hint(first + 5, "entering")));
            await f.WaitSettledAsync();
            for (var i = 0; i < 20; i++) f.Layout(first + i, first + 20 + i);
            await Task.Delay(210);
            Assert(f.Calls.Count == 2 && ReferenceEquals(f.Tags()[0], original),
                   "small scrolls repeatedly refilled or replaced published tag identity");
        }
    }

    private static async Task PendingGuardRefillAsync()
    {
        foreach (var empty in new[] { true, false })
        using (var f = new Fixture(lineCount: 4000, directSource: true))
        {
            f.Layout(1000, 1020);
            f.Attach();
            await f.WaitCallsAsync(1);
            var initial = f.Calls[0];
            f.Layout(1100, 1120);
            f.Layout(1129, 1149);
            Assert(f.Calls.Count == 1 && !initial.Token.IsCancellationRequested,
                   "latest viewport inside pending query must reuse the request");
            initial.Result.SetResult(empty ? new JArray() : new JArray(Hint(1135, "retained")));
            await f.WaitCallsAsync(2); // No layout or TagsChanged is required to continue.
            Assert(!initial.Token.IsCancellationRequested && f.Calls[1].Start == 873 && f.Calls[1].End == 1405,
                   "successful pending query did not refill around the latest viewport");
            var original = f.Tags();
            Assert(empty ? f.Changes == 0 : original.Length == 1,
                   "empty coverage raised a tag change or nonempty coverage was lost");
            f.Layout(1130, 1150);
            Assert(!f.Calls[1].Token.IsCancellationRequested, "completion cleanup cancelled the new refill CTS");
            f.Calls[1].Result.SetResult(empty ? new JArray() : new JArray(Hint(1135, "retained")));
            await f.WaitSettledAsync();
            f.Layout(1129, 1149);
            await Task.Delay(210);
            Assert(f.Calls.Count == 2 && f.Tags().SequenceEqual(original),
                   "completed guard coverage looped or replaced unchanged tags");
        }
    }

    private static async Task GuardBoundariesDoNotLoopAsync()
    {
        using (var f = new Fixture(lineCount: 1500, directSource: true))
        {
            f.Layout(500, 520);
            f.Attach();
            await f.WaitCallsAsync(1);
            f.Calls[0].Result.SetResult(new JArray());
            await f.WaitSettledAsync();
            var viewports = new[] { 629, 0, 1479, 1350 };
            for (var i = 0; i < viewports.Length; i++)
            {
                var first = viewports[i];
                f.Layout(first, first + 20);
                await f.WaitCallsAsync(i + 2);
                var call = f.Calls.Last();
                Assert(call.Start == Math.Max(0, first - 256) && call.End == Math.Min(1499, first + 276),
                       "source prefetch was not clipped at BOF/EOF");
                call.Result.SetResult(new JArray());
                await f.WaitSettledAsync();
                for (var j = 0; j < 5; j++)
                {
                    var delta = first == 1479 ? -1 : 1;
                    f.Layout(first + delta, first + 20 + delta);
                    f.Layout(first, first + 20);
                }
                await Task.Delay(210);
                Assert(f.Calls.Count == i + 2 && f.Changes == 0,
                       "empty guard coverage or a clipped document boundary caused a request loop");
            }
        }
    }

    private static async Task FailedGuardRefillRetainsCacheAsync()
    {
        foreach (var failure in new[] { "exception", "cancellation", "invalid" })
        using (var f = new Fixture(lineCount: 4000, directSource: true))
        {
            f.Layout(1000, 1020);
            f.Attach();
            await f.WaitCallsAsync(1);
            f.Calls[0].Result.SetResult(new JArray(Hint(1135, "cached")));
            await f.WaitTagsAsync(1);
            await f.WaitSettledAsync();
            var original = f.Tags()[0];
            var changes = f.Changes;
            f.Layout(1129, 1149);
            await f.WaitCallsAsync(2);
            if (failure == "exception") f.Calls[1].Result.SetException(new InvalidOperationException("expected refill failure"));
            if (failure == "cancellation") f.Calls[1].Result.SetCanceled();
            if (failure == "invalid") f.Calls[1].Result.SetResult(null);
            await f.WaitSettledAsync();
            await Task.Delay(210);
            Assert(f.Calls.Count == 2 && f.Changes == changes && ReferenceEquals(f.Tags()[0], original),
                   failure + " discarded cached hints or immediately retried a failed refill");
            f.Layout(1129, 1149);
            await f.WaitCallsAsync(3);
        }
    }

    private static async Task PendingProjectionMappingRestoredAsync()
    {
        using (var f = new Fixture { Disjoint = true })
        {
            f.Attach();
            await f.WaitCallsAsync(1);
            var pending = f.Calls[0];
            var snapshot = f.Source.Object.CurrentSnapshot;
            // Collapse while the original disjoint query is still in flight.
            f.Disjoint = false;
            f.Mappable = false;
            f.Layout(3, 3);
            Assert(!pending.Token.IsCancellationRequested && f.Calls.Count == 1 &&
                   ReferenceEquals(snapshot, f.Source.Object.CurrentSnapshot),
                   "projection-only collapse invalidated a reusable source query");
            pending.Result.SetResult(new JArray(Hint(0, "collapsed"), Hint(3, "retained"), Hint(2, "query gap")));
            await f.WaitPublishedAsync();
            Assert(f.Tags().Length == 0, "temporarily unmapped source hints leaked into GetTags");
            f.Mappable = true;
            f.Disjoint = true;
            f.Layout(0, 3);
            var restored = f.Tags();
            Assert(restored.Length == 2 && restored[0].Span.Start.Position == 1 &&
                   restored[1].Span.Start.Position == 16,
                   "restored projection permanently lost covered source hints or admitted a query-gap hint");
            await Task.Delay(210);
            Assert(f.Calls.Count == 1 && f.Tags().SequenceEqual(restored),
                   "restoring mapped query coverage sent another RPC or recreated tags");
            f.Edit(notify: false);
            Assert(f.Tags().Length == 0, "restored projection bypassed source snapshot identity protection");
            f.Close();
            Assert(f.Tags().Length == 0, "closing a restored projection retained source hints");
        }
    }

    private static async Task PrefetchNextScreenAsync()
    {
        using (var f = new Fixture(lineCount: 1000, directSource: true))
        {
            f.Layout(500, 520);
            f.Attach();
            await f.WaitCallsAsync(1);
            var call = f.Calls[0];
            Assert(call.Start == 244 && call.End == 776,
                   "first request did not prefetch a bounded neighborhood above and below the viewport");
            call.Result.SetResult(new JArray(Hint(510, "visible"), Hint(540, "next screen"), Hint(450, "above")));
            await f.WaitTagsAsync(3);
            var original = f.Tags();
            f.Layout(521, 545);
            var entering = f.Tagger.GetTags(new NormalizedSnapshotSpanCollection(
                new SnapshotSpan(f.Source.Object.CurrentSnapshot, 521 * 5, 25 * 5))).ToArray();
            Assert(entering.Length == 1 && ReferenceEquals(entering[0], original[2]),
                   "newly visible hint was not immediately available from prefetch");
            f.Layout(445, 465);
            await Task.Delay(210);
            Assert(f.Calls.Count == 1 && f.Tags().SequenceEqual(original),
                   "scroll within prefetched coverage sent another RPC or recreated tags");
        }
    }

    private static async Task ScrollMissDispatchesImmediatelyAsync()
    {
        using (var f = new Fixture(lineCount: 1000, directSource: true))
        {
            f.Attach();
            await f.WaitCallsAsync(1);
            f.Calls[0].Result.SetResult(new JArray());
            await Task.Delay(40);
            f.Layout(400, 420);
            // A dispatcher barrier, not a wall-clock threshold: dispatch must be
            // queued immediately, rather than after the old 150 ms debounce.
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;
            Assert(f.Calls.Count == 2, "scroll cache miss still waits for the 150 ms edit debounce");
            var pending = f.Calls[1];
            f.Layout(800, 820);
            Assert(pending.Token.IsCancellationRequested, "scroll outside pending query did not cancel obsolete work");
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;
            Assert(f.Calls.Count == 3, "distant scroll did not immediately dispatch a replacement query");
            pending.Result.SetResult(new JArray(Hint(410, "stale")));
            await Task.Delay(40);
            Assert(f.Tags().Length == 0, "cancelled prefetch published a stale response");
        }
    }

    private static async Task PendingPrefetchSurvivesScrollAsync()
    {
        using (var f = new Fixture(lineCount: 1000, directSource: true))
        {
            f.Layout(500, 520);
            f.Attach();
            await f.WaitCallsAsync(1);
            var initial = f.Calls[0];
            f.Layout(521, 540);
            f.Layout(540, 560);
            Assert(!initial.Token.IsCancellationRequested && f.Calls.Count == 1,
                   "continuous scroll inside pending prefetch cancelled or rebuilt the query");
            initial.Result.SetResult(new JArray(Hint(250, "original"), Hint(555, "prefetched")));
            await f.WaitTagsAsync(2);
            var original = f.Tags();
            f.Layout(675, 695); // Guard miss, with overlap in the previous query.
            await f.WaitCallsAsync(2);
            var supplement = f.Calls[1];
            f.Layout(645, 665); // Cached, and also inside the pending query.
            Assert(!supplement.Token.IsCancellationRequested && f.Tags().SequenceEqual(original),
                   "cached viewport cancelled still-useful overlapping prefetch");
            supplement.Result.SetResult(new JArray(Hint(555, "prefetched"), Hint(700, "supplement")));
            await f.WaitTagsAsync(3);
            Assert(ReferenceEquals(f.Tags()[0], original[0]) && ReferenceEquals(f.Tags()[1], original[1]),
                   "prefetch reconciliation lost outside-query tags or overlapping tag identity");
            f.Layout(695, 710);
            await Task.Delay(210);
            Assert(f.Calls.Count == 2, "completed supplementary prefetch was not retained as coverage");
        }
    }

    private static async Task PrefetchCacheIsBoundedAsync()
    {
        using (var f = new Fixture(lineCount: 5000, directSource: true))
        {
            f.Layout(700, 720);
            f.Attach();
            for (var i = 0; i < 6; i++)
            {
                var first = 700 + i * 600;
                f.Layout(first, first + 20);
                await f.WaitCallsAsync(i + 1);
                var call = f.Calls.Last();
                call.Result.SetResult(new JArray(Enumerable.Range(call.Start, call.End - call.Start + 1)
                    .Select(line => Hint(line, "hint"))));
                await WaitAsync(() => f.Tags().Any(t => t.Span.Start.Position == first * 5 + 1),
                                "dense prefetch response was not published");
                await f.WaitSettledAsync();
                Assert(f.Tags().All(t => t.Span.Start.Position >= (first - 512) * 5 &&
                                         t.Span.Start.Position <= (first + 20 + 512) * 5 + 4) &&
                       f.Tags().Length <= 1045, "prefetched tags escaped the bounded viewport neighborhood");
            }
            var before = f.Calls.Count;
            f.Layout(700, 720);
            await f.WaitCallsAsync(before + 1);
            var last = f.Calls.Last();
            last.Result.SetResult(new JArray());
            await f.WaitTagsAsync(0);
            f.Layout(3700, 3720);
            await f.WaitCallsAsync(before + 2);
            f.Calls.Last().Result.SetResult(new JArray());
            await Task.Delay(40);
            f.Layout(700, 720);
            await f.WaitCallsAsync(before + 3);
        }
    }

    private static async Task PrefetchLifecycleAsync()
    {
        foreach (var reason in new[] { "edit", "session-stop", "close" })
        using (var f = new Fixture(lineCount: 2000, directSource: true))
        {
            f.Layout(500, 520);
            f.Attach();
            await f.WaitCallsAsync(1);
            f.Calls[0].Result.SetResult(new JArray(Hint(510, "visible"), Hint(540, "prefetched")));
            await f.WaitTagsAsync(2);
            f.Layout(629, 649);
            await f.WaitCallsAsync(2);
            var pending = f.Calls[1];
            if (reason == "edit")
            {
                f.Tagger.TagsChanged += (s, e) => f.Layout(629, 649);
                f.Edit();
            }
            if (reason == "session-stop") { f.Connection = null; f.Notify("SessionChanged"); }
            if (reason == "close") f.Close();
            Assert(f.Tags().Length == 0 && pending.Token.IsCancellationRequested,
                   reason + " did not clear prefetched tags and cancel pending work");
            pending.Result.SetResult(new JArray(Hint(635, "stale")));
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;
            Assert(f.Tags().Length == 0, reason + " resurrected prefetched tags");
            if (reason == "edit")
            {
                Assert(f.Calls.Count == 2, "edit refresh bypassed the debounce");
                f.Layout(630, 650);
                f.Edit();
                await Task.Delay(60);
                Assert(f.Calls.Count == 2, "layout bypassed pending edit debounce");
                await f.WaitCallsAsync(3);
            }
        }
    }

    private static async Task ScrollPreservesIdentityAsync()
    {
        using (var f = new Fixture())
        {
            f.Attach();
            await f.WaitCallsAsync(1);
            f.Calls[0].Result.SetResult(Hints());
            await f.WaitTagsAsync(2);
            var original = f.Tags();
            var changes = f.Changes;
            f.Layout(1, 3);
            Assert(f.Tags().SequenceEqual(original) && f.Changes == changes,
                   "scroll cleared published tags before the replacement response");
            await f.WaitCallsAsync(2);
            f.Calls[1].Result.SetResult(new JArray(Hint(2, "type: int", 2),
                                                  Hint(3, " arg: ", 1)));
            await Task.Delay(40);
            Assert(f.Tags().SequenceEqual(original) && f.Changes == changes,
                   "unchanged overlapping response recreated tag/UI objects or raised TagsChanged");
            f.Layout(3, 4);
            Assert(f.Tags().SequenceEqual(original), "new scheduled request hid the cache");
            await f.WaitCallsAsync(3);
            f.Calls[2].Result.SetResult(new JArray(Hint(3, " arg: ", 1), Hint(4, "new")));
            await f.WaitTagsAsync(3);
            var merged = f.Tags();
            Assert(ReferenceEquals(merged[0], original[0]) && ReferenceEquals(merged[1], original[1]) &&
                   ReferenceEquals(merged[1].Tag, original[1].Tag) &&
                   ReferenceEquals(merged[1].Tag.Adornment, original[1].Tag.Adornment),
                   "range reconciliation lost outside-range tags or overlapping object identity");
            Assert(f.Changes == changes + 1, "range reconciliation notified without a tag change");
            f.Layout(1, 4);
            await Task.Delay(210);
            Assert(f.Calls.Count == 3, "merged coverage failed to cover a cross-line viewport");
        }
    }

    private static async Task CachedReturnCancelsPendingAsync()
    {
        using (var f = new Fixture())
        {
            f.Attach();
            await f.WaitCallsAsync(1);
            f.Calls[0].Result.SetResult(Hints());
            await f.WaitTagsAsync(2);
            var original = f.Tags();
            var changes = f.Changes;
            f.Layout(3, 4);
            await f.WaitCallsAsync(2);
            var pending = f.Calls[1];
            f.Layout(2, 3);
            Assert(pending.Token.IsCancellationRequested && f.Tags().SequenceEqual(original),
                   "return to cached viewport failed to cancel pending work without clearing tags");
            pending.Result.SetResult(new JArray(Hint(3, "stale"), Hint(4, "stale")));
            await Task.Delay(210);
            Assert(f.Calls.Count == 2 && f.Changes == changes && f.Tags().SequenceEqual(original),
                   "late cancelled response changed cached tags or started another request");
        }
    }

    private static async Task FailedRequestAndInvalidLayoutAsync()
    {
        using (var f = new Fixture())
        {
            f.Attach();
            await f.WaitCallsAsync(1);
            f.Calls[0].Result.SetResult(Hints());
            await f.WaitTagsAsync(2);
            var original = f.Tags();
            var changes = f.Changes;
            f.Layout(3, 4);
            await f.WaitCallsAsync(2);
            f.Calls[1].Result.SetException(new InvalidOperationException("expected inlay request failure"));
            await Task.Delay(40);
            Assert(f.Tags().SequenceEqual(original) && f.Changes == changes,
                   "supplementary request failure discarded published tags");
            f.Layout(3, 4);
            await f.WaitCallsAsync(3);
            f.ValidLines = false;
            f.Layout(3, 4);
            Assert(f.Tags().SequenceEqual(original) && f.Changes == changes &&
                   f.Calls[2].Token.IsCancellationRequested,
                   "temporarily invalid layout cleared the cache or retained obsolete pending work");
            f.Calls[2].Result.SetResult(new JArray(Hint(3, "stale")));
            await Task.Delay(40);
            f.ValidLines = true;
            f.Layout(3, 4);
            await f.WaitCallsAsync(4);
            f.Calls[3].Result.SetResult(new JArray());
            await f.WaitTagsAsync(1);
            Assert(ReferenceEquals(f.Tags()[0], original[0]),
                   "empty result removed tags outside its replacement range");
            f.Layout(2, 4);
            await Task.Delay(210);
            Assert(f.Calls.Count == 4 && f.Changes == changes + 1,
                   "empty coverage was forgotten or a failed request was treated as covered");
        }
    }

    private static async Task EmptyCoverageAndFeedbackAsync()
    {
        using (var f = new Fixture())
        {
            f.Attach();
            await f.WaitCallsAsync(1);
            f.Calls[0].Result.SetResult(new JArray());
            await Task.Delay(40);
            Assert(f.Changes == 0, "empty initial result raised TagsChanged without changing tags");
            f.Layout(2, 3);
            await Task.Delay(210);
            Assert(f.Calls.Count == 1, "empty coverage caused repeated layout requests");
            f.Tagger.TagsChanged += (s, e) => f.Layout(1, 3);
            f.Layout(1, 3);
            await f.WaitCallsAsync(2);
            f.Calls[1].Result.SetResult(new JArray(Hint(1, "first"), Hint(1, "second")));
            await f.WaitTagsAsync(2);
            var original = f.Tags();
            f.Layout(0, 3);
            await f.WaitCallsAsync(3);
            f.Calls[2].Result.SetResult(new JArray(Hint(1, "first"), Hint(1, "second")));
            await Task.Delay(210);
            Assert(f.Tags().SequenceEqual(original) && f.Changes == 1 && f.Calls.Count == 3,
                   "same-position hints lost identity or TagsChanged/layout created a request loop");
        }
    }

    private static async Task BoundedCacheAsync()
    {
        using (var f = new Fixture(lineCount: 4000))
        {
            f.Attach();
            await f.WaitCallsAsync(1);
            f.Calls[0].Result.SetResult(Hints());
            await f.WaitTagsAsync(2);
            for (var i = 1; i <= 5; i++)
            {
                var first = i * 600;
                f.Layout(first, first + 1);
                await f.WaitCallsAsync(i + 1);
                f.Calls[i].Result.SetResult(new JArray(Hint(first, "hint")));
                await WaitAsync(() => f.Tags().Any(t => t.Span.Start.Position == first * 5 + 1),
                                "new viewport hint was not published");
                Assert(f.Tags().All(t => t.Span.Start.Position >= Math.Max(0, first - 512) * 5) &&
                       f.Tags().Length <= 3, "scroll cache grew beyond a bounded line window");
            }
            f.Layout(2, 3);
            await f.WaitCallsAsync(7);
            f.Calls[6].Result.SetResult(new JArray());
            await f.WaitTagsAsync(0);
            f.Layout(3000, 3001);
            await f.WaitCallsAsync(8);
            f.Calls[7].Result.SetResult(new JArray());
            await Task.Delay(40);
            f.Layout(2, 3);
            await f.WaitCallsAsync(9); // Empty coverage must be evicted along with distant tags.
        }
    }

    private static async Task PublishedLifecycleAsync()
    {
        foreach (var reason in new[] { "edit", "silent-edit", "connection", "session", "disabled", "takeover", "uri", "close" })
        using (var f = new Fixture())
        {
            f.Attach();
            await f.WaitCallsAsync(1);
            f.Calls[0].Result.SetResult(Hints());
            await f.WaitTagsAsync(2);
            if (reason == "edit") f.Edit();
            if (reason == "silent-edit") f.Edit(notify: false);
            if (reason == "connection") f.Connection = new object();
            if (reason == "session") f.Notify("SessionChanged");
            if (reason == "disabled") { f.Enabled = false; f.Notify("SettingsChanged"); }
            if (reason == "takeover") { f.Takeover = false; f.ContentChanged(); }
            if (reason == "uri") f.FilePath = @"C:\Work\renamed.cpp";
            if (reason == "close") f.Close();
            Assert(f.Tags().Length == 0, reason + " retained published tags from an invalid cache identity");
        }
    }

    private static async Task CancelledTransportRetriesAsync()
    {
        using (var f = new Fixture())
        {
            f.Attach();
            await f.WaitCallsAsync(1);
            f.Calls[0].Result.SetResult(Hints());
            await f.WaitTagsAsync(2);
            var original = f.Tags();
            var changes = f.Changes;
            f.Layout(3, 4);
            await f.WaitCallsAsync(2);
            f.Calls[1].Result.SetCanceled(); // Transport cancellation, not a new viewport.
            await Task.Delay(40);
            Assert(f.Tags().SequenceEqual(original) && f.Changes == changes,
                   "transport cancellation discarded the published cache");
            f.Layout(3, 4);
            await f.WaitCallsAsync(3);
            f.Calls[2].Result.SetResult(null);
            await Task.Delay(40);
            Assert(f.Tags().SequenceEqual(original) && f.Changes == changes,
                   "invalid response was treated as an empty replacement");
            f.Layout(3, 4);
            await f.WaitCallsAsync(4);
        }
    }

    private static async Task ChangedLabelAndProjectionCoverageAsync()
    {
        using (var f = new Fixture { Disjoint = true })
        {
            f.Attach();
            await f.WaitCallsAsync(1);
            f.Calls[0].Result.SetResult(Hints());
            await f.WaitTagsAsync(2);
            var outside = f.Tags()[0];
            var original = f.Tags()[1];
            f.Disjoint = false;
            f.Layout(2, 3); // The RPC envelope covered line 2, but the projection did not.
            await f.WaitCallsAsync(2);
            Assert(f.Tags().Contains(original), "newly mapped projection range cleared existing hints");
            f.Calls[1].Result.SetResult(new JArray(Hint(2, "mapped"), Hint(3, "changed", 1)));
            await f.WaitTagsAsync(3);
            Assert(ReferenceEquals(f.Tags()[0], outside) &&
                   !ReferenceEquals(f.Tags()[2].Tag.Adornment, original.Tag.Adornment) &&
                   ((TextBlock)f.Tags()[2].Tag.Adornment).Text == "changed",
                   "changed label was not replaced or projection-gap coverage erased outside tags");
        }
    }

    private static JObject Hint(int line, string label, int character = 1) => new JObject {
        ["position"] = new JObject { ["line"] = line, ["character"] = character }, ["label"] = label,
    };

    private static JArray Hints() => JArray.Parse(@"[
        {'position':{'line':2,'character':2},'label':[{'value':'type: '},{'value':'int'}]},
        {'position':{'line':3,'character':1},'label':'arg:','paddingLeft':true,'paddingRight':true},
        {'position':{'line':2,'character':99},'label':'invalid column'},
        {'position':{'line':-1,'character':0},'label':'invalid line'},
        {'position':{'line':0,'character':0},'label':'outside visible range'}]");

    private static void ProviderAttachment(Type providerType, Type listenerType)
    {
        var view = new Mock<IWpfTextView>();
        var source = new Mock<ITextBuffer>();
        var projection = new Mock<ITextBuffer>();
        var model = new Mock<ITextDataModel>();
        var content = new Mock<IContentType>();
        content.Setup(c => c.IsOfType("C/C++")).Returns(true);
        source.SetupGet(b => b.ContentType).Returns(content.Object);
        source.SetupGet(b => b.CurrentSnapshot).Returns(Snapshot(source.Object, 0));
        view.SetupGet(v => v.VisualElement).Returns(new Grid());
        view.SetupGet(v => v.Properties).Returns(new PropertyCollection());
        view.SetupGet(v => v.TextBuffer).Returns(projection.Object);
        view.SetupGet(v => v.TextDataModel).Returns(model.Object);
        model.SetupGet(m => m.DocumentBuffer).Returns(source.Object);
        var documents = new Mock<ITextDocumentFactoryService>();
        ITextDocument document = new Mock<ITextDocument>().Object;
        documents.Setup(d => d.TryGetTextDocument(source.Object, out document)).Returns(true);
        var provider = (IViewTaggerProvider)Activator.CreateInstance(providerType, true);
        providerType.GetField("Documents", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(provider, documents.Object);
        var listener = (IWpfTextViewCreationListener)Activator.CreateInstance(listenerType, true);
        listenerType.GetField("Documents", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(listener, documents.Object);
        listener.TextViewCreated(view.Object); // Native-born view, no takeover required to attach.
        var first = provider.CreateTagger<IntraTextAdornmentTag>(view.Object, source.Object);
        Assert(first != null && ReferenceEquals(first, provider.CreateTagger<IntraTextAdornmentTag>(view.Object, source.Object)),
               "listener/provider did not share one coordinator for the document buffer");
        Assert(provider.CreateTagger<IntraTextAdornmentTag>(view.Object, projection.Object) == null,
               "provider attached to a projection instead of the document buffer");
        view.Raise(v => v.Closed += null, EventArgs.Empty);
    }

    private sealed class Call
    {
        internal string Uri;
        internal int Start, End;
        internal CancellationToken Token;
        internal readonly TaskCompletionSource<JToken> Result =
            new TaskCompletionSource<JToken>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Fixture : IDisposable
    {
        internal bool Enabled = true, Takeover = true, Mappable = true, ValidLines = true, Disjoint, ShowThroughEof;
        internal string FilePath = @"C:\Work\inlay.cpp";
        internal object Connection = new object();
        internal readonly List<Call> Calls = new List<Call>();
        internal readonly Mock<ITextBuffer> Source = new Mock<ITextBuffer>();
        internal readonly ITagger<IntraTextAdornmentTag> Tagger;
        internal int Changes, MapUpCalls;
        private readonly Mock<IWpfTextView> view = new Mock<IWpfTextView>();
        private readonly Mock<IBufferGraph> graph = new Mock<IBufferGraph>();
        private readonly Mock<IWpfTextViewLineCollection> lines = new Mock<IWpfTextViewLineCollection>();
        private readonly Mock<ITextBuffer> surface = new Mock<ITextBuffer>();
        private readonly Type type = typeof(ClangdVSPackage).Assembly.GetType("ClangdVS.InlayHintCoordinator");
        private readonly object coordinator;
        private int start = 2, end = 3, version;
        private readonly int lineCount;
        private bool closed;

        internal Fixture(bool trailingEmptyLine = false, int lineCount = 5, bool directSource = false)
        {
            this.lineCount = lineCount;
            var snapshot = Snapshot(Source.Object, 0, trailingEmptyLine, lineCount);
            Source.SetupGet(b => b.CurrentSnapshot).Returns(snapshot);
            surface.SetupGet(b => b.CurrentSnapshot).Returns(Snapshot(surface.Object, 0));
            view.SetupGet(v => v.VisualElement).Returns(new Grid());
            view.SetupGet(v => v.Properties).Returns(new PropertyCollection());
            view.SetupGet(v => v.IsClosed).Returns(() => closed);
            view.SetupGet(v => v.TextBuffer).Returns(Source.Object);
            view.SetupGet(v => v.TextSnapshot).Returns(() => Source.Object.CurrentSnapshot);
            view.SetupGet(v => v.VisualSnapshot).Returns(() => directSource ? Source.Object.CurrentSnapshot : surface.Object.CurrentSnapshot);
            view.SetupGet(v => v.BufferGraph).Returns(graph.Object);
            view.SetupGet(v => v.TextViewLines).Returns(lines.Object);
            lines.SetupGet(l => l.Count).Returns(2);
            lines.SetupGet(l => l.IsValid).Returns(() => ValidLines);
            var firstLine = new Mock<IWpfTextViewLine>();
            var lastLine = new Mock<IWpfTextViewLine>();
            firstLine.SetupGet(l => l.Start).Returns(() => directSource
                ? new SnapshotPoint(Source.Object.CurrentSnapshot, start * 5)
                : new SnapshotPoint(surface.Object.CurrentSnapshot, 0));
            lastLine.SetupGet(l => l.End).Returns(() => directSource
                ? new SnapshotPoint(Source.Object.CurrentSnapshot, end * 5 + 4)
                : new SnapshotPoint(surface.Object.CurrentSnapshot, 9));
            lines.SetupGet(l => l.FirstVisibleLine).Returns(firstLine.Object);
            lines.SetupGet(l => l.LastVisibleLine).Returns(lastLine.Object);
            graph.Setup(g => g.MapDownToBuffer(It.IsAny<SnapshotSpan>(), It.IsAny<SpanTrackingMode>(), Source.Object))
                 .Returns(() => ShowThroughEof
                     ? new NormalizedSnapshotSpanCollection(new SnapshotSpan(
                         Source.Object.CurrentSnapshot, 0, Source.Object.CurrentSnapshot.Length))
                     : Disjoint
                     ? new NormalizedSnapshotSpanCollection(new[] {
                         new SnapshotSpan(Source.Object.CurrentSnapshot, 0, 4),
                         new SnapshotSpan(Source.Object.CurrentSnapshot, 15, 4) })
                     : new NormalizedSnapshotSpanCollection(new SnapshotSpan(
                         Source.Object.CurrentSnapshot, start * 5, (end - start) * 5 + 4)));
            graph.Setup(g => g.MapUpToSnapshot(It.IsAny<SnapshotPoint>(), It.IsAny<PointTrackingMode>(),
                                              It.IsAny<PositionAffinity>(), It.IsAny<ITextSnapshot>()))
                 .Returns<SnapshotPoint, PointTrackingMode, PositionAffinity, ITextSnapshot>((p, t, a, s) => {
                     MapUpCalls++;
                     Assert(ReferenceEquals(s, surface.Object.CurrentSnapshot),
                            "source hint was mapped to the edit snapshot instead of the visual projection");
                     return Mappable ? (SnapshotPoint?)new SnapshotPoint(s, 1) : null;
                 });
            var document = new Mock<ITextDocument>();
            document.SetupGet(d => d.FilePath).Returns(() => FilePath);
            Func<object, string, int, int, CancellationToken, Task<JToken>> request = (c, uri, first, last, token) => {
                Assert(ReferenceEquals(c, Connection), "request used an obsolete connection");
                var call = new Call { Uri = uri, Start = first, End = last, Token = token };
                Calls.Add(call);
                return call.Result.Task;
            };
            coordinator = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null,
                new object[] { view.Object, Source.Object, document.Object, (Func<bool>)(() => Enabled && Takeover),
                               (Func<object>)(() => Connection), request }, null);
            Tagger = (ITagger<IntraTextAdornmentTag>)coordinator;
            Tagger.TagsChanged += (s, e) => Changes++;
        }

        internal void Attach() => type.GetMethod("Attach", BindingFlags.Instance | BindingFlags.NonPublic)
                                      .Invoke(coordinator, null);
        internal void Notify(string method) => type.GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)
                                                   .Invoke(null, null);
        internal void Layout(int first, int last)
        {
            start = first; end = last;
            view.Raise(v => v.LayoutChanged += null, (TextViewLayoutChangedEventArgs)null);
        }
        internal void ContentChanged() => Source.Raise(b => b.ContentTypeChanged += null, (ContentTypeChangedEventArgs)null);
        internal void Edit(bool notify = true)
        {
            Source.SetupGet(b => b.CurrentSnapshot).Returns(Snapshot(Source.Object, ++version, lineCount: lineCount));
            if (notify)
                Source.Raise(b => b.Changed += null, (TextContentChangedEventArgs)null);
        }
        internal ITagSpan<IntraTextAdornmentTag>[] Tags() => Tagger.GetTags(new NormalizedSnapshotSpanCollection(
            new SnapshotSpan(Source.Object.CurrentSnapshot, 0, Source.Object.CurrentSnapshot.Length))).ToArray();
        internal async Task WaitCallsAsync(int count) => await WaitAsync(() => Calls.Count >= count, "request not sent");
        internal async Task WaitTagsAsync(int count) => await WaitAsync(() => Tags().Length == count, "tags not published");
        internal async Task WaitPublishedAsync() => await WaitAsync(() =>
            type.GetField("_published", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(coordinator) != null,
            "response was not published");
        internal async Task WaitSettledAsync() => await WaitAsync(() =>
            type.GetField("_scheduled", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(coordinator) == null,
            "request did not settle");
        internal void Close()
        {
            if (closed) return;
            closed = true;
            view.Raise(v => v.Closed += null, EventArgs.Empty);
        }
        public void Dispose()
        {
            Close();
            foreach (var call in Calls)
                if (!call.Result.Task.IsCompleted)
                    call.Result.TrySetCanceled();
        }
    }

    private static ITextSnapshot Snapshot(ITextBuffer buffer, int number, bool trailingEmptyLine = false, int lineCount = 5)
    {
        var text = string.Join("\n", Enumerable.Repeat("aaaa", lineCount)) + (trailingEmptyLine ? "\n" : "");
        var snapshot = new Mock<ITextSnapshot>();
        var version = new Mock<ITextVersion>();
        version.SetupGet(v => v.VersionNumber).Returns(number);
        snapshot.SetupGet(s => s.Version).Returns(version.Object);
        snapshot.SetupGet(s => s.TextBuffer).Returns(buffer);
        snapshot.SetupGet(s => s.Length).Returns(text.Length);
        snapshot.SetupGet(s => s.LineCount).Returns(trailingEmptyLine ? lineCount + 1 : lineCount);
        Func<int, ITextSnapshotLine> line = n => {
            var result = new Mock<ITextSnapshotLine>();
            result.SetupGet(l => l.LineNumber).Returns(n);
            result.SetupGet(l => l.Start).Returns(new SnapshotPoint(snapshot.Object, n * 5));
            var length = n == lineCount ? 0 : 4;
            result.SetupGet(l => l.End).Returns(new SnapshotPoint(snapshot.Object, n * 5 + length));
            result.SetupGet(l => l.Length).Returns(length);
            return result.Object;
        };
        snapshot.Setup(s => s.GetLineFromLineNumber(It.IsAny<int>())).Returns<int>(n => line(n));
        snapshot.Setup(s => s.GetLineFromPosition(It.IsAny<int>())).Returns<int>(p => line(p / 5));
        return snapshot.Object;
    }

    private static async Task WaitAsync(Func<bool> condition, string message)
    {
        for (var i = 0; i < 150 && !condition(); i++) await Task.Delay(10);
        Assert(condition(), message);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
