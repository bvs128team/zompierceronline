using System;
using System.Threading;

namespace ZompiercerLAN
{
    // Public room metadata survives PIN expiry. No Unity APIs or secret verifiers.
    internal sealed class LanRoomAdvertisement : IDisposable
    {
        private readonly LanDiscovery _discovery;
        private readonly byte[] _room;
        private readonly string _name;
        private readonly Thread _worker;
        private volatile bool _stopping;
        private int _flags = 1;
        internal LanRoomAdvertisement(byte[] room, string name)
        {
            _room = (byte[])room.Clone(); _name = LanDiscovery.CleanName(name);
            _discovery = new LanDiscovery(true);
            try
            {
                _worker = new Thread(Run) { IsBackground = true, Name = "Zompiercer LAN rooms" }; _worker.Start();
            }
            catch { _discovery.Dispose(); throw; }
        }
        internal void SetFlags(byte flags) { if (flags <= 2) Volatile.Write(ref _flags, flags); }
        private void Run()
        {
            try
            {
                while (!_stopping) { _discovery.Advertise(_room, (byte)Volatile.Read(ref _flags), _name); Thread.Sleep(10); }
            }
            catch (Exception) { /* Cancellation or a closed adapter stops public discovery only. */ }
            finally { _discovery.Dispose(); }
        }
        public void Dispose()
        {
            if (_stopping) return; _stopping = true; _discovery.Dispose();
            if (Thread.CurrentThread != _worker) _worker.Join(100);
        }
    }

    internal sealed class LanRoomBrowser : IDisposable
    {
        private readonly LanDiscovery _discovery;
        private readonly Thread _worker;
        private volatile bool _stopping, _finished;
        private LanDiscovery.Candidate[] _rooms = new LanDiscovery.Candidate[0];
        private string _status = "Поиск комнат…";
        private LanFault _failure;
        internal LanFault Failure { get { return _failure; } }
        internal bool Finished { get { return _finished; } }
        internal string Status { get { return Volatile.Read(ref _status); } }
        internal LanDiscovery.Candidate[] Rooms { get { return Volatile.Read(ref _rooms); } }
        internal LanRoomBrowser()
        {
            _discovery = new LanDiscovery(false);
            try
            {
                _worker = new Thread(Run) { IsBackground = true, Name = "Zompiercer LAN room search" }; _worker.Start();
            }
            catch { _discovery.Dispose(); throw; }
        }
        private void Run()
        {
            try
            {
                var rooms = _discovery.Find(() => !_stopping, null, true).ToArray();
                Volatile.Write(ref _rooms, rooms);
                Volatile.Write(ref _status, rooms.Length == 0 ? "Комнаты не найдены; обновите список или укажите IP" : "Выберите комнату и введите свежий код хоста");
            }
            catch (Exception ex) { if (!_stopping) { _failure=LanNetworkFailure.From(ex);Volatile.Write(ref _status,LanFaultException.MessageFor(_failure)); } }
            finally { _discovery.Dispose(); _finished = true; }
        }
        public void Dispose()
        {
            if (_stopping) return; _stopping = true; _discovery.Dispose();
            if (Thread.CurrentThread != _worker) _worker.Join(100);
        }
    }
}
