using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ZompiercerLAN
{
    // Local, per-Windows-user guest identity: a random 32-byte secret protected with
    // DPAPI (CurrentUser). A host never sees the secret, only a token derived for its
    // own world, so a host cannot use what it learned to claim this player's profile
    // in another host's world. Host-side profiles are keyed by SHA-256 of the token.
    // (2026-10-03: RSA proofs from the first draft replaced by HMAC tokens; the
    // channel is already DTLS-authenticated and Unity Mono RSA/CSP behaviour differs.)
    internal sealed class LanPlayerIdentity : IDisposable
    {
        private const string FileName = "player-identity.dpapi";
        private const int SecretBytes = 32, MaxProtectedBytes = 4096;
        private static readonly byte[] Header = Encoding.ASCII.GetBytes("ZLANID02");
        private static readonly byte[] Label = Encoding.ASCII.GetBytes("ZompiercerLAN guest profile v1\0");
        private readonly object gate = new object();
        private byte[] secret;

        private LanPlayerIdentity(byte[] value) { secret = value; }

        // Default location: outside the game folder and the disposable LAN-sessions.
        internal static string DefaultDirectory
        { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZompiercerLAN"); } }

        internal byte[] WorldToken(Guid worldId)
        {
            if (worldId == Guid.Empty) throw new ArgumentException("World id is required.");
            var message = new byte[Label.Length + 16];
            Buffer.BlockCopy(Label, 0, message, 0, Label.Length);
            Buffer.BlockCopy(worldId.ToByteArray(), 0, message, Label.Length, 16);
            lock (gate)
            {
                if (secret == null) throw new ObjectDisposedException("LanPlayerIdentity");
                using (var hmac = new HMACSHA256(secret)) return hmac.ComputeHash(message);
            }
        }


        internal static LanPlayerIdentity LoadOrCreate(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("Identity directory is required.", "directory");
            string root = Path.GetFullPath(directory);
            RejectLinks(root);
            Directory.CreateDirectory(root);
            RejectLinks(root);
            string target = Path.Combine(root, FileName);
            RejectLinks(target);
            if (File.Exists(target)) return Read(target);

            string temporary = Path.Combine(root, ".player-identity-" + Guid.NewGuid().ToString("N") + ".tmp");
            bool temporaryCreated = false;
            byte[] value = new byte[SecretBytes];
            try
            {
                using (var random = RandomNumberGenerator.Create()) random.GetBytes(value);
                byte[] encrypted = ProtectedData.Protect(value, Header, DataProtectionScope.CurrentUser);
                if (encrypted.Length > MaxProtectedBytes) throw new CryptographicException("Identity storage limit exceeded.");
                RejectLinks(temporary);
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    temporaryCreated = true;
                    using (var writer = new BinaryWriter(output, Encoding.UTF8, true))
                    {
                        writer.Write(Header);
                        writer.Write(encrypted.Length);
                        writer.Write(encrypted);
                        writer.Flush();
                        output.Flush(true);
                    }
                }
                RejectLinks(target);
                RejectLinks(temporary);
                try
                {
                    // Same-directory rename publishes only a complete file and never replaces
                    // an existing identity. Concurrent creators load the winning identity.
                    File.Move(temporary, target);
                    temporaryCreated = false;
                }
                catch (IOException)
                {
                    RejectLinks(target);
                    if (!File.Exists(target)) throw;
                    return Read(target);
                }
                var result = new LanPlayerIdentity(value);
                value = null;
                return result;
            }
            finally
            {
                if (value != null) Array.Clear(value, 0, value.Length);
                if (temporaryCreated)
                {
                    RejectLinks(temporary);
                    File.Delete(temporary);
                }
            }
        }

        // A damaged file fails closed: it is never silently replaced, so a player
        // does not lose access to profiles because of a transient read problem.
        private static LanPlayerIdentity Read(string path)
        {
            RejectLinks(path);
            byte[] value = null;
            try
            {
                byte[] encrypted;
                using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var reader = new BinaryReader(input))
                {
                    if (input.Length < Header.Length + 5 || input.Length > Header.Length + 4 + MaxProtectedBytes)
                        throw new InvalidDataException("Invalid player identity file size.");
                    foreach (byte expected in Header)
                        if (reader.ReadByte() != expected) throw new InvalidDataException("Unsupported player identity file.");
                    int length = reader.ReadInt32();
                    if (length <= 0 || length > MaxProtectedBytes || input.Length != Header.Length + 4L + length)
                        throw new InvalidDataException("Invalid player identity payload size.");
                    encrypted = reader.ReadBytes(length);
                    if (encrypted.Length != length) throw new InvalidDataException("Truncated player identity file.");
                }
                value = ProtectedData.Unprotect(encrypted, Header, DataProtectionScope.CurrentUser);
                if (value.Length != SecretBytes) throw new InvalidDataException("Invalid player identity secret size.");
                var result = new LanPlayerIdentity(value);
                value = null;
                return result;
            }
            finally { if (value != null) Array.Clear(value, 0, value.Length); }
        }

        private static void RejectLinks(string path)
        {
            string current = Path.GetFullPath(path);
            while (!string.IsNullOrEmpty(current))
            {
                try
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Player identity path contains a reparse point.");
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                current = Path.GetDirectoryName(current);
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (secret != null) { Array.Clear(secret, 0, secret.Length); secret = null; }
            }
        }
    }
}
