using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClangdVS
{
    /// <summary>
    /// Write-only client-to-server LSP transport. Only complete initialize requests are rewritten.
    /// Flush never forwards a partial frame; disposing with a partial frame reports truncation.
    /// </summary>
    internal sealed class LspInitializeStream : Stream
    {
        private const int MaxHeaderLength = 8192;
        private const int MaxBodyLength = 64 * 1024 * 1024;
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
        private readonly Stream _inner;
        private readonly Func<bool> _inactiveRegionsEnabled;
        // One gate covers framing, downstream writes, flush and disposal. Async callers wait
        // asynchronously; no Task.Run, worker thread, or Stream's separate async lock is needed.
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly byte[] _header = new byte[MaxHeaderLength];
        private int _headerCount;
        private byte[] _body;
        private int _bodyCount;
        private int _lengthStart;
        private int _lengthEnd;
        private Exception _failure;
        private volatile bool _disposed;

        internal LspInitializeStream(Stream inner, Func<bool> inactiveRegionsEnabled)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _inactiveRegionsEnabled = inactiveRegionsEnabled ?? throw new ArgumentNullException(nameof(inactiveRegionsEnabled));
            if (!inner.CanWrite)
                throw new ArgumentException("The inner stream must be writable.", nameof(inner));
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => !_disposed && _inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            ValidateBuffer(buffer, offset, count);
            _gate.Wait();
            try
            {
                CheckState();
                try
                {
                    while (count > 0)
                    {
                        if (!Accept(buffer, ref offset, ref count))
                            continue;
                        var header = PrepareFrame(out var headerCount, out var body);
                        _inner.Write(header, 0, headerCount);
                        if (body.Length != 0)
                            _inner.Write(body, 0, body.Length);
                        ResetFrame();
                    }
                }
                catch (Exception error)
                {
                    Fault(error);
                    throw;
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ValidateBuffer(buffer, offset, count);
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                CheckState();
                // Cancellation before consuming this call leaves any earlier partial frame intact.
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    while (count > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!Accept(buffer, ref offset, ref count))
                            continue;
                        var header = PrepareFrame(out var headerCount, out var body);
                        await _inner.WriteAsync(header, 0, headerCount, cancellationToken).ConfigureAwait(false);
                        if (body.Length != 0)
                            await _inner.WriteAsync(body, 0, body.Length, cancellationToken).ConfigureAwait(false);
                        ResetFrame();
                    }
                }
                catch (Exception error)
                {
                    // A downstream write (including a canceled write) may already have emitted
                    // bytes. Retrying on this transport could corrupt framing or duplicate a request.
                    Fault(error);
                    throw;
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        // Consume at most one frame. Header scanning is in-memory; bodies are copied in blocks,
        // and the inner stream sees at most two writes per frame, irrespective of input splits.
        private bool Accept(byte[] buffer, ref int offset, ref int count)
        {
            while (_body == null && count > 0)
            {
                if (_headerCount == MaxHeaderLength)
                    throw new InvalidDataException("LSP message header exceeded 8192 bytes.");
                _header[_headerCount++] = buffer[offset++];
                count--;
                if (_headerCount >= 4 && _header[_headerCount - 4] == 13 && _header[_headerCount - 3] == 10 &&
                    _header[_headerCount - 2] == 13 && _header[_headerCount - 1] == 10)
                {
                    _body = new byte[ParseContentLength()];
                }
                else if (_headerCount == MaxHeaderLength)
                {
                    throw new InvalidDataException("LSP message header exceeded 8192 bytes.");
                }
            }

            if (_body == null)
                return false;
            var copied = Math.Min(count, _body.Length - _bodyCount);
            Buffer.BlockCopy(buffer, offset, _body, _bodyCount, copied);
            _bodyCount += copied;
            offset += copied;
            count -= copied;
            return _bodyCount == _body.Length;
        }

        private int ParseContentLength()
        {
            var text = Encoding.ASCII.GetString(_header, 0, _headerCount);
            var contentLength = -1;
            var start = 0;
            while (start < text.Length - 2)
            {
                var end = text.IndexOf("\r\n", start, StringComparison.Ordinal);
                var separator = text.IndexOf(':', start, end - start);
                if (separator <= start)
                    throw new InvalidDataException("Invalid LSP message header.");
                if (text.Substring(start, separator - start).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    if (contentLength >= 0)
                        throw new InvalidDataException("Duplicate LSP Content-Length header.");
                    _lengthStart = separator + 1;
                    _lengthEnd = end;
                    while (_lengthStart < _lengthEnd && IsHeaderSpace(text[_lengthStart]))
                        _lengthStart++;
                    while (_lengthEnd > _lengthStart && IsHeaderSpace(text[_lengthEnd - 1]))
                        _lengthEnd--;
                    if (!int.TryParse(text.Substring(_lengthStart, _lengthEnd - _lengthStart), NumberStyles.None,
                        CultureInfo.InvariantCulture, out contentLength) || contentLength < 0 || contentLength > MaxBodyLength)
                        throw new InvalidDataException("Invalid or oversized LSP Content-Length header.");
                }
                start = end + 2;
            }
            if (contentLength < 0)
                throw new InvalidDataException("Missing LSP Content-Length header.");
            return contentLength;
        }

        private static bool IsHeaderSpace(char value) => value == ' ' || value == '\t';

        private byte[] PrepareFrame(out int headerCount, out byte[] body)
        {
            headerCount = _headerCount;
            body = _body;
            var request = ParseObject(body);
            var id = request?["id"];
            if (request == null || request["method"]?.Type != JTokenType.String ||
                (string)request["method"] != "initialize" || id == null ||
                (id.Type != JTokenType.String && id.Type != JTokenType.Integer && id.Type != JTokenType.Float &&
                 id.Type != JTokenType.Null) ||
                request.Property("result") != null || request.Property("error") != null)
                return _header;

            var parameters = GetOrCreateObject(request, "params");
            var capabilities = GetOrCreateObject(parameters, "capabilities");
            var textDocument = GetOrCreateObject(capabilities, "textDocument");
            var inactiveRegions = GetOrCreateObject(textDocument, "inactiveRegionsCapabilities");
            var enabled = _inactiveRegionsEnabled();
            if (inactiveRegions["inactiveRegions"]?.Type == JTokenType.Boolean &&
                (bool)inactiveRegions["inactiveRegions"] == enabled)
                return _header;
            inactiveRegions["inactiveRegions"] = enabled;

            var json = request.ToString(Formatting.None);
            var length = Utf8.GetByteCount(json);
            if (length > MaxBodyLength)
                throw new InvalidDataException("Rewritten LSP body exceeded 64 MiB.");
            body = Utf8.GetBytes(json);

            // Replace only the length's digits. Preserve all other header bytes, including casing,
            // whitespace, order, Content-Type and extension headers.
            var digits = Encoding.ASCII.GetBytes(body.Length.ToString(CultureInfo.InvariantCulture));
            headerCount = _headerCount - (_lengthEnd - _lengthStart) + digits.Length;
            if (headerCount > MaxHeaderLength)
                throw new InvalidDataException("Rewritten LSP header exceeded 8192 bytes.");
            var header = new byte[headerCount];
            Buffer.BlockCopy(_header, 0, header, 0, _lengthStart);
            Buffer.BlockCopy(digits, 0, header, _lengthStart, digits.Length);
            Buffer.BlockCopy(_header, _lengthEnd, header, _lengthStart + digits.Length, _headerCount - _lengthEnd);
            return header;
        }

        private static JObject ParseObject(byte[] body)
        {
            // Invalid JSON/UTF-8 is not ours to repair: forward the original bytes so the peer can
            // report a protocol error. Only JSON decoding errors are handled here; IO, settings and
            // rewrite failures propagate and fault the transport.
            try
            {
                using (var text = new StringReader(Utf8.GetString(body)))
                using (var reader = new JsonTextReader(text) { DateParseHandling = DateParseHandling.None })
                {
                    var token = JToken.ReadFrom(reader);
                    if (reader.Read())
                        return null;
                    return token as JObject;
                }
            }
            catch (JsonException)
            {
                return null;
            }
            catch (DecoderFallbackException)
            {
                return null;
            }
        }

        private static JObject GetOrCreateObject(JObject parent, string name)
        {
            var value = parent[name];
            if (value == null || value.Type == JTokenType.Null)
            {
                var created = new JObject();
                parent[name] = created;
                return created;
            }
            if (value is JObject existing)
                return existing;
            throw new InvalidDataException("initialize " + name + " must be a JSON object.");
        }

        public override void Flush()
        {
            _gate.Wait();
            try
            {
                CheckState();
                try { _inner.Flush(); }
                catch (Exception error) { Fault(error); throw; }
            }
            finally
            {
                _gate.Release();
            }
        }

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                CheckState();
                cancellationToken.ThrowIfCancellationRequested();
                try { await _inner.FlushAsync(cancellationToken).ConfigureAwait(false); }
                catch (Exception error) { Fault(error); throw; }
            }
            finally
            {
                _gate.Release();
            }
        }

        private static void ValidateBuffer(byte[] buffer, int offset, int count)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset > buffer.Length - count)
                throw new ArgumentOutOfRangeException();
        }

        private void CheckState()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(LspInitializeStream));
            if (_failure != null)
                throw new IOException("The LSP write transport is faulted; create a new transport before retrying.", _failure);
        }

        private void ResetFrame()
        {
            _headerCount = 0;
            _body = null;
            _bodyCount = 0;
        }

        private void Fault(Exception error)
        {
            _failure = error;
            ResetFrame();
        }

        protected override void Dispose(bool disposing)
        {
            if (!disposing)
            {
                base.Dispose(false);
                return;
            }
            _gate.Wait();
            try
            {
                if (_disposed)
                    return;
                _disposed = true;
                var truncated = _failure == null && _headerCount != 0;
                ResetFrame();
                try
                {
                    _inner.Dispose();
                }
                finally
                {
                    base.Dispose(true);
                }
                if (truncated)
                    throw new EndOfStreamException("Disposed with an incomplete LSP message header or body.");
            }
            finally
            {
                // Do not dispose the gate: callers already waiting must wake up and observe disposal.
                _gate.Release();
            }
        }
    }
}
