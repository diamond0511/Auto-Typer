<#
  Aggregates rhythm-tracker recordings into measured typing statistics, and writes a
  profile file the typing engine can be driven from.

    .\analyze-rhythm.ps1                    # analyse every .json beside this script
    .\analyze-rhythm.ps1 -Path <dir>
    .\analyze-rhythm.ps1 -Digraphs          # also dump the per-pair timing table

  Everything here is measured. Nothing is assumed about the shape of a distribution:
  percentiles are reported so the engine can sample from the empirical quantiles.
#>
param(
  [string]$Path,
  [string]$OutProfile,
  [switch]$Digraphs,
  [switch]$NoFilter,          # keep flushed events, to see how bad the contamination is
  [double]$FlushMs = 2.0      # two key events closer than this did not happen that way
)

$ErrorActionPreference = 'Stop'
if (-not $Path)       { $Path = $PSScriptRoot }
if (-not $OutProfile) { $OutProfile = Join-Path $PSScriptRoot 'rhythm-profile.json' }

$MOD_SHIFT = 1; $MOD_CTRL = 2

$MODIFIERS = @{}
'ShiftLeft ShiftRight ControlLeft ControlRight AltLeft AltRight MetaLeft MetaRight CapsLock'.Split(' ') |
  ForEach-Object { $MODIFIERS[$_] = $true }

$LEFT = @{}
('KeyQ KeyW KeyE KeyR KeyT KeyA KeyS KeyD KeyF KeyG KeyZ KeyX KeyC KeyV KeyB ' +
 'Digit1 Digit2 Digit3 Digit4 Digit5 Backquote Tab').Split(' ') | ForEach-Object { $LEFT[$_] = $true }

$SENTENCE_PUNCT = '.,;:!?'

# ---------------------------------------------------------------- helpers

function Pct([double[]]$sorted, [double]$p) {
  $i = [int][Math]::Round(($sorted.Length - 1) * $p)
  if ($i -lt 0) { $i = 0 }
  if ($i -ge $sorted.Length) { $i = $sorted.Length - 1 }
  return $sorted[$i]
}

function Summarize($list) {
  if ($null -eq $list) { return $null }
  $a = [double[]]@($list)
  if ($a.Length -eq 0) { return $null }
  [Array]::Sort($a)
  $sum = 0.0; foreach ($x in $a) { $sum += $x }
  return [pscustomobject]@{
    n      = $a.Length
    mean   = [math]::Round($sum / $a.Length, 1)
    min    = [math]::Round($a[0], 1)
    p05    = [math]::Round((Pct $a 0.05), 1)
    p10    = [math]::Round((Pct $a 0.10), 1)
    p25    = [math]::Round((Pct $a 0.25), 1)
    median = [math]::Round((Pct $a 0.50), 1)
    p75    = [math]::Round((Pct $a 0.75), 1)
    p90    = [math]::Round((Pct $a 0.90), 1)
    p95    = [math]::Round((Pct $a 0.95), 1)
    p99    = [math]::Round((Pct $a 0.99), 1)
    max    = [math]::Round($a[$a.Length - 1], 1)
  }
}

function Line($label, $s, $width = 30) {
  if ($null -eq $s) { return ('  {0}  no data' -f $label.PadRight($width)) }
  return ('  {0}{1,7}{2,9}{3,8}{4,9}{5,9}{6,9}' -f $label.PadRight($width),
          $s.n, $s.p10, $s.p25, $s.median, $s.p75, $s.p90)
}

function Header($title) {
  ''
  '=' * 92
  "  $title"
  '=' * 92
}

function ColHead($what = 'measurement') {
  ('  {0}{1,7}{2,9}{3,8}{4,9}{5,9}{6,9}' -f $what.PadRight(30), 'n', 'p10', 'p25', 'median', 'p75', 'p90')
  '  ' + ('-' * 88)
}

