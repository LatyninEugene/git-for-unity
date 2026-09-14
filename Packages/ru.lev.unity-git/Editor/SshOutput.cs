using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Lev.Git
{
    /// <summary>Что показал `ssh -v`: пустил ли сервер, какие ключи предлагались и какой он принял.</summary>
    internal sealed class SshProbe
    {
        public bool Authenticated;
        public bool HostKeyFailed;
        public bool ConnectionFailed;

        /// <summary>Ключи в том порядке, в каком ssh их предлагал.</summary>
        public readonly List<string> Offered = new List<string>();

        /// <summary>
        /// Ключ, который сервер согласился принять: путь к файлу, а для ключа из
        /// агента — его комментарий. Принят — ещё не значит «вошли»: у ключа под
        /// паролем ssh в пакетном режиме дальше открыть закрытую часть не может.
        /// </summary>
        public string AcceptedKey;

        public bool AcceptedFromAgent;
    }

    /// <summary>
    /// Разбор вывода утилит OpenSSH для диагностики отказа «Permission denied (publickey)».
    ///
    /// От Unity не зависит: проверяется чистыми тестами.
    /// </summary>
    internal static class SshOutput
    {
        // "debug1: Server accepts key: C:\\Users\\u\\.ssh\\github ED25519 SHA256:… explicit"
        // Путь может содержать пробелы, поэтому он заканчивается там, где начинаются тип и отпечаток.
        private static readonly Regex KeyLine = new Regex(
            @"^debug1: (?<what>Offering public key|Server accepts key): (?<key>.+?) (?<type>[A-Z0-9][A-Z0-9\-@.]*) SHA256:\S+(?: (?<how>.*))?$",
            RegexOptions.CultureInvariant);

        private static readonly Regex UrlUser = new Regex(
            @"^(?:ssh://)?(?<user>[^@/:\s]+)@", RegexOptions.CultureInvariant);

        private static readonly Regex AgentSocket = new Regex(
            @"SSH_AUTH_SOCK=(?<v>[^;\s]+);", RegexOptions.CultureInvariant);

        private static readonly Regex AgentPid = new Regex(
            @"SSH_AGENT_PID=(?<v>\d+);", RegexOptions.CultureInvariant);

        private static readonly Regex KeygenList = new Regex(
            @"^\d+ (?<fp>SHA256:\S+)(?: .*?)? \((?<type>[^)]+)\)\s*$", RegexOptions.CultureInvariant);

        /// <summary>
        /// Программа для SSH_ASKPASS: отдаёт ssh-add пароль из переменной окружения
        /// процесса. В самом файле секрета нет. На повторный запрос («Bad passphrase,
        /// try again») отвечает отказом — иначе ssh-add спрашивал бы неверный пароль бесконечно.
        /// </summary>
        public const string AskpassScript =
            "#!/bin/sh\n" +
            "case \"$1\" in\n" +
            "  Bad*) exit 1 ;;\n" +
            "esac\n" +
            "printf '%s\\n' \"$LEV_GIT_ASKPASS\"\n";

        public const string AskpassVariable = "LEV_GIT_ASKPASS";

        public static SshProbe ParseProbe(string verbose)
        {
            var probe = new SshProbe();
            if (string.IsNullOrEmpty(verbose)) return probe;

            foreach (var raw in verbose.Replace("\r\n", "\n").Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;

                if (line.Contains("Authenticated to ") || line.Contains("You've successfully authenticated"))
                    probe.Authenticated = true;

                if (line.Contains("Host key verification failed") || line.Contains("REMOTE HOST IDENTIFICATION HAS CHANGED"))
                    probe.HostKeyFailed = true;

                if (line.Contains("Could not resolve hostname") || line.Contains("Connection timed out") ||
                    line.Contains("Connection refused") || line.Contains("Network is unreachable") ||
                    line.Contains("No route to host"))
                    probe.ConnectionFailed = true;

                var m = KeyLine.Match(line);
                if (!m.Success) continue;

                // ssh под Windows удваивает обратные слэши в путях.
                var key = m.Groups["key"].Value.Replace("\\\\", "\\");
                if (m.Groups["what"].Value == "Offering public key")
                {
                    probe.Offered.Add(key);
                }
                else
                {
                    probe.AcceptedKey = key;
                    probe.AcceptedFromAgent = m.Groups["how"].Value.Contains("agent");
                }
            }

            return probe;
        }

        /// <summary>Строки `ssh -v`, по которым видно, что произошло: соединение, ключи, итог. Для журнала.</summary>
        public static List<string> DiagnosticLines(string verbose)
        {
            var lines = new List<string>();
            if (string.IsNullOrEmpty(verbose)) return lines;

            foreach (var raw in verbose.Replace("\r\n", "\n").Split('\n'))
            {
                var line = raw.Trim();
                if (line.Contains("Connecting to ") || line.Contains("connect to ") || line.Contains("Connection established") ||
                    line.Contains("Offering public key") || line.Contains("Server accepts key") ||
                    line.Contains("Authenticated to ") || line.Contains("Permission denied") ||
                    line.Contains("timed out") || line.Contains("Could not resolve") ||
                    line.Contains("Host key verification failed"))
                    lines.Add(line.Replace("\\\\", "\\"));
            }

            return lines;
        }

        /// <summary>Пользователь из адреса remote: git@host:… → git. Без пользователя — git, как у всех хостингов.</summary>
        public static string UserFromUrl(string url)
        {
            var m = UrlUser.Match(url ?? string.Empty);
            return m.Success ? m.Groups["user"].Value : "git";
        }

        /// <summary>Разбирает вывод `ssh-agent -s`.</summary>
        public static bool ParseAgent(string output, out string socket, out string pid)
        {
            var s = AgentSocket.Match(output ?? string.Empty);
            var p = AgentPid.Match(output ?? string.Empty);
            socket = s.Success ? s.Groups["v"].Value : null;
            pid = p.Success ? p.Groups["v"].Value : null;
            return socket != null && pid != null;
        }

        /// <summary>"256 SHA256:abc user@host (ED25519)" → "SHA256:abc (ED25519)". Не разобралось — пустая строка.</summary>
        public static string Fingerprint(string keygenOutput)
        {
            var m = KeygenList.Match((keygenOutput ?? string.Empty).Trim());
            return m.Success ? m.Groups["fp"].Value + " (" + m.Groups["type"].Value + ")" : string.Empty;
        }

        /// <summary>Один и тот же файл в записи Windows (C:\…) и msys (/c/…).</summary>
        public static bool SamePath(string a, string b)
        {
            return string.Equals(NormPath(a), NormPath(b), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Путь ключа для человека: внутри домашней папки — через ~.</summary>
        public static string DisplayPath(string path, string home)
        {
            var p = NormPath(path);
            var h = NormPath(home).TrimEnd('/');
            if (h.Length > 0 && p.StartsWith(h + "/", StringComparison.OrdinalIgnoreCase))
                return "~" + p.Substring(h.Length);
            return p;
        }

        private static string NormPath(string path)
        {
            var p = (path ?? string.Empty).Trim().Replace('\\', '/');
            var msys = Regex.Match(p, @"^/(?<drive>[a-zA-Z])(?:/|$)");
            if (msys.Success) p = msys.Groups["drive"].Value + ":" + p.Substring(2);
            return p;
        }
    }
}
