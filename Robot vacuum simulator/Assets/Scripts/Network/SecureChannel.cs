using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace RobotVacuum.Network
{
    /// <summary>The other side's message failed authentication: wrong key, tampering, replay or reordering.</summary>
    public sealed class ChannelAuthenticationException : IOException
    {
        public ChannelAuthenticationException(string message) : base(message) { }
    }

    /// <summary>
    /// Encrypted, authenticated messages over one TCP connection (requirement 4.8 and C.12 for the worker link).
    ///
    /// Both sides hold a 32-byte secret: the long-term key made at pairing, or, during pairing itself, a key derived
    /// from the pairing code with PBKDF2. Neither is ever sent. Each connection mixes the secret with two fresh random
    /// nonces (one from each side, sent in the handshake) into four keys, one encryption and one MAC key per direction,
    /// so every connection has its own keys and a message can't be reflected back to its sender.
    ///
    /// Each message is AES-256-CBC with a random IV, then HMAC-SHA256 over a message counter, the IV and the
    /// ciphertext (encrypt-then-MAC). The counter is not sent: the receiver expects the next number, so a replayed,
    /// dropped or reordered message fails the MAC and the connection is closed. Standard primitives only, from .NET.
    /// </summary>
    public sealed class SecureChannel
    {
        const int IvBytes = 16;
        const int MacBytes = 32;
        public const int NonceBytes = 32;
        public const int KeyBytes = 32;

        /// <summary>Characters of a pairing code: no 0/O, 1/I/L, so it can be read aloud and typed without mix-ups.</summary>
        public const string PairingAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
        public const int PairingCodeLength = 10; // 31^10, about 49 bits
        public const int PairingIterations = 100_000;
        static readonly byte[] PairingSalt = Encoding.ASCII.GetBytes("robot-vacuum-sim/pair/v1");

        readonly Stream stream;
        readonly byte[] sendEncKey, sendMacKey, receiveEncKey, receiveMacKey;
        long sendCounter, receiveCounter;

        SecureChannel(Stream stream, byte[] secret, byte[] clientNonce, byte[] serverNonce, bool isClient)
        {
            if (secret == null || secret.Length < 16) throw new ArgumentException("The channel secret is missing or too short.");
            if (clientNonce == null || clientNonce.Length != NonceBytes || serverNonce == null || serverNonce.Length != NonceBytes)
                throw new ArgumentException("Both nonces must be 32 bytes.");

            this.stream = stream;
            byte[] c2sEnc = Derive(secret, "c2s-enc", clientNonce, serverNonce);
            byte[] c2sMac = Derive(secret, "c2s-mac", clientNonce, serverNonce);
            byte[] s2cEnc = Derive(secret, "s2c-enc", clientNonce, serverNonce);
            byte[] s2cMac = Derive(secret, "s2c-mac", clientNonce, serverNonce);
            sendEncKey = isClient ? c2sEnc : s2cEnc;
            sendMacKey = isClient ? c2sMac : s2cMac;
            receiveEncKey = isClient ? s2cEnc : c2sEnc;
            receiveMacKey = isClient ? s2cMac : c2sMac;
        }

        public static SecureChannel ForClient(Stream stream, byte[] secret, byte[] clientNonce, byte[] serverNonce) =>
            new SecureChannel(stream, secret, clientNonce, serverNonce, true);

        public static SecureChannel ForServer(Stream stream, byte[] secret, byte[] clientNonce, byte[] serverNonce) =>
            new SecureChannel(stream, secret, clientNonce, serverNonce, false);

        // ---------------------------------------------------------------- sending and receiving

        public void Send(string type, object body, byte[] payload = null)
        {
            var envelope = new Envelope { type = type, body = body != null ? JsonUtility.ToJson(body) : "{}" };
            byte[] header = WorkerProtocol.Utf8(JsonUtility.ToJson(envelope));
            payload = payload ?? Array.Empty<byte>();

            var plain = new byte[4 + header.Length + payload.Length];
            WorkerProtocol.WriteInt32(plain, 0, header.Length);
            Buffer.BlockCopy(header, 0, plain, 4, header.Length);
            Buffer.BlockCopy(payload, 0, plain, 4 + header.Length, payload.Length);

            WorkerProtocol.WriteFrame(stream, Seal(plain));
        }

        /// <summary>The next message, or null if the other side closed the connection. Throws <see cref="ChannelAuthenticationException"/> on a bad message.</summary>
        public Envelope Receive(out byte[] payload)
        {
            payload = null;
            byte[] frame = WorkerProtocol.ReadFrame(stream);
            if (frame == null) return null;

            byte[] plain = Open(frame);
            if (plain.Length < 4) throw new ChannelAuthenticationException("A message was too short.");
            int headerLength = WorkerProtocol.ReadInt32(plain, 0);
            if (headerLength < 0 || headerLength > plain.Length - 4) throw new ChannelAuthenticationException("A message header was malformed.");

            var envelope = JsonUtility.FromJson<Envelope>(Encoding.UTF8.GetString(plain, 4, headerLength));
            if (envelope == null || string.IsNullOrEmpty(envelope.type)) throw new InvalidDataException("A message had no type.");
            payload = new byte[plain.Length - 4 - headerLength];
            Buffer.BlockCopy(plain, 4 + headerLength, payload, 0, payload.Length);
            return envelope;
        }

        /// <summary>Encrypts and authenticates one message (exposed for tests).</summary>
        internal byte[] Seal(byte[] plain)
        {
            byte[] iv = RandomBytes(IvBytes);
            byte[] cipher;
            using (var aes = NewAes(sendEncKey, iv))
            using (var encryptor = aes.CreateEncryptor())
                cipher = encryptor.TransformFinalBlock(plain, 0, plain.Length);

            byte[] mac = Mac(sendMacKey, sendCounter, iv, cipher, 0, cipher.Length);
            sendCounter++;

            var frame = new byte[IvBytes + cipher.Length + MacBytes];
            Buffer.BlockCopy(iv, 0, frame, 0, IvBytes);
            Buffer.BlockCopy(cipher, 0, frame, IvBytes, cipher.Length);
            Buffer.BlockCopy(mac, 0, frame, IvBytes + cipher.Length, MacBytes);
            return frame;
        }

        /// <summary>Checks and decrypts one message (exposed for tests).</summary>
        internal byte[] Open(byte[] frame)
        {
            if (frame.Length < IvBytes + 16 + MacBytes || (frame.Length - IvBytes - MacBytes) % 16 != 0)
                throw new ChannelAuthenticationException("A message had the wrong size.");

            int cipherLength = frame.Length - IvBytes - MacBytes;
            var iv = new byte[IvBytes];
            Buffer.BlockCopy(frame, 0, iv, 0, IvBytes);
            byte[] expected = Mac(receiveMacKey, receiveCounter, iv, frame, IvBytes, cipherLength);
            if (!FixedTimeEquals(expected, frame, IvBytes + cipherLength))
                throw new ChannelAuthenticationException("A message failed authentication (wrong key, or it was changed, replayed or reordered).");
            receiveCounter++;

            using (var aes = NewAes(receiveEncKey, iv))
            using (var decryptor = aes.CreateDecryptor())
                return decryptor.TransformFinalBlock(frame, IvBytes, cipherLength);
        }

        // ---------------------------------------------------------------- primitives

        static Aes NewAes(byte[] key, byte[] iv)
        {
            var aes = Aes.Create();
            aes.KeySize = 256;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.Key = key;
            aes.IV = iv;
            return aes;
        }

        static byte[] Mac(byte[] key, long counter, byte[] iv, byte[] data, int offset, int count)
        {
            var counterBytes = new byte[8];
            for (int i = 0; i < 8; i++) counterBytes[i] = (byte)(counter >> (56 - 8 * i));
            using (var hmac = new HMACSHA256(key))
            {
                hmac.TransformBlock(counterBytes, 0, 8, null, 0);
                hmac.TransformBlock(iv, 0, iv.Length, null, 0);
                hmac.TransformFinalBlock(data, offset, count);
                return hmac.Hash;
            }
        }

        static bool FixedTimeEquals(byte[] expected, byte[] buffer, int offset)
        {
            int difference = 0;
            for (int i = 0; i < expected.Length; i++) difference |= expected[i] ^ buffer[offset + i];
            return difference == 0;
        }

        static byte[] Derive(byte[] secret, string label, byte[] clientNonce, byte[] serverNonce)
        {
            byte[] labelBytes = Encoding.ASCII.GetBytes(label);
            var data = new byte[labelBytes.Length + 1 + clientNonce.Length + serverNonce.Length];
            Buffer.BlockCopy(labelBytes, 0, data, 0, labelBytes.Length);
            Buffer.BlockCopy(clientNonce, 0, data, labelBytes.Length + 1, clientNonce.Length);
            Buffer.BlockCopy(serverNonce, 0, data, labelBytes.Length + 1 + clientNonce.Length, serverNonce.Length);
            using (var hmac = new HMACSHA256(secret)) return hmac.ComputeHash(data);
        }

        public static byte[] RandomBytes(int count)
        {
            var bytes = new byte[count];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            return bytes;
        }

        /// <summary>PBKDF2 with HMAC-SHA256 (RFC 8018), written out because Mono's Rfc2898DeriveBytes may only offer SHA-1.</summary>
        public static byte[] Pbkdf2(byte[] password, byte[] salt, int iterations, int length)
        {
            if (iterations < 1) throw new ArgumentOutOfRangeException(nameof(iterations));
            var output = new byte[length];
            using (var hmac = new HMACSHA256(password))
            {
                int blocks = (length + 31) / 32;
                for (int block = 1; block <= blocks; block++)
                {
                    var first = new byte[salt.Length + 4];
                    Buffer.BlockCopy(salt, 0, first, 0, salt.Length);
                    WorkerProtocol.WriteInt32(first, salt.Length, block);
                    byte[] u = hmac.ComputeHash(first);
                    var t = (byte[])u.Clone();
                    for (int i = 1; i < iterations; i++)
                    {
                        u = hmac.ComputeHash(u);
                        for (int j = 0; j < t.Length; j++) t[j] ^= u[j];
                    }
                    int offset = (block - 1) * 32;
                    Buffer.BlockCopy(t, 0, output, offset, Math.Min(32, length - offset));
                }
            }
            return output;
        }

        // ---------------------------------------------------------------- pairing codes

        /// <summary>A fresh random pairing code, unformatted (10 characters of <see cref="PairingAlphabet"/>).</summary>
        public static string NewPairingCode()
        {
            var code = new StringBuilder(PairingCodeLength);
            using (var random = RandomNumberGenerator.Create())
            {
                var one = new byte[1];
                while (code.Length < PairingCodeLength)
                {
                    random.GetBytes(one);
                    int value = one[0];
                    if (value >= 248) continue; // 248 = 8 x 31: keeps every character equally likely
                    code.Append(PairingAlphabet[value % PairingAlphabet.Length]);
                }
            }
            return code.ToString();
        }

        /// <summary>"ABCDE-FGHJK", for display.</summary>
        public static string FormatPairingCode(string code)
        {
            string clean = NormalizePairingCode(code);
            return clean.Length == PairingCodeLength ? clean.Substring(0, 5) + "-" + clean.Substring(5) : clean;
        }

        /// <summary>Upper case, without spaces or dashes, so "abcde fghjk" and "ABCDE-FGHJK" are the same code.</summary>
        public static string NormalizePairingCode(string code)
        {
            var clean = new StringBuilder();
            foreach (char c in code ?? "")
                if (char.IsLetterOrDigit(c)) clean.Append(char.ToUpperInvariant(c));
            return clean.ToString();
        }

        /// <summary>The pairing secret: PBKDF2 of the code, salted with this connection's nonces.</summary>
        public static byte[] PairingKey(string code, byte[] clientNonce, byte[] serverNonce, int iterations = PairingIterations)
        {
            var salt = new byte[PairingSalt.Length + clientNonce.Length + serverNonce.Length];
            Buffer.BlockCopy(PairingSalt, 0, salt, 0, PairingSalt.Length);
            Buffer.BlockCopy(clientNonce, 0, salt, PairingSalt.Length, clientNonce.Length);
            Buffer.BlockCopy(serverNonce, 0, salt, PairingSalt.Length + clientNonce.Length, serverNonce.Length);
            return Pbkdf2(Encoding.UTF8.GetBytes(NormalizePairingCode(code)), salt, iterations, KeyBytes);
        }
    }
}