function NewBag {
  return [pscustomobject]@{
    hold          = New-Object System.Collections.ArrayList   # dwell, all keys
    holdLetter    = New-Object System.Collections.ArrayList
    holdSpace     = New-Object System.Collections.ArrayList
    holdShift     = New-Object System.Collections.ArrayList
    holdBack      = New-Object System.Collections.ArrayList
    gap           = New-Object System.Collections.ArrayList   # down->down, all non-modifier
    gapClean      = New-Object System.Collections.ArrayList   # both keys correct, nothing between
    gapBase       = New-Object System.Collections.ArrayList   # plain lowercase letter after letter
    gapSpace      = New-Object System.Collections.ArrayList   # before a space
    gapAfterSpace = New-Object System.Collections.ArrayList   # first letter of a word
    gapShifted    = New-Object System.Collections.ArrayList   # before a shifted character
    gapDigit      = New-Object System.Collections.ArrayList   # entering a digit run
    gapAfterPunct = New-Object System.Collections.ArrayList   # after . , ; : ! ?
    gapSameHand   = New-Object System.Collections.ArrayList
    gapAltHand    = New-Object System.Collections.ArrayList
    gapBaseSame   = New-Object System.Collections.ArrayList   # same hand, within the plain case
    gapBaseAlt    = New-Object System.Collections.ArrayList
    overlap       = New-Object System.Collections.ArrayList   # prev up -> next down, negative = rollover
    shiftLead     = New-Object System.Collections.ArrayList   # shift down -> letter down
    shiftTrail    = New-Object System.Collections.ArrayList   # letter up -> shift up
    shiftRun      = New-Object System.Collections.ArrayList   # chars typed per shift hold
    errLatency    = New-Object System.Collections.ArrayList   # chars typed before noticing
    errLatencyMs  = New-Object System.Collections.ArrayList
    burstLen      = New-Object System.Collections.ArrayList   # backspaces per burst
    burstGap      = New-Object System.Collections.ArrayList   # gap inside a backspace burst
    pauseGap      = New-Object System.Collections.ArrayList
    digraph       = @{}
    counts        = @{}
  }
}

function Bump($bag, $name, $by = 1) {
  if (-not $bag.counts.ContainsKey($name)) { $bag.counts[$name] = 0 }
  $bag.counts[$name] += $by
}

function MergeBag($dst, $src) {
  foreach ($p in $src.PSObject.Properties) {
    if ($p.Name -eq 'digraph') {
      foreach ($k in $src.digraph.Keys) {
        if (-not $dst.digraph.ContainsKey($k)) { $dst.digraph[$k] = New-Object System.Collections.ArrayList }
        $dst.digraph[$k].AddRange($src.digraph[$k])
      }
    } elseif ($p.Name -eq 'counts') {
      foreach ($k in $src.counts.Keys) { Bump $dst $k $src.counts[$k] }
    } else {
      $dst.($p.Name).AddRange($src.($p.Name))
    }
  }
}

# ---------------------------------------------------------------- one session

