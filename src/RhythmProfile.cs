using System;

namespace AutoTyper
{
    /// <summary>
    /// How people actually type, measured rather than invented.
    ///
    /// Source: nine sessions recorded with tools/rhythm-tracker.html, three typists,
    /// 6,602 characters at 53-74 wpm, analysed by tools/analyze-rhythm.ps1. Events whose
    /// timing could be proven impossible (a browser stall delivering several keystrokes
    /// at once) were discarded before any of these numbers were computed.
    ///
    /// The arrays are percentiles, from the 5th to the 95th in steps of 5. Sampling draws
    /// a uniform index and interpolates, which reproduces the measured shape exactly as it
    /// was: no assumption that it is normal, triangular or anything else. The outermost
    /// 5% at each end is left out deliberately, because that is where the outliers and the
    /// residual measurement noise live.
    /// </summary>
    internal static class RhythmProfile
    {
        /// <summary>How long a key stays down. Median 120 ms.</summary>
        static readonly double[] Dwell = {
            63.8, 73.3, 80.2, 88.3, 95.8, 99.3, 105.0, 111.6, 112.5, 119.8,
            126.5, 128.0, 133.5, 142.6, 144.1, 152.1, 160.1, 175.3, 192.3
        };

        /// <summary>
        /// Key-down to the next key-down, for the plain case: an ordinary letter after
        /// another letter, mid-word, no shift. Median 100 ms. Every multiplier below is
        /// relative to this.
        /// </summary>
        static readonly double[] BaseGap = {
            49.3, 63.7, 67.8, 74.8, 79.9, 81.6, 88.2, 95.3, 96.2, 100.1,
            104.7, 111.9, 117.8, 127.6, 135.9, 144.7, 160.5, 180.5, 223.4
        };

        /// <summary>Shift going down to the letter going down. Median 104 ms.</summary>
        static readonly double[] ShiftLead = {
            63.2, 63.8, 71.7, 75.7, 79.7, 81.4, 87.8, 95.4, 95.9, 104.0,
            111.7, 119.8, 127.1, 128.6, 160.2, 190.9, 238.8, 383.2, 484.4
        };

        /// <summary>
        /// Shift coming up, relative to the letter coming up. Mostly negative: Shift is
        /// let go roughly 40 ms *before* the letter is, not after it.
        /// </summary>
        static readonly double[] ShiftTrail = {
            -112.3, -104.0, -95.5, -87.9, -80.3, -80.1, -72.1, -71.8, -63.8, -40.0,
            -15.0, 8.0, 13.7, 15.9, 23.1, 39.3, 49.0, 65.2, 88.1
        };

        /// <summary>Gap between the backspaces of one correction. Median 144 ms.</summary>
        static readonly double[] BurstGap = {
            31.4, 111.8, 126.6, 127.3, 128.4, 131.4, 135.9, 142.4, 143.6, 143.9,
            144.3, 145.4, 152.0, 156.6, 159.6, 160.2, 160.8, 176.0, 208.5
        };

        /// <summary>The median of <see cref="BaseGap"/>, used to scale to a requested speed.</summary>
        public const double BaseGapMedianMs = 100.1;

        /// <summary>
        /// Average gap once the multipliers below have been applied, divided by
        /// <see cref="BaseGapMedianMs"/>. Replaying the model over the recorded sentences
        /// gives a mean of 124 ms against a base median of 100, so throughput is about a
        /// quarter lower than the base figure alone suggests. Used for the speed estimate
        /// in Settings, which would otherwise overstate it badly.
        /// </summary>
        public const double MeanGapRatio = 1.24;

        // Context multipliers, each the measured median for that case divided by the
        // median of BaseGap. The engine's old hand-picked values are in brackets.
        public const double SpaceFactor      = 0.98;   // was 1.30: nobody slows down for the space bar
        public const double WordStartFactor  = 1.60;   // was absent: the pause is on the letter *after* the space
        public const double CapitalFactor    = 2.08;   // was 1.20
        public const double PunctuationFactor = 2.23;  // was 1.55
        public const double DigitFactor      = 5.91;   // was 1.20; only 24 samples, so treat as approximate

        // Two keys struck by the same hand run slower than two that alternate. Measured
        // inside the plain case, which is the only place it is applied, and expressed
        // against the plain case as a whole so that using one or the other leaves the
        // overall median where the recordings put it. Measured across every keystroke
        // the gap looks much wider (1.30), but most of that is the word boundaries and
        // capitals that have their own multiplier already.
        public const double SameHandFactor   = 1.113;
        public const double AltHandFactor    = 0.961;

        // Mistakes. 144 of them in 6,602 characters, 93% corrected, noticed about two
        // characters later. Correction was Backspace 324 times and Ctrl+Backspace 21;
        // Delete and Ctrl+Delete were not used once by anyone, and nor was Caps Lock.
        public const int    CharsPerMistake      = 46;
        public const double CtrlBackspaceShare   = 0.06;   // 21 of 345 corrections

        [ThreadStatic] static Random _rnd;

        static Random Rnd
        {
            get
            {
                if (_rnd == null) _rnd = new Random(unchecked(Environment.TickCount * 31 + 17));
                return _rnd;
            }
        }

        /// <summary>
        /// Draws from a percentile table by picking a uniform position and interpolating
        /// between the two neighbouring percentiles.
        /// </summary>
        static double Sample(double[] q)
        {
            double pos = Rnd.NextDouble() * (q.Length - 1);
            int i = (int)pos;
            if (i >= q.Length - 1) return q[q.Length - 1];
            double frac = pos - i;
            return q[i] + (q[i + 1] - q[i]) * frac;
        }

        public static double DwellMs()      { return Sample(Dwell); }
        public static double BaseGapMs()    { return Sample(BaseGap); }
        public static double ShiftLeadMs()  { return Sample(ShiftLead); }
        public static double ShiftTrailMs() { return Sample(ShiftTrail); }
        public static double BurstGapMs()   { return Sample(BurstGap); }

        /// <summary>How many characters go by before a mistake is noticed. Median 2.</summary>
        public static int NoticeAfterChars()
        {
            double u = Rnd.NextDouble();
            if (u < 0.40) return 1;
            if (u < 0.65) return 2;
            if (u < 0.78) return 3;
            if (u < 0.88) return 4;
            if (u < 0.94) return 5;
            return 6 + (int)(Rnd.NextDouble() * 4);
        }

        public static bool Chance(double p) { return Rnd.NextDouble() < p; }
        public static int Next(int n)       { return Rnd.Next(n); }

        /// <summary>
        /// The keys either side of this one on a QWERTY row, which is where a slip of the
        /// finger actually lands. The mistake *rate* is measured; which wrong key gets hit
        /// is not, so this is the conventional adjacency model rather than a finding.
        /// </summary>
        public static char Neighbour(char c)
        {
            const string rows = "qwertyuiop\0asdfghjkl\0zxcvbnm";
            bool upper = char.IsUpper(c);
            char lower = char.ToLowerInvariant(c);

            int at = rows.IndexOf(lower);
            if (at < 0) return '\0';

            int pick = Rnd.Next(2) == 0 ? at - 1 : at + 1;
            if (pick < 0 || pick >= rows.Length) pick = (pick < 0) ? at + 1 : at - 1;
            if (pick < 0 || pick >= rows.Length) return '\0';

            char hit = rows[pick];
            if (hit == '\0') return '\0';          // ran off the end of a row
            return upper ? char.ToUpperInvariant(hit) : hit;
        }
    }
}
