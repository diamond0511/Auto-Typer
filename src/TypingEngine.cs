using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace AutoTyper
{
    internal enum StopReason
    {
        Completed,
        Aborted,
        FocusLost,
        MouseClicked,
        Blocked,
        Error
    }

    /// <summary>
    /// Types a string as synthetic keystrokes on a background thread.
    ///
    /// Characters go out as real virtual keys wherever the active keyboard layout can
    /// produce them, with Shift genuinely pressed and released around them. That matters
    /// for web forms: a Unicode packet carries no physical key, so pages see an empty
    /// event.code and shiftKey always false, which breaks input masks and per-keystroke
    /// validation. Packets remain the fallback for characters the layout cannot type.
    ///
    /// The pacing comes from <see cref="RhythmProfile"/>, which is measured from recordings
    /// of real typists rather than guessed. The important consequence is that keys overlap:
    /// a key is held for about 120 ms while the next one follows about 100 ms later, so the
    /// next key goes down before the previous one comes up roughly half the time. Releases
    /// are therefore scheduled on a clock rather than performed in line, which is what the
    /// pending list below is for.
    ///
    /// Progress/Stopped fire on this thread; the caller marshals to the UI.
    /// </summary>
    internal sealed class TypingEngine
    {
        static readonly int InputSize = Marshal.SizeOf(typeof(NativeMethods.INPUT));

        /// <summary>A key that is down, and when it is due to come up.</summary>
        struct Release
        {
            public double At;
            public ushort Vk;                      // 0 for a Unicode packet
            public NativeMethods.INPUT Input;
        }

        volatile bool _running;
        volatile bool _abort;
        readonly object _gate = new object();

        // ---- run state (one run at a time, guarded by _running) -------------
        AppSettings _s;
        IntPtr _layout;
        double _scale = 1.0;
        bool _shiftHeld;
        bool _sendFailed;
        Stopwatch _clock;
        readonly List<Release> _pending = new List<Release>();

        // Mouse watch. The keyboard and the mouse are not usable at the same time during a
        // run, so a click ends it.
        volatile bool _mouseClicked;
        bool _watchMouse;
        double _lastMousePoll;

        public event Action<int, int> Progress;              // typed, total
        public event Action<StopReason, int, int> Stopped;    // reason, typed, total

        public bool IsRunning
        {
            get { return _running; }
        }

        public void Abort()
        {
            _abort = true;
        }

        /// <summary>Returns false if a run is already in progress or there is nothing to type.</summary>
        public bool Start(string text, AppSettings settings)
        {
            if (string.IsNullOrEmpty(text)) return false;

            AppSettings snapshot = settings.Clone();   // settings may change mid-run
            snapshot.Normalize();

            lock (_gate)
            {
                if (_running) return false;
                _running = true;
                _abort = false;
            }

            Thread t = new Thread((ThreadStart)delegate { Run(text, snapshot); });
            t.IsBackground = true;
            t.Name = "AutoTyper.Typing";
            // Keystroke pacing is timing sensitive; a nudge above normal reduces jitter.
            t.Priority = ThreadPriority.AboveNormal;
            t.Start();
            return true;
        }

        // ---- the run ------------------------------------------------------

        void Run(string text, AppSettings s)
        {
            StopReason reason = StopReason.Completed;
            int typed = 0;
            int total = text.Length;

            _s = s;
            _shiftHeld = false;
            _sendFailed = false;
            _pending.Clear();
            _mouseClicked = false;
            _watchMouse = false;
            _lastMousePoll = 0;
            _clock = Stopwatch.StartNew();

            bool timerRaised = false;
            try
            {
                // Without this, Thread.Sleep rounds up to the system tick and every gap
                // comes out longer than asked for.
                timerRaised = NativeMethods.timeBeginPeriod(1) == 0;

                // The hotkey that triggered us is physically still held. Injecting characters
                // while Ctrl/Alt are down would produce shortcuts instead of text.
                WaitForModifiersReleased(2000);
                ForceReleaseModifiers();

                if (!Sleep(s.StartDelayMs, false))
                {
                    reason = StopReason.Aborted;
                    return;
                }

                // Captured *after* the start delay, so the user has had time to click the
                // target field without that counting as a focus change.
                IntPtr target = NativeMethods.GetForegroundWindow();
                _layout = LayoutOf(target);
                _scale = SpeedScale(s);

                // Same reason: clicking into the field during the start delay is expected,
                // so clicks only count from here on.
                BeginMouseWatch();

                Stopwatch progressClock = Stopwatch.StartNew();
                int i = 0;
                bool firstKey = true;
                int sinceMistake = 0;
                int nextMistakeAt = NextMistakeInterval();

                while (i < total)
                {
                    if (_abort)
                    {
                        reason = StopReason.Aborted;
                        return;
                    }

                    if (MouseClicked())
                    {
                        reason = StopReason.MouseClicked;
                        return;
                    }

                    if (s.StopOnFocusChange && NativeMethods.GetForegroundWindow() != target)
                    {
                        reason = StopReason.FocusLost;
                        return;
                    }

                    char c = text[i];
                    int consumed = 1;

                    // A deliberate slip, if the option is on and this character is an
                    // ordinary one. The wrong key, a couple more characters before it is
                    // noticed, then backspaces: the pattern the recordings showed.
                    if (s.MakeMistakes && !firstKey && sinceMistake >= nextMistakeAt && IsPlain(c))
                    {
                        if (TypeMistake(text, i))
                        {
                            sinceMistake = 0;
                            nextMistakeAt = NextMistakeInterval();
                        }
                        // A correction ignores both stop signals until it is finished, so
                        // whichever arrived during one is acted on here instead.
                        if (_abort)
                        {
                            reason = StopReason.Aborted;
                            return;
                        }
                        if (_mouseClicked)
                        {
                            reason = StopReason.MouseClicked;
                            return;
                        }
                    }

                    char next = (i + 1 < total) ? text[i + 1] : '\0';

                    if (c == '\r' || c == '\n')
                    {
                        // Treat CRLF as a single break.
                        if (c == '\r' && i + 1 < total && text[i + 1] == '\n')
                        {
                            consumed = 2;
                            next = (i + 2 < total) ? text[i + 2] : '\0';
                        }

                        if (s.NewlineAsEnter) EmitKey(NativeMethods.VK_RETURN, false, next == '\0', GapMs(c, next), false);
                        else EmitPacket('\n', GapMs(c, next), false);
                    }
                    else if (c == '\t' && s.TabAsTab)
                    {
                        EmitKey(NativeMethods.VK_TAB, false, true, GapMs(c, next), false);
                    }
                    else if (char.IsHighSurrogate(c) && i + 1 < total && char.IsLowSurrogate(text[i + 1]))
                    {
                        // Astral characters (emoji) have no key on any layout.
                        consumed = 2;
                        next = (i + 2 < total) ? text[i + 2] : '\0';
                        EmitSurrogatePair(c, text[i + 1], GapMs(c, next), false);
                    }
                    else
                    {
                        EmitChar(c, next, false);
                    }

                    i += consumed;
                    typed += consumed;
                    sinceMistake += consumed;

                    if (_sendFailed && firstKey)
                    {
                        // Nothing got through at all: almost always UIPI blocking injection
                        // into an elevated window.
                        reason = StopReason.Blocked;
                        return;
                    }
                    firstKey = false;

                    if (progressClock.ElapsedMilliseconds >= 150 || i >= total)
                    {
                        progressClock.Restart();
                        Raise(Progress, typed, total);
                    }
                }

                // Let every key that is still down come up on its own schedule.
                FlushUntil(double.MaxValue, true);
            }
            catch (Exception)
            {
                reason = StopReason.Error;
            }
            finally
            {
                // Never leave a key or Shift stuck down, whatever happened above.
                ReleaseEverything();

                if (timerRaised)
                {
                    try { NativeMethods.timeEndPeriod(1); }
                    catch (Exception) { }
                }

                _running = false;

                Action<StopReason, int, int> h = Stopped;
                if (h != null)
                {
                    try { h(reason, typed, total); }
                    catch (Exception) { }
                }
            }
        }

        static void Raise(Action<int, int> handler, int a, int b)
        {
            if (handler == null) return;
            try { handler(a, b); }
            catch (Exception) { }
        }

        // ---- characters ----------------------------------------------------

        /// <summary>One character, as a real key if the layout has one, otherwise a packet.</summary>
        bool EmitChar(char c, char next, bool force)
        {
            ushort vk;
            bool needShift;

            if (_s.UseRealKeys && TryMapToKey(c, _layout, out vk, out needShift))
            {
                // Shift is let go once the run of capitals ends. Across an acronym like
                // HTTPS it stays down, which is what the recordings show people doing.
                bool nextNeedsShift = false;
                if (next != '\0')
                {
                    ushort nvk;
                    bool nshift;
                    if (TryMapToKey(next, _layout, out nvk, out nshift)) nextNeedsShift = nshift;
                }

                return EmitKey(vk, needShift, !nextNeedsShift, GapMs(c, next), force);
            }

            // Layout cannot produce this character; fall back to a packet.
            if (_shiftHeld) ReleaseShiftNow(force);
            return EmitPacket(c, GapMs(c, next), force);
        }

        /// <summary>
        /// Presses a key, schedules its release, then waits until the next key is due.
        /// The release may fall after that point, which is how overlap happens.
        /// </summary>
        bool EmitKey(ushort vk, bool needShift, bool releaseShiftAfter, double gapMs, bool force)
        {
            // The same key cannot be pressed twice while still down: Windows would treat
            // that as auto-repeat rather than a second character.
            ReleaseKeyNow(vk);

            if (needShift && !_shiftHeld)
            {
                CancelPendingShiftRelease();
                if (!_shiftHeld)
                {
                    Send(MakeVirtualKey(NativeMethods.VK_LSHIFT, false, false, _layout));
                    _shiftHeld = true;
                    if (!Sleep(RhythmProfile.ShiftLeadMs() * _scale, force)) return false;
                }
            }
            else if (!needShift && _shiftHeld)
            {
                // The scheduled release has not come round yet. Never type a stray capital.
                ReleaseShiftNow(force);
            }

            double downAt = Now;
            Send(MakeVirtualKey(vk, false, false, _layout));

            double dwell = DwellMs();
            double nextDownAt = downAt + gapMs;

            Schedule(downAt + dwell, vk, MakeVirtualKey(vk, true, false, _layout));

            if (_shiftHeld && releaseShiftAfter)
            {
                // Measured: Shift comes up about 40 ms *before* the letter does.
                double at = downAt + dwell + RhythmProfile.ShiftTrailMs() * _scale;
                double latest = nextDownAt - 6;
                if (at > latest) at = latest;
                if (at < downAt + 8) at = downAt + 8;

                Schedule(at, NativeMethods.VK_LSHIFT,
                         MakeVirtualKey(NativeMethods.VK_LSHIFT, true, false, _layout));
                _shiftHeld = false;
            }

            if (!FlushUntil(nextDownAt, force)) return false;
            return SleepUntil(nextDownAt, force);
        }

        bool EmitPacket(char c, double gapMs, bool force)
        {
            double downAt = Now;
            Send(MakeUnicode(c, false));

            double dwell = DwellMs();
            double nextDownAt = downAt + gapMs;

            // A packet is not a physical key, so there is nothing to overlap with. Release
            // it in line and keep the pairing tight, which is what slow consumers need.
            if (!SleepUntil(Math.Min(downAt + dwell, nextDownAt), force)) return false;
            Send(MakeUnicode(c, true));

            return SleepUntil(nextDownAt, force);
        }

        bool EmitSurrogatePair(char high, char low, double gapMs, bool force)
        {
            double downAt = Now;

            // Both code units must reach the target as one character.
            NativeMethods.INPUT[] inputs = new NativeMethods.INPUT[4];
            inputs[0] = MakeUnicode(high, false);
            inputs[1] = MakeUnicode(high, true);
            inputs[2] = MakeUnicode(low, false);
            inputs[3] = MakeUnicode(low, true);

            if (NativeMethods.SendInput(4, inputs, InputSize) != 4) _sendFailed = true;
            return SleepUntil(downAt + gapMs, force);
        }

        // ---- mistakes -------------------------------------------------------

        /// <summary>
        /// Types the wrong key, carries on for a character or two, then backspaces over it.
        /// Returns false if no plausible slip was available for this character.
        ///
        /// The whole sequence ignores Abort once it has started: stopping half way would
        /// leave the typo sitting in the field, which is worse than typing a little longer.
        /// </summary>
        bool TypeMistake(string text, int at)
        {
            char wrong = RhythmProfile.Neighbour(text[at]);
            if (wrong == '\0') return false;

            // How much of what follows gets typed before the mistake is noticed.
            int extra = RhythmProfile.NoticeAfterChars() - 1;
            int avail = 0;
            while (avail < extra && at + 1 + avail < text.Length && IsPlain(text[at + 1 + avail])) avail++;

            char after = (avail > 0) ? text[at + 1] : '\0';
            EmitChar(wrong, after, true);

            for (int j = 0; j < avail; j++)
            {
                char n = (j + 1 < avail) ? text[at + 2 + j] : '\0';
                EmitChar(text[at + 1 + j], n, true);
            }

            // Notice it, then take it out. Ctrl+Backspace occasionally, as measured, but
            // only when the whole run being removed is a single unbroken word.
            if (!Sleep(RhythmProfile.BaseGapMs() * _scale * 2.0, true)) return false;

            if (_shiftHeld) ReleaseShiftNow(true);

            bool wholeWord = (avail + 1) > 2 && WordRun(text, at, avail);
            if (wholeWord && RhythmProfile.Chance(RhythmProfile.CtrlBackspaceShare))
            {
                EmitCtrlBackspace();
            }
            else
            {
                for (int j = 0; j <= avail; j++) EmitBackspace();
            }

            return true;
        }

        /// <summary>True if the characters about to be removed form one unbroken word.</summary>
        static bool WordRun(string text, int at, int avail)
        {
            for (int j = 0; j <= avail; j++)
            {
                int k = at + j;
                if (k >= text.Length) return false;
                if (!char.IsLetterOrDigit(text[k])) return false;
            }
            return at == 0 || !char.IsLetterOrDigit(text[at - 1]);
        }

        void EmitBackspace()
        {
            EmitKey(NativeMethods.VK_BACK, false, false, RhythmProfile.BurstGapMs() * _scale, true);
        }

        void EmitCtrlBackspace()
        {
            Send(MakeVirtualKey(NativeMethods.VK_LCONTROL, false, false, _layout));
            Sleep(40 * _scale, true);
            EmitKey(NativeMethods.VK_BACK, false, false, RhythmProfile.BurstGapMs() * _scale, true);
            FlushUntil(double.MaxValue, true);
            Send(MakeVirtualKey(NativeMethods.VK_LCONTROL, true, false, _layout));
            Sleep(60 * _scale, true);
        }

        static int NextMistakeInterval()
        {
            // Mistakes arrive at a roughly constant rate, so the gap between them is
            // exponential around the measured mean of one per 46 characters.
            double u = 1.0 - (RhythmProfile.Next(10000) / 10000.0);
            int n = (int)(-RhythmProfile.CharsPerMistake * Math.Log(u));
            if (n < 8) n = 8;
            if (n > 400) n = 400;
            return n;
        }

        static bool IsPlain(char c)
        {
            return c != '\r' && c != '\n' && c != '\t' && !char.IsControl(c);
        }

        // ---- scheduling -----------------------------------------------------

        double Now
        {
            get { return _clock.Elapsed.TotalMilliseconds; }
        }

        void Schedule(double at, ushort vk, NativeMethods.INPUT input)
        {
            Release r = new Release();
            r.At = at;
            r.Vk = vk;
            r.Input = input;

            int i = 0;
            while (i < _pending.Count && _pending[i].At <= at) i++;
            _pending.Insert(i, r);
        }

        /// <summary>Sends every release due at or before <paramref name="t"/>, waiting for each.</summary>
        bool FlushUntil(double t, bool force)
        {
            while (_pending.Count > 0 && _pending[0].At <= t)
            {
                Release r = _pending[0];
                if (!SleepUntil(r.At, force)) return false;
                _pending.RemoveAt(0);
                Send(r.Input);
            }
            return true;
        }

        /// <summary>Releases one key immediately, early, because it is about to be pressed again.</summary>
        void ReleaseKeyNow(ushort vk)
        {
            for (int i = 0; i < _pending.Count; i++)
            {
                if (_pending[i].Vk != vk) continue;
                NativeMethods.INPUT input = _pending[i].Input;
                _pending.RemoveAt(i);
                Send(input);
                return;
            }
        }

        void CancelPendingShiftRelease()
        {
            for (int i = 0; i < _pending.Count; i++)
            {
                if (_pending[i].Vk != NativeMethods.VK_LSHIFT) continue;
                _pending.RemoveAt(i);
                _shiftHeld = true;      // it never actually came up
                return;
            }
        }

        void ReleaseShiftNow(bool force)
        {
            ReleaseKeyNow(NativeMethods.VK_LSHIFT);
            if (_shiftHeld)
            {
                Send(MakeVirtualKey(NativeMethods.VK_LSHIFT, true, false, _layout));
                _shiftHeld = false;
            }
            Sleep(30 * _scale, force);
        }

        /// <summary>Last resort on the way out: everything still down comes up at once.</summary>
        void ReleaseEverything()
        {
            try
            {
                for (int i = 0; i < _pending.Count; i++) Send(_pending[i].Input);
                _pending.Clear();

                if (_shiftHeld)
                {
                    Send(MakeVirtualKey(NativeMethods.VK_LSHIFT, true, false, IntPtr.Zero));
                    _shiftHeld = false;
                }
            }
            catch (Exception) { }
        }

        // ---- rhythm ----------------------------------------------------------

        /// <summary>
        /// The measured profile is scaled so its typical gap lands in the middle of the
        /// delay range the user chose, which keeps that control meaningful without
        /// flattening the shape of the distribution.
        /// </summary>
        static double SpeedScale(AppSettings s)
        {
            if (!s.HumanRhythm) return 1.0;

            double midpoint = (s.MinKeyDelayMs + s.MaxKeyDelayMs) / 2.0;
            if (midpoint <= 0) return 0.05;

            double scale = midpoint / RhythmProfile.BaseGapMedianMs;
            if (scale < 0.05) scale = 0.05;
            if (scale > 10.0) scale = 10.0;
            return scale;
        }

        double DwellMs()
        {
            if (!_s.HumanRhythm)
            {
                return (_s.KeyHoldMaxMs > _s.KeyHoldMinMs)
                    ? RhythmProfile.Next(_s.KeyHoldMaxMs - _s.KeyHoldMinMs + 1) + _s.KeyHoldMinMs
                    : _s.KeyHoldMinMs;
            }
            return RhythmProfile.DwellMs() * _scale;
        }

        /// <summary>
        /// The gap from this key going down to the next one going down. Which multiplier
        /// applies is decided in the same order the measurements were bucketed, so the
        /// numbers mean what they did when they came off the recordings.
        /// </summary>
        double GapMs(char justTyped, char next)
        {
            if (!_s.HumanRhythm)
            {
                int lo = _s.MinKeyDelayMs, hi = _s.MaxKeyDelayMs;
                return (hi > lo) ? lo + RhythmProfile.Next(hi - lo + 1) : lo;
            }

            double ms = RhythmProfile.BaseGapMs() * _scale;
            if (next == '\0') return ms;

            double factor;
            if (next == ' ')                                   factor = RhythmProfile.SpaceFactor;
            else if (justTyped == ' ')                         factor = RhythmProfile.WordStartFactor;
            else if (IsSentencePunctuation(justTyped))         factor = RhythmProfile.PunctuationFactor;
            else if (char.IsDigit(next) && !char.IsDigit(justTyped)) factor = RhythmProfile.DigitFactor;
            else if (NeedsShift(next))                         factor = RhythmProfile.CapitalFactor;
            else if (SameHand(justTyped, next))                factor = RhythmProfile.SameHandFactor;
            else                                               factor = RhythmProfile.AltHandFactor;

            ms *= factor;

            if (ms < 1) ms = 1;
            if (ms > 2000) ms = 2000;      // never stall outright
            return ms;
        }

        bool NeedsShift(char c)
        {
            ushort vk;
            bool shift;
            if (!TryMapToKey(c, _layout, out vk, out shift)) return false;
            return shift;
        }

        static bool IsSentencePunctuation(char c)
        {
            return c == '.' || c == ',' || c == ';' || c == ':' || c == '!' || c == '?';
        }

        const string LeftHandKeys = "qwertasdfgzxcvb12345`";

        /// <summary>
        /// Which hand strikes a key, so the plain case can be split into the same hand
        /// twice (slower) and the hands alternating (quicker). Applied only in the plain
        /// case, so it never compounds with the other multipliers.
        /// </summary>
        static bool SameHand(char a, char b)
        {
            if (a == '\0' || b == '\0' || a == ' ' || b == ' ') return false;

            bool aLeft = LeftHandKeys.IndexOf(char.ToLowerInvariant(a)) >= 0;
            bool bLeft = LeftHandKeys.IndexOf(char.ToLowerInvariant(b)) >= 0;
            return aLeft == bLeft;
        }

        // ---- key mapping --------------------------------------------------

        /// <summary>The keyboard layout of the window being typed into, not ours.</summary>
        static IntPtr LayoutOf(IntPtr window)
        {
            try
            {
                uint pid;
                uint tid = NativeMethods.GetWindowThreadProcessId(window, out pid);
                IntPtr hkl = NativeMethods.GetKeyboardLayout(tid);
                if (hkl != IntPtr.Zero) return hkl;
            }
            catch (Exception)
            {
                // Fall through to our own layout.
            }

            return NativeMethods.GetKeyboardLayout(0);
        }

        /// <summary>
        /// Finds the key that types <paramref name="c"/> on this layout. Ctrl/Alt (AltGr)
        /// combinations are refused: synthesising them is unreliable and risks firing a
        /// shortcut in the target, so those characters go out as packets instead.
        /// </summary>
        static bool TryMapToKey(char c, IntPtr layout, out ushort vk, out bool needShift)
        {
            vk = 0;
            needShift = false;

            short scan = NativeMethods.VkKeyScanEx(c, layout);
            if (scan == -1) return false;

            int mods = (scan >> 8) & 0xFF;
            if ((mods & 0x06) != 0) return false;    // needs Ctrl and/or Alt

            vk = (ushort)(scan & 0xFF);
            if (vk == 0) return false;

            needShift = (mods & 0x01) != 0;
            return true;
        }

        // ---- injection ----------------------------------------------------

        void Send(NativeMethods.INPUT input)
        {
            NativeMethods.INPUT[] one = new NativeMethods.INPUT[1];
            one[0] = input;
            if (NativeMethods.SendInput(1, one, InputSize) != 1) _sendFailed = true;
        }

        static NativeMethods.INPUT MakeUnicode(char c, bool keyUp)
        {
            NativeMethods.INPUT input = new NativeMethods.INPUT();
            input.type = NativeMethods.INPUT_KEYBOARD;
            input.U.ki.wVk = 0;                 // must be 0 for KEYEVENTF_UNICODE
            input.U.ki.wScan = c;
            input.U.ki.dwFlags = NativeMethods.KEYEVENTF_UNICODE |
                                 (keyUp ? NativeMethods.KEYEVENTF_KEYUP : 0);
            input.U.ki.time = 0;
            input.U.ki.dwExtraInfo = IntPtr.Zero;
            return input;
        }

        static NativeMethods.INPUT MakeVirtualKey(ushort vk, bool keyUp, bool extended, IntPtr layout)
        {
            NativeMethods.INPUT input = new NativeMethods.INPUT();
            input.type = NativeMethods.INPUT_KEYBOARD;
            input.U.ki.wVk = vk;
            input.U.ki.wScan = (ushort)((layout == IntPtr.Zero)
                ? NativeMethods.MapVirtualKey(vk, 0)
                : NativeMethods.MapVirtualKeyEx(vk, 0, layout));
            input.U.ki.dwFlags = (keyUp ? NativeMethods.KEYEVENTF_KEYUP : 0) |
                                 (extended ? NativeMethods.KEYEVENTF_EXTENDEDKEY : 0);
            input.U.ki.time = 0;
            input.U.ki.dwExtraInfo = IntPtr.Zero;
            return input;
        }

        // ---- mouse watch -----------------------------------------------------

        static readonly int[] MouseButtons =
        {
            NativeMethods.VK_LBUTTON, NativeMethods.VK_RBUTTON, NativeMethods.VK_MBUTTON,
            NativeMethods.VK_XBUTTON1, NativeMethods.VK_XBUTTON2
        };

        /// <summary>
        /// How often the buttons are actually read. The pacing loop below spins at
        /// microsecond granularity near the end of a wait, and five calls into user32 on
        /// every turn of it would cost more than the wait itself.
        /// </summary>
        const double MousePollMs = 5.0;

        void BeginMouseWatch()
        {
            if (!_s.StopOnMouseClick) return;

            // GetAsyncKeyState latches "went down since the last call" in its low bit. Read
            // every button once here so that a click made before the run, including the one
            // that may have started it from the tray, is not counted against it.
            for (int i = 0; i < MouseButtons.Length; i++)
            {
                NativeMethods.GetAsyncKeyState(MouseButtons[i]);
            }

            _lastMousePoll = Now;
            _watchMouse = true;
        }

        /// <summary>
        /// True once any mouse button has gone down. Latches: once it has happened the run
        /// is over, and asking again keeps saying so.
        ///
        /// Movement is deliberately not watched. Nudging the mouse, or a remote session
        /// syncing the cursor, is not a request to stop, and a run that ends because the
        /// pointer drifted is worse than one that keeps going.
        /// </summary>
        bool MouseClicked()
        {
            if (_mouseClicked) return true;
            if (!_watchMouse) return false;

            double now = Now;
            if (now - _lastMousePoll < MousePollMs) return false;
            _lastMousePoll = now;

            for (int i = 0; i < MouseButtons.Length; i++)
            {
                // 0x8000 is down right now; 0x0001 is a press since the last read, which
                // catches a click shorter than the interval between polls.
                if ((NativeMethods.GetAsyncKeyState(MouseButtons[i]) & 0x8001) != 0)
                {
                    _mouseClicked = true;
                    return true;
                }
            }

            return false;
        }

        // ---- pacing ---------------------------------------------------------

        bool SleepUntil(double t, bool force)
        {
            return Sleep(t - Now, force);
        }

        /// <summary>
        /// Sleeps while staying responsive to the stop hotkey and to mouse clicks.
        /// Thread.Sleep granularity is ~15 ms, so short waits finish on a spin to keep fast
        /// typing speeds honest. With <paramref name="force"/> the wait completes
        /// regardless, which is used to keep a correction atomic.
        /// </summary>
        bool Sleep(double ms, bool force)
        {
            if (!force && Interrupted()) return false;
            if (ms <= 0) return true;

            Stopwatch sw = Stopwatch.StartNew();
            while (true)
            {
                if (!force && Interrupted()) return false;

                double left = ms - sw.Elapsed.TotalMilliseconds;
                if (left <= 0) return true;

                // Sleep only where the remaining time comfortably exceeds the timer's
                // accuracy; finish on a spin so the gap is the one that was asked for.
                if (left > 25) Thread.Sleep(10);
                else if (left > 5) Thread.Sleep(1);
                else Thread.SpinWait(200);
            }
        }

        /// <summary>Anything that should end the run: the stop hotkey, or a mouse click.</summary>
        bool Interrupted()
        {
            if (_abort) return true;
            return MouseClicked();
        }

        static void WaitForModifiersReleased(int timeoutMs)
        {
            Stopwatch sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (!NativeMethods.IsKeyDown(NativeMethods.VK_CONTROL) &&
                    !NativeMethods.IsKeyDown(NativeMethods.VK_MENU) &&
                    !NativeMethods.IsKeyDown(NativeMethods.VK_SHIFT) &&
                    !NativeMethods.IsKeyDown(NativeMethods.VK_LWIN) &&
                    !NativeMethods.IsKeyDown(NativeMethods.VK_RWIN))
                {
                    return;
                }
                Thread.Sleep(15);
            }
        }

        /// <summary>
        /// Synthesises key-up for any modifier still held. Win is deliberately excluded:
        /// a synthetic Win release can pop the Start menu.
        /// </summary>
        static void ForceReleaseModifiers()
        {
            int[] mods = { NativeMethods.VK_CONTROL, NativeMethods.VK_MENU, NativeMethods.VK_SHIFT };

            for (int i = 0; i < mods.Length; i++)
            {
                if (!NativeMethods.IsKeyDown(mods[i])) continue;

                NativeMethods.INPUT[] up = new NativeMethods.INPUT[1];
                up[0] = MakeVirtualKey((ushort)mods[i], true, false, IntPtr.Zero);
                NativeMethods.SendInput(1, up, InputSize);
            }
        }
    }
}
