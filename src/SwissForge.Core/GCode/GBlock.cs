using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace SwissForge.Core.GCode
{
    /// <summary>One address/value pair, e.g. <c>G01</c>, <c>X-12.5</c>, <c>M3</c>.</summary>
    public struct GWord
    {
        public char Address;
        public double Value;
        public string Raw;

        /// <summary>True when the value carried a decimal point, which matters for Fanuc integer-scaling.</summary>
        public bool HadDecimal;

        public GWord(char address, double value, string raw, bool hadDecimal)
        {
            Address = char.ToUpperInvariant(address);
            Value = value;
            Raw = raw;
            HadDecimal = hadDecimal;
        }

        public override string ToString() => Raw ?? (Address + Value.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>One line of NC code, parsed.</summary>
    public sealed class GBlock
    {
        /// <summary>1-based line number in the source file.</summary>
        public int LineNumber { get; set; }

        /// <summary>The original text, unmodified.</summary>
        public string Raw { get; set; } = "";

        public List<GWord> Words { get; } = new List<GWord>();

        /// <summary>Text inside parentheses or after a semicolon.</summary>
        public string Comment { get; set; } = "";

        /// <summary>True when the line starts with '/' (block delete).</summary>
        public bool IsBlockDelete { get; set; }

        /// <summary>Channel this block belongs to, 0-based. Set by the channel splitter.</summary>
        public int Channel { get; set; }

        /// <summary>Raw waitcode text if this block carries one, e.g. "!1L20" or "M100".</summary>
        public string WaitCodeRaw { get; set; }

        /// <summary>Normalised waitcode label used for pairing across channels.</summary>
        public string WaitLabel { get; set; }

        /// <summary>Channels this waitcode explicitly names, when the dialect encodes them.</summary>
        public List<int> WaitPartners { get; } = new List<int>();

        public bool IsEmpty => Words.Count == 0 && string.IsNullOrEmpty(WaitCodeRaw);

        public bool Has(char address) => Words.Any(w => w.Address == char.ToUpperInvariant(address));

        /// <summary>First value for an address, or null when absent.</summary>
        public double? Get(char address)
        {
            char a = char.ToUpperInvariant(address);
            foreach (var w in Words) if (w.Address == a) return w.Value;
            return null;
        }

        /// <summary>All values for an address. A block can legally carry several G words.</summary>
        public IEnumerable<double> GetAll(char address)
        {
            char a = char.ToUpperInvariant(address);
            foreach (var w in Words) if (w.Address == a) yield return w.Value;
        }

        /// <summary>True when this block contains the given G code, e.g. HasG(1) for G01.</summary>
        public bool HasG(int code) => GetAll('G').Any(v => (int)Math.Round(v) == code);

        /// <summary>True when this block contains the given M code.</summary>
        public bool HasM(int code) => GetAll('M').Any(v => (int)Math.Round(v) == code);

        public IEnumerable<int> GCodes => GetAll('G').Select(v => (int)Math.Round(v));
        public IEnumerable<int> MCodes => GetAll('M').Select(v => (int)Math.Round(v));

        /// <summary>True when this block commands motion along any axis.</summary>
        public bool HasMotionWord =>
            Has('X') || Has('Y') || Has('Z') || Has('U') || Has('V') || Has('W') ||
            Has('A') || Has('B') || Has('C');

        public override string ToString() =>
            $"N{LineNumber}: {Raw}";
    }
}
