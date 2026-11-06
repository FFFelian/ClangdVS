using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

internal static class LspInitializeRegression
{
    private const string Initialize = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"capabilities\":{}}}";

    internal static async Task RunAsync()
    {
        await PartialFramesAndUnicodeAsync();
        await CombinedFramesAndPreservedCapabilitiesAsync();
        await NullIdRequestAsync();
        await UnchangedMessagesAsync();
        await CancellationAndSerializationAsync();
        await FailureAndLifetimeAsync();
        HeaderLimitsAndInvalidInput();
        Console.WriteLine("PASS: LspInitializeStream framing, capabilities, cancellation, bounds and lifetime");
    }

    // Resolve the internal type without depending on a particular assembly layout. This also lets
    // the isolated runner demonstrate a real red test before the implementation file exists.
    private static Stream Create(Stream inner, Func<bool> enabled)
    {
        var type = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType("ClangdVS.LspInitializeStream"))
            .FirstOrDefault(candidate => candidate != null);
        Assert(type != null, "LspInitializeStream is missing");
        Assert(type.IsSealed && !type.IsPublic, "initialize stream must be internal sealed");
        return (Stream)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic,
            null, new object[] { inner, enabled }, CultureInfo.InvariantCulture);
    }

    private static async Task PartialFramesAndUnicodeAsync()
    {
        var json = Initialize.Replace("\"capabilities\":{}", "\"rootUri\":\"file:///项目/😀\",\"capabilities\":{}");
        var frame = Frame(json);
        // Every split includes splits inside CRLFCRLF and inside a multibyte UTF-8 character.
        for (var split = 1; split < frame.Length; split++)
        {
            var output = new RecordingStream();
            var calls = 0;
            using (var stream = Create(output, () => { calls++; return true; }))
            {
                Assert(!stream.CanRead && !stream.CanSeek && stream.CanWrite, "incorrect stream capabilities");
                stream.Write(frame, 0, split);
                stream.Flush();
                await stream.FlushAsync(CancellationToken.None);
                Assert(output.Length == 0 && calls == 0, "partial frame escaped or evaluated settings early");
                await stream.WriteAsync(frame, split, frame.Length - split, CancellationToken.None);
                var messages = ReadFrames(output.ToArray());
                Assert(messages.Count == 1, "split write corrupted framing");
                AssertCapability(messages[0], true);
                Assert((string)JObject.Parse(messages[0])["params"]["rootUri"] == "file:///项目/😀",
                    "Unicode initialize params changed");
                Assert(calls == 1, "settings must be sampled once per complete initialize request");
                Assert(output.Writes <= 2, "frame was emitted using tiny writes");
            }
        }

        var byteOutput = new RecordingStream();
        using (var stream = Create(byteOutput, () => true))
        {
            foreach (var value in frame)
                stream.WriteByte(value);
            AssertCapability(ReadFrames(byteOutput.ToArray()).Single(), true);
            Assert(byteOutput.Writes <= 2, "one-byte input caused one-byte downstream writes");
        }
    }

    private static async Task CombinedFramesAndPreservedCapabilitiesAsync()
    {
        const string json = "{\"jsonrpc\":\"2.0\",\"id\":\"启动\",\"method\":\"initialize\",\"params\":{\"rootUri\":\"file:///中文\",\"capabilities\":{\"workspace\":{\"configuration\":true},\"textDocument\":{\"hover\":{\"contentFormat\":[\"markdown\"]},\"inactiveRegionsCapabilities\":{\"inactiveRegions\":true,\"extension\":7}}},\"initializationOptions\":{\"date\":\"2026-01-01T00:00:00+08:00\"}}}";
        var first = Frame(" { \"jsonrpc\":\"2.0\", \"method\":\"initialized\", \"params\":{} } ", true);
        var last = Frame("{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{\"中文\":\"😀\"}}", true);
        var output = new RecordingStream();
        var enabled = true;
        var calls = 0;
        using (var stream = Create(output, () => { calls++; return enabled; }))
        {
            enabled = false;
            var bytes = Join(first, Frame(json, true), last, Frame(Initialize));
            await stream.WriteAsync(bytes, 0, bytes.Length, CancellationToken.None);
            var result = output.ToArray();
            Assert(result.Take(first.Length).SequenceEqual(first), "leading frame changed");
            var messages = ReadFrames(result);
            Assert(messages.Count == 4, "coalesced frames lost or reordered");
            AssertCapability(messages[1], false);
            AssertCapability(messages[3], false);
            var expected = JObject.Parse(json);
            expected["params"]["capabilities"]["textDocument"]["inactiveRegionsCapabilities"]["inactiveRegions"] = false;
            Assert(JToken.DeepEquals(expected, JObject.Parse(messages[1])), "existing initialize fields changed");
            var text = Encoding.UTF8.GetString(result);
            Assert(text.Contains("cOnTeNt-LeNgTh:\t") && text.Contains("\t \r\nX-Trace: keep  spaces\r\n"),
                "Content-Length spelling/whitespace or other headers changed");
            Assert(text.Contains("Content-Type: application/vscode-jsonrpc; charset=utf-8\r\n"), "Content-Type was lost");
            Assert(calls == 2, "settings were sampled for non-initialize messages");
            enabled = true;
            var next = Frame("{\"id\":3,\"method\":\"initialize\"}");
            stream.Write(next, 0, next.Length);
            AssertCapability(ReadFrames(output.ToArray()).Last(), true);
        }
    }

    private static async Task UnchangedMessagesAsync()
    {
        var frames = new[] {
            Frame(" { \"id\":9, \"method\":\"workspace/configuration\", \"params\":{\"date\":\"2026-01-01T00:00:00Z\"} } ", true),
            Frame("{\"method\":\"initialize\",\"params\":{\"capabilities\":{}}}"), // notification
            Frame("{\"id\":1,\"result\":{\"method\":\"initialize\"}}"),
            Frame("{\"id\":2,\"method\":\"INITIALIZE\",\"params\":{}}"),
            Frame("{\"id\":3,\"method\":\"initialize\",\"result\":{}}"),
            Frame("not JSON: 中文 😀", true),
            Frame("[]"), Frame("")
        };
        var input = Join(frames);
        var output = new RecordingStream();
        using (var stream = Create(output, () => { throw new Exception("settings called for another message"); }))
        {
            var padded = Join(new byte[] { 1, 2, 3 }, input, new byte[] { 4 });
            await stream.WriteAsync(padded, 3, input.Length, CancellationToken.None);
            stream.Flush();
            Assert(output.ToArray().SequenceEqual(input), "non-initialize frames did not remain byte-for-byte identical");
        }

        const string alreadyEnabled = " { \"id\":1, \"method\":\"initialize\", \"params\":{\"capabilities\":{\"textDocument\":{\"inactiveRegionsCapabilities\":{\"inactiveRegions\":true}}}} } ";
        var original = Frame(alreadyEnabled, true);
        var unchanged = new RecordingStream();
        using (var stream = Create(unchanged, () => true))
        {
            stream.Write(original, 0, original.Length);
            Assert(unchanged.ToArray().SequenceEqual(original), "already-correct initialize was unnecessarily reserialized");
        }

        var binary = Join(Encoding.ASCII.GetBytes("Content-Length: 3\r\n\r\n"), new byte[] { 0xff, 0x00, 0xfe });
        var binaryOutput = new RecordingStream();
        using (var stream = Create(binaryOutput, () => { throw new Exception("invalid UTF-8 evaluated settings"); }))
        {
            stream.Write(binary, 0, binary.Length);
            Assert(binaryOutput.ToArray().SequenceEqual(binary), "invalid UTF-8 was silently swallowed or changed");
        }
    }

    private static async Task NullIdRequestAsync()
    {
        // JSON-RPC permits a null request ID (although it discourages it). It is still distinct
        // from a notification, which omits the ID entirely.
        var input = Frame("{\"jsonrpc\":\"2.0\",\"id\":null,\"method\":\"initialize\",\"params\":null}");
        var output = new RecordingStream();
        using (var stream = Create(output, () => false))
        {
            await stream.WriteAsync(input, 0, input.Length, CancellationToken.None);
            AssertCapability(ReadFrames(output.ToArray()).Single(), false);
        }
    }

    private static async Task CancellationAndSerializationAsync()
    {
        var frame = Frame(Initialize);
        var output = new RecordingStream();
        using (var stream = Create(output, () => true))
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => stream.WriteAsync(frame, 0, frame.Length, canceled.Token));
            Assert(output.Length == 0, "pre-canceled write emitted bytes");
            stream.Write(frame, 0, 11);
            await ThrowsAsync<OperationCanceledException>(() => stream.WriteAsync(frame, 11, frame.Length - 11, canceled.Token));
            await ThrowsAsync<OperationCanceledException>(() => stream.FlushAsync(canceled.Token));
            await stream.WriteAsync(frame, 11, frame.Length - 11, CancellationToken.None);
            AssertCapability(ReadFrames(output.ToArray()).Single(), true);
        }

        var blocked = new PausingStream();
        using (var stream = Create(blocked, () => true))
        using (var canceled = new CancellationTokenSource())
        {
            var first = Frame("{\"id\":10,\"result\":{}}");
            var second = Frame("{\"id\":11,\"result\":{}}");
            var writing = stream.WriteAsync(first, 0, first.Length, CancellationToken.None);
            await blocked.Entered.Task;
            var waiting = stream.WriteAsync(second, 0, second.Length, canceled.Token);
            canceled.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => waiting);
            var next = stream.WriteAsync(second, 0, second.Length, CancellationToken.None);
            var flushing = stream.FlushAsync(CancellationToken.None);
            Assert(!next.IsCompleted && !flushing.IsCompleted, "write/flush bypassed the active frame lock");
            blocked.Release.TrySetResult(true);
            await Task.WhenAll(writing, next, flushing);
            Assert(blocked.ToArray().SequenceEqual(Join(first, second)), "concurrent writes interleaved frames");
        }

        var interrupted = new PausingStream();
        var interruptedStream = Create(interrupted, () => true);
        using (var canceled = new CancellationTokenSource())
        {
            var writing = interruptedStream.WriteAsync(frame, 0, frame.Length, canceled.Token);
            await interrupted.Entered.Task;
            canceled.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => writing);
            Throws<IOException>(() => interruptedStream.Write(frame, 0, frame.Length));
            interruptedStream.Dispose();
            Assert(interrupted.Disposed, "canceled downstream write did not release stream on dispose");
        }
    }

    private static async Task FailureAndLifetimeAsync()
    {
        foreach (var partial in new[] { Encoding.ASCII.GetBytes("Content-Len"), Frame(Initialize).Take(40).ToArray() })
        {
            var output = new RecordingStream();
            var stream = Create(output, () => true);
            stream.Write(partial, 0, partial.Length);
            await stream.FlushAsync(CancellationToken.None);
            Assert(output.Length == 0, "Flush forwarded a truncated frame");
            Throws<EndOfStreamException>(() => stream.Dispose());
            Assert(output.Disposed, "truncation prevented disposal of inner stream");
            stream.Dispose();
            Assert(!stream.CanWrite, "disposed stream still reports writable");
            Throws<ObjectDisposedException>(() => stream.Write(new byte[0], 0, 0));
        }

        var sink = new RecordingStream();
        var wrapper = Create(sink, () => true);
        Throws<NotSupportedException>(() => wrapper.Read(new byte[1], 0, 1));
        Throws<NotSupportedException>(() => wrapper.Seek(0, SeekOrigin.Begin));
        Throws<NotSupportedException>(() => wrapper.SetLength(0));
        wrapper.Dispose();
        Assert(sink.Disposed, "normal disposal did not release inner stream");

        var failure = new IOException("downstream failure");
        var failing = new FailingStream(failure);
        var streamWithFailure = Create(failing, () => true);
        var frame = Frame(Initialize);
        var caught = await ThrowsAsync<IOException>(() => streamWithFailure.WriteAsync(frame, 0, frame.Length, CancellationToken.None));
        Assert(ReferenceEquals(caught, failure), "downstream error was swallowed/replaced");
        Throws<IOException>(() => streamWithFailure.Flush());
        streamWithFailure.Dispose();
        Assert(failing.Disposed, "faulted stream did not dispose inner stream");

        var settingsFailure = new InvalidOperationException("settings failure");
        var settingsOutput = new RecordingStream();
        var settingsStream = Create(settingsOutput, () => { throw settingsFailure; });
        Assert(ReferenceEquals(Throws<InvalidOperationException>(() => settingsStream.Write(frame, 0, frame.Length)), settingsFailure),
            "settings error was swallowed");
        Assert(settingsOutput.Length == 0, "settings error emitted an unmodified initialize");
        settingsStream.Dispose();
    }

    private static void HeaderLimitsAndInvalidInput()
    {
        foreach (var header in new[] {
            "X-Only: missing\r\n\r\n", "Content-Length: -1\r\n\r\n", "Content-Length: nope\r\n\r\n",
            "Content-Length: 67108865\r\n\r\n", "Content-Length: 999999999999999999999\r\n\r\n",
            "Content-Length: 1\r\nContent-Length: 1\r\n\r\n", new string('x', 8193)
        })
        {
            var output = new RecordingStream();
            var stream = Create(output, () => true);
            var input = Encoding.ASCII.GetBytes(header);
            Throws<InvalidDataException>(() => stream.Write(input, 0, input.Length));
            Assert(output.Length == 0, "invalid header was sent downstream");
            Throws<IOException>(() => stream.Write(Frame(Initialize), 0, Frame(Initialize).Length));
            stream.Dispose();
        }

        var baseHeader = "Content-Length: 0\r\nX-Padding: ";
        var boundary = Encoding.ASCII.GetBytes(baseHeader + new string('p', 8192 - baseHeader.Length - 4) + "\r\n\r\n");
        var boundaryOutput = new RecordingStream();
        using (var stream = Create(boundaryOutput, () => true))
        {
            stream.Write(boundary, 0, boundary.Length);
            Assert(boundaryOutput.ToArray().SequenceEqual(boundary), "exact 8 KiB header was rejected or changed");
            Throws<ArgumentNullException>(() => stream.Write(null, 0, 0));
            Throws<ArgumentOutOfRangeException>(() => stream.Write(new byte[2], 1, 2));
            var valid = Frame(Initialize);
            stream.Write(valid, 0, valid.Length);
            AssertCapability(ReadFrames(boundaryOutput.ToArray()).Last(), true);
        }

        foreach (var json in new[] {
            "{\"id\":1,\"method\":\"initialize\",\"params\":[]}",
            "{\"id\":1,\"method\":\"initialize\",\"params\":{\"capabilities\":false}}",
            "{\"id\":1,\"method\":\"initialize\",\"params\":{\"capabilities\":{\"textDocument\":1}}}"
        })
        {
            var output = new RecordingStream();
            var stream = Create(output, () => true);
            var bytes = Frame(json);
            Throws<InvalidDataException>(() => stream.Write(bytes, 0, bytes.Length));
            Assert(output.Length == 0, "invalid initialize shape silently lost params");
            stream.Dispose();
        }

        var maxOutput = new RecordingStream();
        var maxStream = Create(maxOutput, () => true);
        var maxHeader = Encoding.ASCII.GetBytes("Content-Length: 67108864\r\n\r\n");
        maxStream.Write(maxHeader, 0, maxHeader.Length);
        Assert(maxOutput.Length == 0, "maximum-size body was sent before completion");
        Throws<EndOfStreamException>(() => maxStream.Dispose());
        Assert(maxOutput.Disposed, "maximum-size buffered body prevented disposal");
    }

    private static void AssertCapability(string json, bool expected)
    {
        var parameters = JObject.Parse(json)["params"] as JObject;
        var capabilities = parameters?["capabilities"] as JObject;
        var textDocument = capabilities?["textDocument"] as JObject;
        var inactiveRegions = textDocument?["inactiveRegionsCapabilities"] as JObject;
        var value = inactiveRegions?["inactiveRegions"];
        Assert(value != null && value.Type == JTokenType.Boolean && (bool)value == expected, "inactiveRegions capability missing or incorrect");
    }

    private static byte[] Frame(string json, bool extraHeaders = false)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var header = extraHeaders
            ? "Content-Type: application/vscode-jsonrpc; charset=utf-8\r\ncOnTeNt-LeNgTh:\t" + body.Length + "\t \r\nX-Trace: keep  spaces\r\n\r\n"
            : "Content-Length: " + body.Length + "\r\n\r\n";
        return Join(Encoding.ASCII.GetBytes(header), body);
    }

    private static byte[] Join(params byte[][] arrays) => arrays.SelectMany(array => array).ToArray();

    private static List<string> ReadFrames(byte[] data)
    {
        var result = new List<string>();
        var position = 0;
        while (position < data.Length)
        {
            var start = position;
            while (position + 3 < data.Length && !(data[position] == 13 && data[position + 1] == 10 && data[position + 2] == 13 && data[position + 3] == 10))
                position++;
            Assert(position + 3 < data.Length, "output header truncated");
            var header = Encoding.ASCII.GetString(data, start, position - start);
            var lengthLine = header.Split(new[] { "\r\n" }, StringSplitOptions.None)
                .Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
            var length = int.Parse(lengthLine.Substring(lengthLine.IndexOf(':') + 1).Trim(), CultureInfo.InvariantCulture);
            position += 4;
            Assert(length <= data.Length - position, "output Content-Length exceeds UTF-8 body bytes");
            result.Add(new UTF8Encoding(false, true).GetString(data, position, length));
            position += length;
        }
        return result;
    }

    private static T Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T error) { return error; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    private static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T error) { return error; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private class RecordingStream : MemoryStream
    {
        internal int Writes;
        internal bool Disposed;

        public override void Write(byte[] buffer, int offset, int count)
        {
            Writes++;
            base.Write(buffer, offset, count);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(buffer, offset, count);
            return Task.CompletedTask;
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class PausingStream : RecordingStream
    {
        internal readonly TaskCompletionSource<bool> Entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<bool> Release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Entered.TrySetResult(true);
            var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellationToken.Register(() => canceled.TrySetCanceled()))
            {
                var completed = await Task.WhenAny(Release.Task, canceled.Task).ConfigureAwait(false);
                await completed.ConfigureAwait(false);
            }
            await base.WriteAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class FailingStream : RecordingStream
    {
        private readonly IOException _error;
        internal FailingStream(IOException error) { _error = error; }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => throw _error;
    }
}
