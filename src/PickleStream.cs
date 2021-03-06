﻿using System;
using System.IO;

namespace Ibasa.Pikala
{
    /// <summary>
    /// We always wrap streams with this stream which will track offset from where we started in the stream.
    /// That is Position will always work, and will always return 0 to start.
    /// </summary>
    sealed class PickleStream : Stream
    {
        readonly Stream _stream;
        long _position;

        public PickleStream(Stream stream)
        {
            _stream = stream;
            _position = 0;
        }

        public override bool CanRead => _stream.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => _stream.CanWrite;

        public override long Length => throw new NotImplementedException();

        public override long Position { get => _position; set => throw new NotImplementedException(); }

        public override void Flush()
        {
            _stream.Flush();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var result = _stream.Read(buffer, offset, count);
            _position += result;
            return result;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotImplementedException();
        }

        public override void SetLength(long value)
        {
            throw new NotImplementedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _stream.Write(buffer, offset, count);
            _position += count;
        }
    }
}