function Analyze($json) {
  $bag = NewBag
  $chars = 0; $secs = 0.0

  foreach ($prompt in $json.prompts) {
    if ($prompt.skipped) { continue }
    $target = [string]$prompt.target
    $secs += [double]$prompt.durationMs / 1000.0
    $chars += [int]$prompt.final.Length

    # --- pass 0: find flushed events ------------------------------------
    # Two key events less than $FlushMs apart cannot be two separate physical
    # key movements. They mean the browser's main thread stalled and then
    # dispatched a queue of events, all stamped at dispatch time. Anything
    # measured across such a pair is the stall, not the typist, so both the
    # follower and the event it collapsed onto are discarded.
    # Version 2 recordings carry `ht` (event.timeStamp), taken upstream of the
    # browser's input queue. Prefer it; version 1 only has the handler clock.
    $evs = @($prompt.events)
    $tm = New-Object 'double[]' $evs.Count
    $hasHt = ($evs.Count -gt 0 -and $null -ne $evs[0].ht)
    for ($i = 0; $i -lt $evs.Count; $i++) {
      if ($hasHt) { $tm[$i] = [double]$evs[$i].ht } else { $tm[$i] = [double]$evs[$i].t }
    }
    if ($hasHt) { Bump $bag 'hardwareClock' $evs.Count }

    $bad = New-Object 'bool[]' $evs.Count
    for ($i = 1; $i -lt $evs.Count; $i++) {
      if (($tm[$i] - $tm[$i - 1]) -lt $FlushMs) { $bad[$i] = $true; $bad[$i - 1] = $true }
    }
    Bump $bag 'eventsTotal' $evs.Count
    if (-not $NoFilter) {
      $nb = 0; foreach ($b in $bad) { if ($b) { $nb++ } }
      Bump $bag 'eventsFlushed' $nb
    } else {
      for ($i = 0; $i -lt $bad.Length; $i++) { $bad[$i] = $false }
    }

    # --- pass 1: pair each key-down with its key-up ---------------------
    $strokes = New-Object System.Collections.ArrayList
    $pending = @{}
    for ($i = 0; $i -lt $evs.Count; $i++) {
      $e = $evs[$i]
      if ($e.d -eq 1) {
        $s = [pscustomobject]@{
          t = $tm[$i]; code = [string]$e.code; key = [string]$e.key
          m = [int]$e.m; r = [int]$e.r; sel = [int]$e.sel
          hold = -1.0; repeated = $false; bad = $bad[$i]
        }
        $null = $strokes.Add($s)
        if (-not $pending.ContainsKey($s.code)) { $pending[$s.code] = New-Object System.Collections.Queue }
        if ($e.r -eq 1 -and $pending[$s.code].Count -gt 0) {
          # a held key: the first down owns the up, and its dwell is not a tap
          $strokes[$pending[$s.code].Peek()].repeated = $true
          Bump $bag 'autoRepeat'
        }
        $pending[$s.code].Enqueue($strokes.Count - 1)
      } else {
        if ($pending.ContainsKey($e.code) -and $pending[$e.code].Count -gt 0) {
          $k = $pending[$e.code].Dequeue()
          if (-not ($bad[$i] -or $strokes[$k].bad)) { $strokes[$k].hold = $tm[$i] - $strokes[$k].t }
        }
      }
    }

    # --- pass 2: holds --------------------------------------------------
    foreach ($s in $strokes) {
      if ($s.hold -lt 0 -or $s.repeated -or $s.r -eq 1) { continue }
      $null = $bag.hold.Add($s.hold)
      if ($MODIFIERS.ContainsKey($s.code)) {
        if ($s.code -like 'Shift*') { $null = $bag.holdShift.Add($s.hold) }
      } elseif ($s.code -eq 'Space')     { $null = $bag.holdSpace.Add($s.hold) }
        elseif ($s.code -eq 'Backspace') { $null = $bag.holdBack.Add($s.hold) }
        elseif ($s.code -like 'Key*')    { $null = $bag.holdLetter.Add($s.hold) }
    }

    # --- pass 3: shift runs --------------------------------------------
    $printableIdx = @()
    for ($i = 0; $i -lt $strokes.Count; $i++) {
      $s = $strokes[$i]
      if (-not $MODIFIERS.ContainsKey($s.code) -and $s.key.Length -eq 1) { $printableIdx += $i }
    }
    foreach ($s in $strokes) {
      if (-not ($s.code -like 'Shift*') -or $s.hold -lt 0) { continue }
      $end = $s.t + $s.hold
      $inRun = @()
      foreach ($i in $printableIdx) {
        $p = $strokes[$i]
        if ($p.t -ge $s.t -and $p.t -le $end) { $inRun += $i }
      }
      if ($inRun.Count -eq 0) { Bump $bag 'shiftNoChar'; continue }
      $null = $bag.shiftRun.Add($inRun.Count)
      $first = $strokes[$inRun[0]]
      # the first keystroke of a prompt carries reading time, not a reach
      if ($inRun[0] -gt 0 -and -not $first.bad) { $null = $bag.shiftLead.Add($first.t - $s.t) }
      $last = $strokes[$inRun[$inRun.Count - 1]]
      if ($last.hold -ge 0 -and -not $last.bad) { $null = $bag.shiftTrail.Add($end - ($last.t + $last.hold)) }
    }

    # --- pass 4: the sequence ------------------------------------------
    $prev = $null            # previous non-modifier stroke
    $prevChar = ''           # previous character actually typed
    $prevOk = $false
    $typedSincePrev = 0
    $openErrors = New-Object System.Collections.ArrayList
    $printableSoFar = 0
    $burst = $null

    for ($i = 0; $i -lt $strokes.Count; $i++) {
      $s = $strokes[$i]
      if ($MODIFIERS.ContainsKey($s.code)) { continue }

      $isPrintable = ($s.key.Length -eq 1)
      $isBack = ($s.code -eq 'Backspace')
      $isDel  = ($s.code -eq 'Delete')
      $ctrl   = (($s.m -band $MOD_CTRL) -ne 0)

      # ---- corrections -------------------------------------------------
      if ($isBack -or $isDel) {
        if ($isBack -and $ctrl)      { Bump $bag 'ctrlBackspace' }
        elseif ($isBack)             { Bump $bag 'backspace' }
        elseif ($isDel -and $ctrl)   { Bump $bag 'ctrlDelete' }
        else                         { Bump $bag 'delete' }

        if ($null -ne $burst -and ($s.t - $burst.last) -lt 500) {
          $burst.n++
          $null = $bag.burstGap.Add($s.t - $burst.last)
          $burst.last = $s.t
        } else {
          if ($null -ne $burst) { $null = $bag.burstLen.Add($burst.n) }
          $burst = [pscustomobject]@{ n = 1; last = $s.t; start = $s.t }
          # attribute this burst to the slip that is still outstanding. One slip
          # misaligns everything after it, so the whole run of wrong characters
          # belongs to a single mistake and a single correction.
          if ($openErrors.Count -gt 0) {
            $err = $openErrors[0]
            $null = $bag.errLatency.Add($printableSoFar - $err.at)
            if (-not $s.bad) { $null = $bag.errLatencyMs.Add($s.t - $err.t) }
            Bump $bag 'slipsCorrected'
            $openErrors.Clear()
          } else {
            Bump $bag 'discretionaryFix'   # nothing was wrong; rewriting by choice
          }
        }
      } elseif ($null -ne $burst) {
        $null = $bag.burstLen.Add($burst.n); $burst = $null
      }

      # ---- gaps --------------------------------------------------------
      $timeOk = ($null -ne $prev -and -not $s.bad -and -not $prev.bad)
      if ($timeOk) {
        $g = $s.t - $prev.t
        $null = $bag.gap.Add($g)
        if ($prev.hold -ge 0) { $null = $bag.overlap.Add($s.t - ($prev.t + $prev.hold)) }
        if ($LEFT.ContainsKey($s.code) -eq $LEFT.ContainsKey($prev.code)) {
          $null = $bag.gapSameHand.Add($g)
        } else {
          $null = $bag.gapAltHand.Add($g)
        }
      }

      if ($isPrintable) {
        $pos = $s.sel
        $ok = ($pos -ge 0 -and $pos -lt $target.Length -and $target[$pos] -eq $s.key[0])
        $c = $s.key[0]

        if (-not $ok) {
          Bump $bag 'wrongChars'
          # only the first wrong character is a mistake; the rest of the run is
          # the same mistake still showing, because the text is now out of step
          if ($openErrors.Count -eq 0) {
            Bump $bag 'slips'
            $null = $openErrors.Add([pscustomobject]@{ at = $printableSoFar; t = $s.t })
          }
        }
        Bump $bag 'chars'

        # the clean distribution: ordinary forward typing, no correction involved
        if ($timeOk -and $prevOk -and $ok -and $prev.key.Length -eq 1) {
          $g = $s.t - $prev.t
          $null = $bag.gapClean.Add($g)

          $shifted = (($s.m -band $MOD_SHIFT) -ne 0)
          if ($c -eq ' ')                                   { $null = $bag.gapSpace.Add($g) }
          elseif ($prevChar -eq ' ')                        { $null = $bag.gapAfterSpace.Add($g) }
          elseif ($SENTENCE_PUNCT.IndexOf($prevChar) -ge 0) { $null = $bag.gapAfterPunct.Add($g) }
          elseif ([char]::IsDigit($c) -and -not [char]::IsDigit($prevChar)) { $null = $bag.gapDigit.Add($g) }
          elseif ($shifted)                                 { $null = $bag.gapShifted.Add($g) }
          elseif ([char]::IsLower($c) -and [char]::IsLetter($prevChar)) {
            $null = $bag.gapBase.Add($g)
            # the same split again, but measured inside the plain case, which is the
            # only place the engine applies it
            if ($LEFT.ContainsKey($s.code) -eq $LEFT.ContainsKey($prev.code)) {
              $null = $bag.gapBaseSame.Add($g)
            } else {
              $null = $bag.gapBaseAlt.Add($g)
            }
          }

          $k = "$prevChar$c"
          if (-not $bag.digraph.ContainsKey($k)) { $bag.digraph[$k] = New-Object System.Collections.ArrayList }
          $null = $bag.digraph[$k].Add($g)
        }

        $prevChar = $c
        $prevOk = $ok
        $printableSoFar++
      } else {
        $prevOk = $false
        $prevChar = ''
      }

      $prev = $s
    }

    if ($null -ne $burst) { $null = $bag.burstLen.Add($burst.n) }
    Bump $bag 'slipsLeft' $openErrors.Count
    if (-not $prompt.exact) { Bump $bag 'promptsNotExact' }
  }

  # pauses are relative to this person's own tempo
  $cleanArr = [double[]]@($bag.gapClean)
  if ($cleanArr.Length -gt 0) {
    [Array]::Sort($cleanArr)
    $med = Pct $cleanArr 0.50
    foreach ($g in $bag.gap) { if ($g -gt 3 * $med) { $null = $bag.pauseGap.Add($g) } }
  }

  Bump $bag 'seconds' ([int][math]::Round($secs))
  Bump $bag 'finalChars' $chars
  return $bag
}

