using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace RobotVacuum.Network
{
    /// <summary>Where pairing keys are kept. Never PlayerPrefs, settings files or job files (C.12).</summary>
    public interface ISecretStore
    {
        bool Write(string name, byte[] secret, out string problem);
        byte[] Read(string name);
        void Delete(string name);
        string Describe();
    }

    public static class SecretStore
    {
        /// <summary>
        /// The store for this computer: Windows Credential Manager on Windows (the keys are protected by the user's
        /// Windows sign-in and can be seen or removed in Control Panel › Credential Manager); elsewhere, files that
        /// only this user can read, in <paramref name="fallbackFolder"/>.
        /// </summary>
        public static ISecretStore ForThisComputer(string fallbackFolder) =>
            Environment.OSVersion.Platform == PlatformID.Win32NT
                ? (ISecretStore)new WindowsCredentialStore()
                : new PrivateFileStore(fallbackFolder);
    }

    /// <summary>Generic credentials in Windows Credential Manager, named "RobotVacuumSim/&lt;name&gt;".</summary>
    public sealed class WindowsCredentialStore : ISecretStore
    {
        public const string Prefix = "RobotVacuumSim/";
        const uint TypeGeneric = 1;
        const uint PersistLocalMachine = 2;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct Credential
        {
            public uint Flags;
            public uint Type;
            public string TargetName;
            public string Comment;
            public long LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public string TargetAlias;
            public string UserName;
        }

        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CredWrite(ref Credential credential, uint flags);

        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CredDelete(string target, uint type, uint flags);

        [DllImport("advapi32.dll", EntryPoint = "CredFree")]
        static extern void CredFree(IntPtr buffer);

        public string Describe() => "Windows Credential Manager";

        public bool Write(string name, byte[] secret, out string problem)
        {
            problem = null;
            IntPtr blob = Marshal.AllocHGlobal(secret.Length);
            try
            {
                Marshal.Copy(secret, 0, blob, secret.Length);
                var credential = new Credential
                {
                    Type = TypeGeneric,
                    TargetName = Prefix + name,
                    Comment = "Robot vacuum simulator network worker pairing key",
                    CredentialBlobSize = (uint)secret.Length,
                    CredentialBlob = blob,
                    Persist = PersistLocalMachine,
                    UserName = Environment.UserName,
                };
                if (CredWrite(ref credential, 0)) return true;
                problem = $"Windows Credential Manager refused the key (error {Marshal.GetLastWin32Error()}).";
                return false;
            }
            catch (Exception exception)
            {
                problem = $"Windows Credential Manager is not available: {exception.Message}";
                return false;
            }
            finally
            {
                for (int i = 0; i < secret.Length; i++) Marshal.WriteByte(blob, i, 0);
                Marshal.FreeHGlobal(blob);
            }
        }

        public byte[] Read(string name)
        {
            try
            {
                if (!CredRead(Prefix + name, TypeGeneric, 0, out IntPtr pointer)) return null;
                try
                {
                    var credential = (Credential)Marshal.PtrToStructure(pointer, typeof(Credential));
                    if (credential.CredentialBlobSize == 0 || credential.CredentialBlob == IntPtr.Zero) return null;
                    var secret = new byte[credential.CredentialBlobSize];
                    Marshal.Copy(credential.CredentialBlob, secret, 0, secret.Length);
                    return secret;
                }
                finally
                {
                    CredFree(pointer);
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        public void Delete(string name)
        {
            try { CredDelete(Prefix + name, TypeGeneric, 0); }
            catch (Exception) { }
        }
    }

    /// <summary>
    /// One file per secret in a folder only the current user can open (chmod 700/600). For Linux and macOS workers,
    /// which have no Credential Manager; on Windows the Credential Manager is always used.
    /// </summary>
    public sealed class PrivateFileStore : ISecretStore
    {
        readonly string folder;

        public PrivateFileStore(string folder) => this.folder = folder;

        public string Describe() => "private files in " + folder;

        string PathOf(string name)
        {
            if (!WorkerProtocol.IsValidId(name.Replace('/', '_'))) throw new ArgumentException("Bad secret name.");
            return Path.Combine(folder, name.Replace('/', '_') + ".key");
        }

        public bool Write(string name, byte[] secret, out string problem)
        {
            problem = null;
            try
            {
                Directory.CreateDirectory(folder);
                Restrict(folder, "700");
                string path = PathOf(name);
                File.WriteAllText(path, Convert.ToBase64String(secret));
                Restrict(path, "600");
                return true;
            }
            catch (Exception exception)
            {
                problem = $"Could not save the key in {folder}: {exception.Message}";
                return false;
            }
        }

        public byte[] Read(string name)
        {
            try
            {
                string path = PathOf(name);
                return File.Exists(path) ? Convert.FromBase64String(File.ReadAllText(path).Trim()) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public void Delete(string name)
        {
            try
            {
                string path = PathOf(name);
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception) { }
        }

        static void Restrict(string path, string mode)
        {
            if (Environment.OSVersion.Platform == PlatformID.Win32NT) return;
            try
            {
                using (var chmod = Process.Start(new ProcessStartInfo("chmod", $"{mode} \"{path}\"") { UseShellExecute = false, CreateNoWindow = true }))
                    chmod?.WaitForExit(5000);
            }
            catch (Exception) { }
        }
    }

    /// <summary>Keeps secrets in memory only. For tests.</summary>
    public sealed class MemorySecretStore : ISecretStore
    {
        readonly Dictionary<string, byte[]> secrets = new Dictionary<string, byte[]>();

        public string Describe() => "memory";

        public bool Write(string name, byte[] secret, out string problem)
        {
            problem = null;
            lock (secrets) secrets[name] = (byte[])secret.Clone();
            return true;
        }

        public byte[] Read(string name)
        {
            lock (secrets) return secrets.TryGetValue(name, out var secret) ? (byte[])secret.Clone() : null;
        }

        public void Delete(string name)
        {
            lock (secrets) secrets.Remove(name);
        }
    }
}
