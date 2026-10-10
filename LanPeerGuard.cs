using System;

namespace ZompiercerLAN
{
    // Worker-owned. Count only authenticated DTLS plaintext violations: invalid
    // admission MACs, corrupt ciphertext, missing/replayed UDP and full local
    // queues MUST NOT let an outsider revoke an approved peer.
    internal sealed class LanPeerGuard
    {
        private double _windowAt;
        private int _invalid, _rate, _totalInvalid, _totalRate;
        internal string Failure { get; private set; }
#if SECURITY_CHECKS
        internal string CountsForChecks { get { return "invalid="+_totalInvalid+", rate="+_totalRate; } }
#endif
        internal void Invalid(double now)
        {
            Window(now);_invalid++;_totalInvalid++;
            if(_invalid>=8 || _totalInvalid>=32) Fail("Участник повторно присылает недопустимые защищённые сообщения");
        }
        internal void ExcessRate(double now)
        {
            Window(now);_rate++;_totalRate++;
            if(_rate>=64 || _totalRate>=512) Fail("Участник систематически превышает допустимую частоту защищённых сообщений");
        }
        private void Window(double now)
        {
            if(double.IsNaN(now) || double.IsInfinity(now) || now<0) { Fail("Ошибка учёта безопасности соединения");return; }
            if(now-_windowAt>=10) { _windowAt=now;_invalid=0;_rate=0; }
        }
        private void Fail(string reason) { if(Failure==null) Failure=reason; }
    }
    internal sealed class LanReconnectDelay
    {
        private int _failures;
        private double _retryAt;
        internal bool Ready(double now) { return Valid(now) && now>=_retryAt; }
        internal double Failed(double now)
        {
            if(!Valid(now)) throw new ArgumentException("Invalid reconnect time");
            _failures=Math.Min(16,_failures+1);
            double delay=Math.Min(30,.5*Math.Pow(2,_failures-1));
            _retryAt=Math.Max(_retryAt,now+delay);return delay;
        }
        // Call once after ten seconds of an established, live application session.
        internal void Healthy() { _failures=0;_retryAt=0; }
        private static bool Valid(double x) { return !double.IsNaN(x) && !double.IsInfinity(x) && x>=0; }
    }
}