# ---------------------------------------------------------------- load

$files = @(Get-ChildItem (Join-Path $Path '*.json') |
           Where-Object { $_.Name -like 'rhythm-*' -and $_.Name -ne 'rhythm-profile.json' })
if ($files.Count -eq 0) { throw "no rhythm-*.json recordings found in $Path" }

$sessions = @()
foreach ($f in $files) {
  $json = Get-Content $f.FullName -Raw | ConvertFrom-Json
  $bag = Analyze $json
  # "Richard3" and "richard1" are the same person typing twice
  $person = ([regex]::Match($json.participant, '^[A-Za-z]+')).Value.ToLower()
  if (-not $person) { $person = $json.participant }
  $sessions += [pscustomobject]@{
    file = $f.Name; label = $json.participant; person = $person
    style = $json.style; bag = $bag
    promptSet = if ($json.prompts[0].target -like 'The quick brown*') { 'mixed' } else { 'prose' }
  }
}

$all = NewBag
$byPerson = @{}
foreach ($s in $sessions) {
  MergeBag $all $s.bag
  if (-not $byPerson.ContainsKey($s.person)) { $byPerson[$s.person] = NewBag }
  MergeBag $byPerson[$s.person] $s.bag
}

# ---------------------------------------------------------------- report

function Cnt($bag, $k) { if ($bag.counts.ContainsKey($k)) { return $bag.counts[$k] } else { return 0 } }

