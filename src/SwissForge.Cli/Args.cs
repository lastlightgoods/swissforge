using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SwissForge.Cli
{
    /// <summary>Minimal argument parsing: --flag, --key value, --key=value, and positionals.</summary>
    public sealed class Args
    {
        private readonly Dictionary<string, string> _options =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _positional = new List<string>();

        public string Command { get; private set; } = "";
        public IReadOnlyList<string> Positional => _positional;

        public static Args Parse(string[] argv)
        {
            var a = new Args();
            if (argv == null || argv.Length == 0) return a;

            int start = 0;
            if (!argv[0].StartsWith("-", StringComparison.Ordinal))
            {
                a.Command = argv[0];
                start = 1;
            }

            for (int i = start; i < argv.Length; i++)
            {
                var token = argv[i];

                if (!token.StartsWith("--", StringComparison.Ordinal))
                {
                    a._positional.Add(token);
                    continue;
                }

                var body = token.Substring(2);
                int eq = body.IndexOf('=');
                if (eq >= 0)
                {
                    a._options[body.Substring(0, eq)] = body.Substring(eq + 1);
                    continue;
                }

                // A flag followed by a non-flag token takes it as its value.
                if (i + 1 < argv.Length && !argv[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    a._options[body] = argv[++i];
                    continue;
                }

                a._options[body] = "true";
            }

            return a;
        }

        public bool Has(string name) => _options.ContainsKey(name);

        public string Str(string name, string fallback = null) =>
            _options.TryGetValue(name, out var v) ? v : fallback;

        public double Num(string name, double fallback)
        {
            var s = Str(name);
            return s != null && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                ? d : fallback;
        }

        public int Int(string name, int fallback) => (int)Math.Round(Num(name, fallback));

        public bool Flag(string name, bool fallback = false)
        {
            var s = Str(name);
            if (s == null) return fallback;
            return s == "true" || s == "1" || s.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }

        public T Enum<T>(string name, T fallback) where T : struct
        {
            var s = Str(name);
            return s != null && System.Enum.TryParse<T>(s, true, out var v) ? v : fallback;
        }
    }
}
