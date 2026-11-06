using System;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;

namespace ClangdVS
{
    internal sealed class LspMessageFilterStream : Stream
    {
        private readonly Stream _inner;
        private readonly Func<JObject, bool> _allowMessage;
        private byte[] _pending = Array.Empty<byte>();
        private int _pendingOffset;

        internal LspMessageFilterStream(Stream inner, Func<JObject, bool> allowMessage)
        {
            _inner = new BufferedStream(inner ?? throw new ArgumentNullException(nameof(inner)), 4096);
            _allowMessage = allowMessage ?? throw new ArgumentNullException(nameof(allowMessage));
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || buffer.Length - offset < count)
                throw new ArgumentOutOfRangeException();
            if (count == 0)
                return 0;

            while (_pendingOffset == _pending.Length)
            {
                if (!TryReadFrame(out var frame))
                    return 0;

                var body = GetBody(frame);
                JObject message;
                try
                {
                    message = JObject.Parse(Encoding.UTF8.GetString(body));
                }
                catch
                {
                    message = null;
                }

                if (message != null && !_allowMessage(message))
                    continue;
                _pending = frame;
                _pendingOffset = 0;
            }

            var copied = Math.Min(count, _pending.Length - _pendingOffset);
            Buffer.BlockCopy(_pending, _pendingOffset, buffer, offset, copied);
            _pendingOffset += copied;
            return copied;
        }

        private bool TryReadFrame(out byte[] frame)
        {
            frame = null;
            using (var header = new MemoryStream())
            {
                var matched = 0;
                while (matched < 4)
                {
                    var value = _inner.ReadByte();
                    if (value < 0)
                    {
                        if (header.Length == 0)
                            return false;
                        throw new EndOfStreamException("Incomplete LSP message header.");
                    }
                    header.WriteByte((byte)value);
                    if (value == new[] { 13, 10, 13, 10 }[matched])
                        matched++;
                    else
                        matched = value == 13 ? 1 : 0;
                    if (header.Length > 8192)
                        throw new InvalidDataException("LSP message header exceeded 8192 bytes.");
                }

                var headerBytes = header.ToArray();
                var text = Encoding.ASCII.GetString(headerBytes);
                var contentLength = -1;
                foreach (var line in text.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var separator = line.IndexOf(':');
                    if (separator > 0 && line.Substring(0, separator).Trim()
                                          .Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!int.TryParse(line.Substring(separator + 1).Trim(), NumberStyles.None,
                                           CultureInfo.InvariantCulture, out contentLength) || contentLength < 0)
                            throw new InvalidDataException("Invalid LSP Content-Length header.");
                    }
                }
                if (contentLength < 0 || contentLength > 64 * 1024 * 1024)
                    throw new InvalidDataException("Missing or oversized LSP Content-Length header.");

                var body = new byte[contentLength];
                ReadExactly(_inner, body);
                frame = new byte[headerBytes.Length + body.Length];
                Buffer.BlockCopy(headerBytes, 0, frame, 0, headerBytes.Length);
                Buffer.BlockCopy(body, 0, frame, headerBytes.Length, body.Length);
                return true;
            }
        }

        private static void ReadExactly(Stream stream, byte[] bytes)
        {
            var offset = 0;
            while (offset < bytes.Length)
            {
                var count = stream.Read(bytes, offset, bytes.Length - offset);
                if (count == 0)
                    throw new EndOfStreamException("Incomplete LSP message body.");
                offset += count;
            }
        }

        private static byte[] GetBody(byte[] frame)
        {
            for (var i = 0; i < frame.Length - 3; i++)
            {
                if (frame[i] == 13 && frame[i + 1] == 10 && frame[i + 2] == 13 && frame[i + 3] == 10)
                {
                    var start = i + 4;
                    var body = new byte[frame.Length - start];
                    Buffer.BlockCopy(frame, start, body, 0, body.Length);
                    return body;
                }
            }
            throw new InvalidDataException("LSP message header terminator was missing.");
        }

        public override void Flush() => _inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
