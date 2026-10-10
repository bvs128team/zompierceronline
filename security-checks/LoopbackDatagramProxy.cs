using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace ZompiercerLAN
{
    // Test traffic is restricted to 127.0.0.1, fixed host port, and one client.
    // The proxy knows the test admission key, so AEAD corruption is exercised
    // AFTER admission filtering. It cannot decrypt or forge valid DTLS records.
    internal sealed class LoopbackDatagramProxy : IDisposable
    {
        private readonly Socket _socket;
        private readonly IPEndPoint _host;
        private readonly byte[] _admissionKey;
        private readonly object _gate=new object();
        private sealed class Record { internal byte[] Bytes;internal IPEndPoint Target; }
        private readonly List<Record> _records=new List<Record>();
        private readonly Thread _worker;
        private volatile bool _stopping,_tamper,_tamperHost,_capture;
        private volatile int _dropEvery;
        private int _applicationCount;
        private IPEndPoint _client;
        private Exception _failure;
        internal int Port { get; private set; }
        internal LoopbackDatagramProxy(int hostPort,byte[] secret)
        {
            _host=new IPEndPoint(IPAddress.Loopback,hostPort);
            using(var h=new HMACSHA256(secret)) _admissionKey=h.ComputeHash(Encoding.ASCII.GetBytes("ZompiercerLAN paired DTLS admission v3"));
            _socket=new Socket(AddressFamily.InterNetwork,SocketType.Dgram,ProtocolType.Udp);
            _socket.Bind(new IPEndPoint(IPAddress.Loopback,0));_socket.Blocking=false;Port=((IPEndPoint)_socket.LocalEndPoint).Port;
            _worker=new Thread(Run) {IsBackground=true,Name="Loopback DTLS test proxy"};_worker.Start();
        }
        internal void Tamper(bool on,bool both=false) { _tamperHost=on && both;_tamper=on; }
        internal void Capture(bool on) { _capture=on; }
        internal void DropEvery(int count) { _dropEvery=count; }
        private void Run()
        {
            var buffer=new byte[1301];
            try {
                while(!_stopping) {
                    if(!_socket.Poll(1000,SelectMode.SelectRead)) { Thread.Sleep(1);continue; }
                    EndPoint source=new IPEndPoint(IPAddress.Loopback,0);int size;
                    try { size=_socket.ReceiveFrom(buffer,ref source); }
                    catch(SocketException ex) { if(ex.SocketErrorCode==SocketError.WouldBlock)continue;throw; }
                    var from=(IPEndPoint)source;if(!from.Address.Equals(IPAddress.Loopback))continue;
                    bool fromHost=from.Equals(_host);IPEndPoint target;
                    lock(_gate) {
                        if(fromHost)target=_client;
                        else { if(_client==null)_client=from;if(!_client.Equals(from))continue;target=_host; }
                    }
                    if(target==null)continue;
                    var bytes=new byte[size];Buffer.BlockCopy(buffer,0,bytes,0,size);
                    bool application=size>6+13+32 && bytes[6]==23;
                    if(application && _capture) lock(_gate) { if(_records.Count<32)_records.Add(new Record {Bytes=(byte[])bytes.Clone(),Target=target}); }
                    int drop=_dropEvery;
                    if(application && drop>0 && ++_applicationCount%drop==0)continue;
                    if(application && (fromHost?_tamperHost:_tamper)) {
                        bytes[size-33]^=1;
                        using(var h=new HMACSHA256(_admissionKey)) { var tag=h.ComputeHash(bytes,0,size-32);Buffer.BlockCopy(tag,0,bytes,size-32,32); }
                    }
                    _socket.SendTo(bytes,target);
                }
            } catch(Exception ex) { if(!_stopping)_failure=ex; }
        }
        internal int Replay()
        {
            lock(_gate) { foreach(var record in _records)_socket.SendTo(record.Bytes,record.Target);return _records.Count; }
        }
        internal void InvalidBurst(bool both=false,int count=512)
        {
            if(count<1 || count>512)throw new ArgumentOutOfRangeException("count");
            var bytes=new byte[1301];new Random(771).NextBytes(bytes);
            // Less than 1 MiB, no broadcast or non-loopback address.
            IPEndPoint client;lock(_gate)client=_client;
            for(int i=0;i<count;i++) { _socket.SendTo(bytes,_host);if(both && client!=null)_socket.SendTo(bytes,client); }
        }
        public void Dispose()
        {
            _stopping=true;_socket.Dispose();_worker.Join(1000);Array.Clear(_admissionKey,0,_admissionKey.Length);
            if(_failure!=null)throw new Exception("Loopback proxy failed",_failure);
        }
    }
}
