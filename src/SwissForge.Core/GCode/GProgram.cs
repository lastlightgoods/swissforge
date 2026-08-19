using System;
using System.Collections.Generic;
using System.Linq;
using SwissForge.Core.Json;
using SwissForge.Core.Model;

namespace SwissForge.Core.GCode
{
    /// <summary>A parsed NC program, before or after channel splitting.</summary>
    public sealed class GProgram
    {
        public string Name { get; set; } = "";

        public List<GBlock> Blocks { get; } = new List<GBlock>();

        /// <summary>True when the file carried % tape markers.</summary>
        public bool HasTapeMarkers { get; set; }

        /// <summary>Tokens the parser could not classify. Surfaced by the linter, never dropped.</summary>
        public List<string> UnparsedTokens { get; } = new List<string>();

        /// <summary>Lines where a '(' comment was never closed.</summary>
        public List<int> UnterminatedCommentLines { get; } = new List<int>();

        /// <summary>Channel indices present after splitting.</summary>
        public List<int> Channels =>
            Blocks.Select(b => b.Channel).Distinct().OrderBy(c => c).ToList();

        public IEnumerable<GBlock> BlocksIn(int channel) => Blocks.Where(b => b.Channel == channel);

        /// <summary>O-numbers declared in the file.</summary>
        public List<int> ProgramNumbers =>
            Blocks.Where(b => b.Has('O'))
                  .Select(b => (int)Math.Round(b.Get('O') ?? 0))
                  .Distinct().ToList();

        /// <summary>
        /// Assigns every block to a channel and resolves waitcodes into comparable labels.
        /// <para>
        /// Channel assignment follows the "$n" section markers. A file with no markers is
        /// treated as a single channel, which is the right answer for a sub-program or for
        /// a single-path machine.
        /// </para>
        /// </summary>
        public void SplitChannels(ControlProfile profile)
        {
            profile = profile ?? ControlProfile.For(ControlDialect.FanucGeneric);

            int current = 0;
            bool sawMarker = false;

            foreach (var block in Blocks)
            {
                var marker = block.Get('$');
                if (marker.HasValue)
                {
                    // "$1" is channel 0 here. Machinists count from one; arrays do not.
                    current = Math.Max(0, (int)Math.Round(marker.Value) - 1);
                    sawMarker = true;
                }

                block.Channel = current;

                // Bang-style waitcode captured by the parser
                if (!string.IsNullOrEmpty(block.WaitCodeRaw))
                {
                    block.WaitPartners.Clear();
                    block.WaitLabel = profile.NormalizeWaitLabel(block.WaitCodeRaw, block.WaitPartners);
                }

                // M-code waits
                if (profile.UsesMCodeWaits)
                {
                    foreach (var m in block.MCodes)
                    {
                        if (!profile.IsWaitMCode(m)) continue;
                        block.WaitCodeRaw = block.WaitCodeRaw ?? ("M" + m);
                        block.WaitLabel = block.WaitLabel ?? ("M" + m);
                    }
                }
            }

            if (!sawMarker)
                foreach (var block in Blocks) block.Channel = 0;
        }

        public JsonValue ToJson(bool includeBlocks = false)
        {
            var j = JsonValue.Obj();
            j.Set("name", Name);
            j.Set("blockCount", Blocks.Count);
            j.Set("channels", Channels);
            j.Set("programNumbers", ProgramNumbers);
            j.Set("hasTapeMarkers", HasTapeMarkers);
            j.Set("unparsedTokens", UnparsedTokens);
            j.Set("unterminatedCommentLines", UnterminatedCommentLines);

            if (includeBlocks)
            {
                var arr = JsonValue.Arr();
                foreach (var b in Blocks)
                {
                    if (b.IsEmpty && string.IsNullOrEmpty(b.Comment)) continue;
                    var bj = JsonValue.Obj();
                    bj.Set("line", b.LineNumber);
                    bj.Set("channel", b.Channel);
                    bj.Set("raw", b.Raw.Trim());
                    if (!string.IsNullOrEmpty(b.Comment)) bj.Set("comment", b.Comment);
                    if (!string.IsNullOrEmpty(b.WaitLabel)) bj.Set("waitLabel", b.WaitLabel);
                    arr.Add(bj);
                }
                j["blocks"] = arr;
            }

            return j;
        }
    }
}
