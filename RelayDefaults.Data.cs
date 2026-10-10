// Builds from the public source have no built-in relay: the settings of the official
// releases are not published, and neither is the relay server itself, so these builds
// play over the local network only.
namespace ZompiercerLAN
{
    internal static partial class RelayDefaults
    {
        private const int PortValue = 27780;
        private static readonly byte[] Mask = { 0 };
        private static readonly byte[] ServerData = new byte[0];
        private static readonly byte[] FingerprintData = new byte[0];
        private static readonly byte[] AccessKeyData = new byte[0];
    }
}
