using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SwissForge.Core.GCode
{
    /// <summary>
    /// Turns NC text into blocks.
    /// <para>
    /// Deliberately permissive: real post-processor output is full of vendor extensions,
    /// and a parser that throws on the first unfamiliar address is useless for auditing
    /// the programs a shop actually runs. Anything it cannot classify is preserved on the
    /// block as raw text and reported by the linter rather than silently dropped.
    /// </para>
    /// </summary>
    public sealed class GParser
    {
        /// <summary>Addresses treated as numeric words. Everything else is flagged, not discarded.</summary>
        private const string KnownAddresses = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";

        public GProgram Parse(string text, string programName = "")
        {
            var program = new GProgram { Name = programName };
            if (string.IsNullOrEmpty(text)) return program;

            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            for (int i = 0; i < lines.Length; i++)
            {
                var block = ParseLine(lines[i], i + 1, program);
                if (block != null) program.Blocks.Add(block);
            }

            return program;
        }

        private GBlock ParseLine(string line, int lineNumber, GProgram program)
        {
            var block = new GBlock { LineNumber = lineNumber, Raw = line };
            if (string.IsNullOrWhiteSpace(line)) return block;

            var comment = new StringBuilder();
            int i = 0;
            int depth = 0;

            // Leading block-delete slash
            while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
            if (i < line.Length && line[i] == '/')
            {
                block.IsBlockDelete = true;
                i++;
            }

            while (i < line.Length)
            {
                char c = line[i];

                if (char.IsWhiteSpace(c)) { i++; continue; }

                // --- comments
                if (c == '(')
                {
                    depth++;
                    i++;
                    int start = i;
                    while (i < line.Length && depth > 0)
                    {
                        if (line[i] == '(') depth++;
                        else if (line[i] == ')') depth--;
                        if (depth > 0) i++;
                    }
                    comment.Append(line.Substring(start, Math.Max(0, i - start)));
                    if (i < line.Length) i++;   // closing paren
                    else program.UnterminatedCommentLines.Add(lineNumber);
                    continue;
                }

                if (c == ';')
                {
                    comment.Append(line.Substring(i + 1));
                    break;
                }

                // --- percent (tape start/end) and program-section markers are kept verbatim
                if (c == '%')
                {
                    program.HasTapeMarkers = true;
                    i++;
                    continue;
                }

                // --- Citizen/Fanuc channel selector and waitcode, e.g. "$2" or "!1L20"
                if (c == '$' || c == '!')
                {
                    int start = i;
                    i++;
                    while (i < line.Length && !char.IsWhiteSpace(line[i]) && line[i] != '(' && line[i] != ';') i++;
                    var token = line.Substring(start, i - start);

                    if (c == '$') block.Words.Add(new GWord('$', ParseChannelToken(token), token, false));
                    else block.WaitCodeRaw = token;
                    continue;
                }

                // --- ordinary address/value word
                if (char.IsLetter(c))
                {
                    char address = char.ToUpperInvariant(c);
                    int start = i;
                    i++;

                    int numStart = i;
                    if (i < line.Length && (line[i] == '+' || line[i] == '-')) i++;
                    bool hadDecimal = false;
                    while (i < line.Length && (char.IsDigit(line[i]) || line[i] == '.'))
                    {
                        if (line[i] == '.') hadDecimal = true;
                        i++;
                    }

                    var numText = line.Substring(numStart, i - numStart);
                    var raw = line.Substring(start, i - start);

                    if (numText.Length == 0)
                    {
                        // A bare letter with no value: a macro reference or a typo.
                        program.UnparsedTokens.Add($"line {lineNumber}: bare address '{address}'");
                        block.Words.Add(new GWord(address, double.NaN, raw, false));
                        continue;
                    }

                    if (!double.TryParse(numText, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                    {
                        program.UnparsedTokens.Add($"line {lineNumber}: cannot read '{raw}'");
                        block.Words.Add(new GWord(address, double.NaN, raw, hadDecimal));
                        continue;
                    }

                    if (KnownAddresses.IndexOf(address) < 0)
                        program.UnparsedTokens.Add($"line {lineNumber}: unexpected address '{address}'");

                    block.Words.Add(new GWord(address, value, raw, hadDecimal));
                    continue;
                }

                // --- anything else: macro arithmetic, brackets, vendor syntax. Preserve and note.
                {
                    int start = i;
                    while (i < line.Length && !char.IsLetterOrDigit(line[i]) && !char.IsWhiteSpace(line[i])) i++;
                    if (i == start) i++;
                    var token = line.Substring(start, i - start);
                    if (token != "=" && token.Trim().Length > 0)
                        program.UnparsedTokens.Add($"line {lineNumber}: unhandled token '{token}'");
                }
            }

            block.Comment = comment.ToString().Trim();
            return block;
        }

        /// <summary>Reads the numeric part of a "$2" style channel marker.</summary>
        private static double ParseChannelToken(string token)
        {
            var digits = new StringBuilder();
            foreach (var ch in token) if (char.IsDigit(ch)) digits.Append(ch);
            return digits.Length > 0 && int.TryParse(digits.ToString(), out var n) ? n : 0;
        }
    }
}