Header 'RECORDINGS'
('  {0}{1,-9}{2,-7}{3,7}{4,6}{5,7}{6,7}{7,7}{8,10}{9,10}' -f 'file'.PadRight(15), 'person', 'text',
   'chars', 'secs', 'wpm', 'slips', 'fixes', 'clock', 'unusable')
'  ' + ('-' * 88)
foreach ($s in $sessions) {
  $b = $s.bag
  $secs = Cnt $b 'seconds'; $ch = Cnt $b 'chars'
  $wpm = if ($secs -gt 0) { [math]::Round(($ch / 5.0) / ($secs / 60.0), 1) } else { 0 }
  $fl = Cnt $b 'eventsFlushed'; $tot = Cnt $b 'eventsTotal'
  $clock = if ((Cnt $b 'hardwareClock') -gt 0) { 'event' } else { 'handler' }
  ('  {0}{1,-9}{2,-7}{3,7}{4,6}{5,7}{6,7}{7,7}{8,10}{9,9}%' -f $s.label.PadRight(15), $s.person, $s.promptSet,
     $ch, $secs, $wpm, (Cnt $b 'slips'),
     ((Cnt $b 'backspace') + (Cnt $b 'ctrlBackspace') + (Cnt $b 'delete') + (Cnt $b 'ctrlDelete')),
     $clock, [math]::Round(100.0 * $fl / [Math]::Max(1, $tot), 1))
}
''
'  "unusable" is the share of key events that arrived less than ' + $FlushMs + ' ms after the previous one.'
'  Two physical key movements cannot be that close, so those events carry the timestamp of a'
'  stalled browser flushing its queue, not of the keystroke. They are discarded before any'
'  timing below is computed. Run with -NoFilter to see what they do to the numbers.'

