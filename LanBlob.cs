using System;

namespace ZompiercerLAN
{
    // Bounded chunked transfer of one opaque blob over the existing DTLS packets
    // (guest state, guest restore). The sender cycles chunks of the newest revision
    // until the receiver acknowledges that revision; the receiver assembles one
    // revision at a time. No Unity APIs; callers own all game-side validation.
    internal sealed class LanBlobSender
    {
        private byte[] _data;
        private int _revision, _acked, _next;
        private double _nextCycle;
        internal int Revision { get { return _revision; } }
        internal bool Done { get { return _data == null || _acked == _revision; } }

        // Starts a new revision; an unacknowledged older one is abandoned.
        internal int Start(byte[] data)
        {
            if (data == null || data.Length < 1 || data.Length > LanProtocol.MaxGuestBytes) throw new ArgumentException("Blob size");
            _data = (byte[])data.Clone(); _revision++; _next = 0; _nextCycle = 0;
            return _revision;
        }
        internal void Acknowledge(int revision) { if (revision == _revision) _acked = revision; }
        internal void Cancel() { _data = null; }

        // Paced: at most `perSecond` chunks per second (the receiver's per-kind limit
        // is higher); after a full cycle without an ack it repeats after `retry` s.
        internal void Tick(double now, double perSecond, double retry, Action<Packet> send, PacketKind kind)
        {
            if (Done || now < _nextCycle) return;
            int count = Chunks(_data.Length);
            if (_nextChunk < now - 1) _nextChunk = now; // no burst after an idle period
            for (int i = 0; i < 8 && _next < count && now >= _nextChunk; i++, _next++)
            {
                int start = _next * LanProtocol.ChunkSize;
                var chunk = new byte[Math.Min(LanProtocol.ChunkSize, _data.Length - start)];
                Buffer.BlockCopy(_data, start, chunk, 0, chunk.Length);
                send(new Packet { Kind = kind, Revision = _revision, ChunkIndex = _next, ChunkCount = count, Chunk = chunk });
                _nextChunk += 1 / perSecond;
            }
            if (_next == count) { _next = 0; _nextCycle = now + retry; }
        }
        private double _nextChunk;
        internal static int Chunks(int bytes) { return (bytes + LanProtocol.ChunkSize - 1) / LanProtocol.ChunkSize; }
    }

    internal sealed class LanBlobReceiver
    {
        private byte[][] _chunks;
        private int _revision, _received, _completed;
        internal int Completed { get { return _completed; } }

        // Returns the complete blob once, when its last chunk arrives; otherwise null.
        // A packet of an older or already completed revision only needs a repeated ack.
        internal byte[] Accept(Packet p, out bool ack)
        {
            ack = false;
            if (p.Revision <= 0 || p.ChunkCount < 1 || p.ChunkCount > LanProtocol.MaxGuestChunks || p.ChunkIndex < 0 || p.ChunkIndex >= p.ChunkCount || p.Chunk == null)
                return null;
            if (p.Revision <= _completed) { ack = p.Revision == _completed; return null; }
            if (p.Revision < _revision) return null;
            if (p.Revision != _revision || _chunks == null || _chunks.Length != p.ChunkCount)
            {
                _revision = p.Revision; _chunks = new byte[p.ChunkCount][]; _received = 0;
            }
            if (_chunks[p.ChunkIndex] != null) return null;
            bool last = p.ChunkIndex == p.ChunkCount - 1;
            if (!last && p.Chunk.Length != LanProtocol.ChunkSize || p.Chunk.Length < 1 || p.Chunk.Length > LanProtocol.ChunkSize) return null;
            _chunks[p.ChunkIndex] = (byte[])p.Chunk.Clone();
            if (++_received != _chunks.Length) return null;
            int total = 0;
            foreach (var c in _chunks) total += c.Length;
            if (total > LanProtocol.MaxGuestBytes) { _chunks = null; return null; }
            var data = new byte[total]; int offset = 0;
            foreach (var c in _chunks) { Buffer.BlockCopy(c, 0, data, offset, c.Length); offset += c.Length; }
            _chunks = null; _completed = _revision; ack = true;
            return data;
        }
        internal void Reset() { _chunks = null; _revision = 0; _received = 0; _completed = 0; }
    }
}
