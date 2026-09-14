using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace Lev.Git
{
    /// <summary>
    /// Секреты пакета — токены API интеграций — в системном хранилище учётных
    /// данных этой машины.
    ///
    /// Не в проекте: настройки проекта коммитятся и уезжают всей команде. Не в
    /// EditorPrefs: там они лежат открытым текстом в реестре. И не в хранилище
    /// git: токен API отдельный от учётных данных для push, и их нельзя
    /// перепутать или отозвать одно вместе с другим.
    ///
    /// Windows — Диспетчер учётных данных, Linux — Secret Service через
    /// secret-tool. На macOS пока не поддерживается: связка ключей через
    /// командную строку принимает секрет только в аргументах, а они видны в
    /// списке процессов.
    /// </summary>
    public static class SecretStore
    {
        private const string Prefix = "ru.lev.unity-git:";

        public static bool Supported
        {
            get
            {
                return Application.platform == RuntimePlatform.WindowsEditor ||
                       (Application.platform == RuntimePlatform.LinuxEditor && SecretToolAvailable());
            }
        }

        public static string UnsupportedReason
        {
            get
            {
                switch (Application.platform)
                {
                    case RuntimePlatform.OSXEditor:
                        return L.T("Storing the token is not supported on macOS yet.");
                    case RuntimePlatform.LinuxEditor:
                        return L.T("secret-tool not found (libsecret-tools package) — there is nowhere to store the token.");
                    default:
                        return null;
                }
            }
        }

        /// <summary>Секрет по ключу или null.</summary>
        public static string Read(string key)
        {
            try
            {
                if (Application.platform == RuntimePlatform.WindowsEditor) return WinRead(Prefix + key);
                if (Application.platform == RuntimePlatform.LinuxEditor) return LinuxRead(key);
            }
            catch (Exception e)
            {
                Diagnostics.Journal.Warn(L.F("Credential store is unavailable: {0}", e.Message));
            }
            return null;
        }

        public static bool Write(string key, string secret, string label, out string error)
        {
            error = null;
            try
            {
                if (Application.platform == RuntimePlatform.WindowsEditor) return WinWrite(Prefix + key, secret, label, out error);
                if (Application.platform == RuntimePlatform.LinuxEditor) return LinuxWrite(key, secret, label, out error);
                error = UnsupportedReason ?? L.T("Credential store is unavailable.");
            }
            catch (Exception e)
            {
                error = e.Message;
            }
            return false;
        }

        public static bool Delete(string key)
        {
            try
            {
                if (Application.platform == RuntimePlatform.WindowsEditor) return CredDelete(Prefix + key, CredTypeGeneric, 0);
                if (Application.platform == RuntimePlatform.LinuxEditor) return RunSecretTool("clear service ru.lev.unity-git key " + Quote(key), null, out _) == 0;
            }
            catch { }
            return false;
        }

        // ------------------------------------------------------------ Windows ---

        private const uint CredTypeGeneric = 1;
        private const uint CredPersistLocalMachine = 2;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct Credential
        {
            public uint Flags;
            public uint Type;
            public string TargetName;
            public string Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public string TargetAlias;
            public string UserName;
        }

        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredWrite(ref Credential credential, uint flags);

        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredDelete(string target, uint type, uint flags);

        [DllImport("advapi32.dll")]
        private static extern void CredFree(IntPtr buffer);

        private static string WinRead(string target)
        {
            IntPtr ptr;
            if (!CredRead(target, CredTypeGeneric, 0, out ptr)) return null;

            try
            {
                var cred = (Credential)Marshal.PtrToStructure(ptr, typeof(Credential));
                if (cred.CredentialBlobSize == 0 || cred.CredentialBlob == IntPtr.Zero) return null;

                var bytes = new byte[cred.CredentialBlobSize];
                Marshal.Copy(cred.CredentialBlob, bytes, 0, bytes.Length);
                var value = Encoding.UTF8.GetString(bytes);
                Array.Clear(bytes, 0, bytes.Length);
                return value;
            }
            finally
            {
                CredFree(ptr);
            }
        }

        private static bool WinWrite(string target, string secret, string label, out string error)
        {
            error = null;
            var bytes = Encoding.UTF8.GetBytes(secret ?? string.Empty);
            var blob = Marshal.AllocHGlobal(bytes.Length);

            try
            {
                Marshal.Copy(bytes, 0, blob, bytes.Length);

                var cred = new Credential
                {
                    Type = CredTypeGeneric,
                    TargetName = target,
                    Comment = label,
                    CredentialBlobSize = (uint)bytes.Length,
                    CredentialBlob = blob,
                    Persist = CredPersistLocalMachine,
                    UserName = "unity-git"
                };

                if (CredWrite(ref cred, 0)) return true;

                error = L.F("Credential Manager refused (code {0}).", Marshal.GetLastWin32Error());
                return false;
            }
            finally
            {
                // Затираем копии токена в памяти, прежде чем отпустить.
                for (int i = 0; i < bytes.Length; i++) Marshal.WriteByte(blob, i, 0);
                Marshal.FreeHGlobal(blob);
                Array.Clear(bytes, 0, bytes.Length);
            }
        }

        // -------------------------------------------------------------- Linux ---

        private static bool? _secretTool;

        private static bool SecretToolAvailable()
        {
            if (_secretTool.HasValue) return _secretTool.Value;
            try { _secretTool = RunSecretTool("--version", null, out _) >= 0; }
            catch { _secretTool = false; }
            return _secretTool.Value;
        }

        private static string LinuxRead(string key)
        {
            string output;
            return RunSecretTool("lookup service ru.lev.unity-git key " + Quote(key), null, out output) == 0 && output.Length > 0
                ? output : null;
        }

        private static bool LinuxWrite(string key, string secret, string label, out string error)
        {
            string output;
            // Секрет — через stdin: аргументы видны в списке процессов.
            int code = RunSecretTool("store --label=" + Quote(label) + " service ru.lev.unity-git key " + Quote(key), secret, out output);
            error = code == 0 ? null : L.F("secret-tool exited with code {0}.", code);
            return code == 0;
        }

        /// <summary>Свой запуск, а не общий: вывод здесь — секрет, и в журнал команд он не должен попасть даже случайно.</summary>
        private static int RunSecretTool(string args, string stdin, out string output)
        {
            output = string.Empty;
            var psi = new ProcessStartInfo("secret-tool", args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = stdin != null
            };

            using (var p = Process.Start(psi))
            {
                if (p == null) return -1;
                if (stdin != null)
                {
                    p.StandardInput.Write(stdin);
                    p.StandardInput.Close();
                }
                output = p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                if (!p.WaitForExit(15000)) { try { p.Kill(); } catch { } return -1; }
                return p.ExitCode;
            }
        }

        private static string Quote(string s)
        {
            return "\"" + (s ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }
    }
}