Header 'DWELL TIME (ms a key is held down)'
ColHead
Line 'all keys'      (Summarize $all.hold)
Line 'letters'       (Summarize $all.holdLetter)
Line 'space'         (Summarize $all.holdSpace)
Line 'shift'         (Summarize $all.holdShift)
Line 'backspace'     (Summarize $all.holdBack)

Header 'GAP BETWEEN KEYSTROKES (ms, key-down to key-down)'
ColHead
Line 'every keystroke'          (Summarize $all.gap)
Line 'clean forward typing'     (Summarize $all.gapClean)
Line '  plain letter run'       (Summarize $all.gapBase)
Line '  before a space'         (Summarize $all.gapSpace)
Line '  first letter of a word' (Summarize $all.gapAfterSpace)
Line '  before a capital'       (Summarize $all.gapShifted)
Line '  entering digits'        (Summarize $all.gapDigit)
Line '  after . , ; : ! ?'      (Summarize $all.gapAfterPunct)
Line 'same hand twice'          (Summarize $all.gapSameHand)
Line 'hands alternating'        (Summarize $all.gapAltHand)
Line '  same hand, plain case'  (Summarize $all.gapBaseSame)
Line '  alternating, plain case' (Summarize $all.gapBaseAlt)
Line 'pauses (> 3x own median)' (Summarize $all.pauseGap)

$base = Summarize $all.gapBase
if ($null -ne $base -and $base.median -gt 0) {
  ''
  '  multiplier against a plain letter run (engine currently assumes 1.00 / 1.30 / 1.20 / 1.20 / 1.55):'
  foreach ($row in @(
    @('plain letter run',       (Summarize $all.gapBase),       '1.00'),
    @('before a space',         (Summarize $all.gapSpace),      '1.30'),
    @('first letter of a word', (Summarize $all.gapAfterSpace),  '-'),
    @('before a capital',       (Summarize $all.gapShifted),    '1.20'),
    @('entering digits',        (Summarize $all.gapDigit),      '1.20'),
    @('after punctuation',      (Summarize $all.gapAfterPunct), '1.55'))) {
    if ($null -eq $row[1]) { continue }
    ('    {0}{1,8}x   (engine {2})' -f $row[0].PadRight(26),
      [math]::Round($row[1].median / $base.median, 2), $row[2])
  }
}

Header 'KEY OVERLAP (ms from one key coming up to the next going down)'
ColHead
Line 'all consecutive keys' (Summarize $all.overlap)
$ov = [double[]]@($all.overlap)
if ($ov.Length -gt 0) {
  $neg = 0; foreach ($x in $ov) { if ($x -lt 0) { $neg++ } }
  ''
  ('  rollover: {0} of {1} keystrokes ({2}%) went down before the previous key came up' -f
     $neg, $ov.Length, [math]::Round(100.0 * $neg / $ov.Length, 1))
  '  the engine never does this: it releases every key before pressing the next.'
}

Header 'SHIFT'
ColHead
Line 'shift down -> letter down' (Summarize $all.shiftLead)
Line 'letter up -> shift up'     (Summarize $all.shiftTrail)
Line 'characters per shift hold' (Summarize $all.shiftRun)

Header 'MISTAKES AND CORRECTIONS'
$ch = Cnt $all 'chars'
('  characters typed            {0,8}' -f $ch)
('  mistakes                    {0,8}   1 every {1} characters' -f (Cnt $all 'slips'),
   [math]::Round($ch / [Math]::Max(1, (Cnt $all 'slips')), 0))
('  wrong characters on screen  {0,8}   one mistake knocks the text out of step, so it shows' -f (Cnt $all 'wrongChars'))
('                                       as {0} wrong characters per mistake on average' -f
   [math]::Round(1.0 * (Cnt $all 'wrongChars') / [Math]::Max(1, (Cnt $all 'slips')), 1))
('  noticed and corrected       {0,8}   {1}% of mistakes' -f (Cnt $all 'slipsCorrected'),
   [math]::Round(100.0 * (Cnt $all 'slipsCorrected') / [Math]::Max(1, (Cnt $all 'slips')), 0))
('  left standing               {0,8}' -f (Cnt $all 'slipsLeft'))
''
('  Backspace                   {0,8}' -f (Cnt $all 'backspace'))
('  Ctrl+Backspace              {0,8}' -f (Cnt $all 'ctrlBackspace'))
('  Delete                      {0,8}' -f (Cnt $all 'delete'))
('  Ctrl+Delete                 {0,8}' -f (Cnt $all 'ctrlDelete'))
('  held down to auto-repeat    {0,8}' -f (Cnt $all 'autoRepeat'))
('  fixes with nothing wrong    {0,8}   rewrote something that was correct' -f (Cnt $all 'discretionaryFix'))
''
ColHead
Line 'chars typed before noticing' (Summarize $all.errLatency)
Line 'ms before correcting'        (Summarize $all.errLatencyMs)
Line 'backspaces per burst'        (Summarize $all.burstLen)
Line 'gap inside a burst'          (Summarize $all.burstGap)

Header 'PER PERSON'
ColHead 'person / measurement'
foreach ($p in ($byPerson.Keys | Sort-Object)) {
  $b = $byPerson[$p]
  $secs = Cnt $b 'seconds'; $c = Cnt $b 'chars'
  $wpm = if ($secs -gt 0) { [math]::Round(($c / 5.0) / ($secs / 60.0), 1) } else { 0 }
  ''
  ("  {0}   {1} wpm, {2} chars, 1 mistake every {3} chars, {4}% of events unusable" -f $p, $wpm, $c,
     [math]::Round($c / [Math]::Max(1, (Cnt $b 'slips')), 0),
     [math]::Round(100.0 * (Cnt $b 'eventsFlushed') / [Math]::Max(1, (Cnt $b 'eventsTotal')), 1))
  Line '  dwell'          (Summarize $b.hold)
  Line '  clean gap'      (Summarize $b.gapClean)
  Line '  overlap'        (Summarize $b.overlap)
  Line '  shift lead'     (Summarize $b.shiftLead)
  Line '  burst length'   (Summarize $b.burstLen)
}

$dg = @()
foreach ($k in $all.digraph.Keys) {
  if ($all.digraph[$k].Count -lt 12) { continue }
  $s = Summarize $all.digraph[$k]
  $dg += [pscustomobject]@{ pair = $k; n = $s.n; median = $s.median }
}
$dg = $dg | Sort-Object median

Header 'DIGRAPHS'
("  {0} pairs seen at least 12 times (of {1} distinct pairs)" -f $dg.Count, $all.digraph.Count)
if ($dg.Count -gt 0) {
  ''
  '  fastest 12:'
  foreach ($d in ($dg | Select-Object -First 12)) {
    ('    "{0}"{1,8} ms   n={2}' -f $d.pair.Replace(' ', '_'), $d.median, $d.n) }
  ''
  '  slowest 12:'
  foreach ($d in ($dg | Select-Object -Last 12)) {
    ('    "{0}"{1,8} ms   n={2}' -f $d.pair.Replace(' ', '_'), $d.median, $d.n) }
}
if ($Digraphs) {
  ''
  '  full table:'
  foreach ($d in $dg) { ('    "{0}"{1,8} ms   n={2}' -f $d.pair.Replace(' ', '_'), $d.median, $d.n) }
}

# ---------------------------------------------------------------- profile

function QuantArray($list) {
  $a = [double[]]@($list)
  if ($a.Length -eq 0) { return @() }
  [Array]::Sort($a)
  $out = @()
  for ($i = 0; $i -le 20; $i++) { $out += [math]::Round((Pct $a ($i / 20.0)), 1) }
  return $out
}

$profile = [pscustomobject]@{
  generated   = (Get-Date).ToString('s')
  sessions    = $sessions.Count
  people      = $byPerson.Count
  charsTyped  = Cnt $all 'chars'
  # 21 points: the 0th, 5th, 10th ... 100th percentile. Sample by drawing a
  # uniform random index and interpolating; that reproduces the measured shape
  # without assuming it is normal, triangular or anything else.
  quantiles   = [pscustomobject]@{
    dwellMs        = QuantArray $all.hold
    gapMs          = QuantArray $all.gapClean
    # the shape the multipliers below are relative to: an ordinary letter
    # following another letter, mid-word, no shift, no correction
    gapBaseMs      = QuantArray $all.gapBase
    overlapMs      = QuantArray $all.overlap
    shiftLeadMs    = QuantArray $all.shiftLead
    shiftTrailMs   = QuantArray $all.shiftTrail
    burstGapMs     = QuantArray $all.burstGap
    errLatencyChar = QuantArray $all.errLatency
  }
  multipliers = [pscustomobject]@{
    base      = 1.0
    space     = if ($null -ne $base -and $base.median) { [math]::Round((Summarize $all.gapSpace).median / $base.median, 3) } else { $null }
    wordStart = if ($null -ne $base -and $base.median) { [math]::Round((Summarize $all.gapAfterSpace).median / $base.median, 3) } else { $null }
    capital   = if ($null -ne $base -and $base.median) { [math]::Round((Summarize $all.gapShifted).median / $base.median, 3) } else { $null }
    digit     = if ($null -ne $base -and $base.median -and $null -ne (Summarize $all.gapDigit)) { [math]::Round((Summarize $all.gapDigit).median / $base.median, 3) } else { $null }
    punct     = if ($null -ne $base -and $base.median) { [math]::Round((Summarize $all.gapAfterPunct).median / $base.median, 3) } else { $null }
    sameHand  = if ($null -ne $base -and $base.median) { [math]::Round((Summarize $all.gapSameHand).median / $base.median, 3) } else { $null }
    # the two the engine uses: measured inside the plain case, relative to the plain
    # case as a whole, so applying either one leaves the overall median where it was
    baseSame  = if ($null -ne $base -and $base.median) { [math]::Round((Summarize $all.gapBaseSame).median / $base.median, 3) } else { $null }
    baseAlt   = if ($null -ne $base -and $base.median) { [math]::Round((Summarize $all.gapBaseAlt).median / $base.median, 3) } else { $null }
  }
  quality     = [pscustomobject]@{
    flushMs       = $FlushMs
    eventsTotal   = Cnt $all 'eventsTotal'
    eventsDropped = Cnt $all 'eventsFlushed'
  }
  errors      = [pscustomobject]@{
    charsPerMistake    = [math]::Round((Cnt $all 'chars') / [Math]::Max(1, (Cnt $all 'slips')), 1)
    mistakes           = Cnt $all 'slips'
    corrected          = Cnt $all 'slipsCorrected'
    backspace          = Cnt $all 'backspace'
    ctrlBackspace      = Cnt $all 'ctrlBackspace'
    delete             = Cnt $all 'delete'
    ctrlDelete         = Cnt $all 'ctrlDelete'
    autoRepeat         = Cnt $all 'autoRepeat'
    discretionaryFix   = Cnt $all 'discretionaryFix'
    uncorrected        = Cnt $all 'slipsLeft'
    burstLenQuantiles  = QuantArray $all.burstLen
  }
  pauses      = [pscustomobject]@{
    rate     = [math]::Round(1.0 * $all.pauseGap.Count / [Math]::Max(1, $all.gap.Count), 4)
    msQuants = QuantArray $all.pauseGap
  }
  digraphs    = @{}
}
foreach ($d in $dg) { $profile.digraphs[$d.pair] = @($d.median, $d.n) }

$profile | ConvertTo-Json -Depth 6 | Out-File -FilePath $OutProfile -Encoding utf8
''
"profile written to $OutProfile"
''
