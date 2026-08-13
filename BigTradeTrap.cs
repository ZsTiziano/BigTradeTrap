using TradingPlatform.BusinessLayer;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;

// =============================================================================
//  Big Trade Trap 2.0 — Quantower indicator
//  Detects one-sided volume "trap levels" via Volume Analysis and renders them
//  as volume-proportional bubbles with a per-level life cycle:
//  Trigger -> Active (line) -> Returned (price came back to the level) -> Invalid
//  Older traps fade out over time (default 48h); the strongest trap of the
//  chart is shown in the chart title. One-shot alerts on new traps.
// =============================================================================

public class BigTradeTrap : Indicator, IVolumeAnalysisIndicator
{
    // --------------------------------------------- detection parameters
    [InputParameter("Min Trap Volume (absolute)", 0, 1, 10000000, 1, 0)]
    public double MinTrapLevel = 100;

    [InputParameter("Trap Threshold %", 5, 1, 100, 1, 0)]
    public double TrapThreshold = 80;

    [InputParameter("Delta Threshold %", 6, 1, 100, 1, 0)]
    public double DeltaThreshold = 70;

    // --------------------------------------------- top-trap behavior (kept from v1)
    [InputParameter("Top Trap Only", 7)]
    public bool TopTrapOnly = false;

    // --------------------------------------------- appearance parameters
    [InputParameter("Bubble Mode", 10)]
    public BubbleMode BubbleMode = BubbleMode.Bubble;

    [InputParameter("Bubble Size Scale", 20, 1, 1000, 1, 0)]
    public double BubbleSizeScale = 20;

    [InputParameter("Max Bubble Radius (px)", 21, 10, 100, 1, 0)]
    public double MaxBubbleRadius = 40;

    [InputParameter("Min Bubble Radius (px)", 22, 2, 40, 1, 0)]
    public double MinBubbleRadius = 8;

    [InputParameter("Show Level Lines", 30)]
    public bool ShowLines = true;

    [InputParameter("Bull Color", 40)]
    public Color BullColor = Color.DarkCyan;

    [InputParameter("Bear Color", 41)]
    public Color BearColor = Color.DarkRed;

    // --------------------------------------------- lifecycle parameters
    [InputParameter("Traps Lifetime (hours)", 50, 1, 720, 1, 0)]
    public double TrapsLifetime = 48;

    [InputParameter("Invalid Trap Fade (hours)", 51, 1, 720, 1, 0)]
    public double InvalidFadeTime = 24;

    [InputParameter("Show Invalid Traps", 52)]
    public bool ShowInvalidTraps = true;

    [InputParameter("Show Returned Traps", 53)]
    public bool ShowReturnedTraps = true;

    // --------------------------------------------- alerts & info parameters
    [InputParameter("Enable New Trap Alerts", 60)]
    public bool EnableAlerts = true;

    [InputParameter("Play Alert Sound", 61)]
    public bool PlayAlertSound = true;

    [InputParameter("Show Chart Title (max trap)", 70)]
    public bool ShowChartTitle = true;

    public bool IsRequirePriceLevelsCalculation => true;

    // =========================================================================
    //  Models
    // =========================================================================

    public enum BubbleMode
    {
        Bubble,
        Highlight
    }

    public enum TrapState
    {
        Trigger,   // just created, still on the forming bar
        Active,    // line extended until price returns
        Returned,  // price returned to the level, marked for fade
        Invalid    // price closed through the level, marked for fade
    }

    private sealed class LevelBubble
    {
        public int BarIndex;
        public double Price;
        public bool IsBuy;
        public double TrapLevel;      // signed: >0 bull (buy) dominant
        public double BuyVolume;
        public double SellVolume;
        public double Volume;
        public double Delta;
        public string Key;            // dictionary key, set at creation
        public DateTime CreatedAt;    // anchored to the trap bar's opening time
        public TrapState State = TrapState.Trigger;
        public int CreationBar = -1;  // used for creation-time anchoring/removal
        public bool LineVisible;
        public int LineEndIndex = -1;
        public int ScanIndex = -1;    // incremental scan position for line/invalidate
        public bool Alerted;
    }

    // =========================================================================
    //  State
    // =========================================================================

    private static readonly Font BubbleFont = new Font("Consolas", 8, FontStyle.Bold);
    private static readonly Font TitleFont = new Font("Consolas", 10, FontStyle.Bold);
    private static readonly Font LoadingFont = new Font("Consolas", 14, FontStyle.Bold);

    private readonly Dictionary<string, LevelBubble> _bubbles = new();
    private readonly List<LevelBubble> _renderList = new();
    private readonly object _sync = new object();
    private readonly HashSet<int> _countedBars = new();
    private readonly Dictionary<string, double> _lastAlertAt = new();

    private const int AlertCooldownSeconds = 30;
    private const int MaxAlertsPerUpdate = 3;

    private double _prevMinTrapLevel, _prevTrapThreshold, _prevDeltaThreshold;
    private bool _prevTopTrapOnly, _prevShowLines, _prevShowInvalidTraps, _prevShowReturnedTraps;
    private BubbleMode _prevBubbleMode;
    private double _prevBubbleSizeScale, _prevMaxBubbleRadius, _prevMinBubbleRadius;
    private double _prevTrapsLifetime, _prevInvalidFadeTime;
    private int _lastScannedBarIndex;
    private bool _isReady;
    private double _maxTrapOnChart = 1;   // scale anchor for size/alpha
    private string _chartTitle = string.Empty;
    private Chart _lastChart;                          // chart swap detection

    public BigTradeTrap()
    {
        Name = "BigTradeTrap V2.0";
        Description = "Volume-proportional trap levels with lifecycle (trigger/return/invalid) and alerts";
        SeparateWindow = false;
        SnapshotParameters();
    }

    private void SnapshotParameters()
    {
        _prevMinTrapLevel = MinTrapLevel;
        _prevTrapThreshold = TrapThreshold;
        _prevDeltaThreshold = DeltaThreshold;
        _prevTopTrapOnly = TopTrapOnly;
        _prevShowLines = ShowLines;
        _prevShowInvalidTraps = ShowInvalidTraps;
        _prevShowReturnedTraps = ShowReturnedTraps;
        _prevBubbleMode = BubbleMode;
        _prevBubbleSizeScale = BubbleSizeScale;
        _prevMaxBubbleRadius = MaxBubbleRadius;
        _prevMinBubbleRadius = MinBubbleRadius;
        _prevTrapsLifetime = TrapsLifetime;
        _prevInvalidFadeTime = InvalidFadeTime;
    }

    private bool ParametersChanged() =>
        MinTrapLevel != _prevMinTrapLevel ||
        TrapThreshold != _prevTrapThreshold ||
        DeltaThreshold != _prevDeltaThreshold ||
        TopTrapOnly != _prevTopTrapOnly ||
        ShowLines != _prevShowLines ||
        ShowInvalidTraps != _prevShowInvalidTraps ||
        ShowReturnedTraps != _prevShowReturnedTraps ||
        BubbleMode != _prevBubbleMode ||
        BubbleSizeScale != _prevBubbleSizeScale ||
        MaxBubbleRadius != _prevMaxBubbleRadius ||
        MinBubbleRadius != _prevMinBubbleRadius ||
        TrapsLifetime != _prevTrapsLifetime ||
        InvalidFadeTime != _prevInvalidFadeTime;

    public void VolumeAnalysisData_Loaded()
    {
        ResetState();
        Recalc();
    }

    private void ResetState(bool keepAlerted = false)
    {
        lock (_sync)
        {
            _bubbles.Clear();
            _renderList.Clear();
        }
        _countedBars.Clear();
        if (!keepAlerted)
            _lastAlertAt.Clear();
        _isReady = false;
        _lastScannedBarIndex = 0;
        _maxTrapOnChart = 1;
        _chartTitle = string.Empty;
    }

    // =========================================================================
    //  Update pipeline
    // =========================================================================

    protected override void OnUpdate(UpdateArgs args)
    {
        if (HistoricalData == null || CurrentChart == null)
            return;
        if (HistoricalData.Count < 30)
            return;

        // New chart/window/symbol: never reuse bubbles keyed by stale bar indices.
        if (CurrentChart != _lastChart)
        {
            ResetState();
            _lastChart = CurrentChart;
        }

        if (ParametersChanged())
        {
            ResetState(keepAlerted: true); // alert cooldown history survives param edits
            SnapshotParameters();
        }

        int currentIndex = HistoricalData.Count - 1;
        ScanHistoricalBars(currentIndex);
        ProcessBar(currentIndex, true);
        UpdateBubbleLines(currentIndex);
        UpdateFade(currentIndex);
        SendAlerts(currentIndex, args.Reason != UpdateReason.HistoricalBar);

        lock (_sync)
        {
            _renderList.Clear();
            _renderList.AddRange(_bubbles.Values);
            if (!_isReady && _renderList.Count > 0)
                _isReady = true;
        }

        RequestPaint();
    }

    private void ScanHistoricalBars(int currentIndex)
    {
        for (int idx = _lastScannedBarIndex; idx < currentIndex; idx++)
        {
            if (HistoricalData[idx, SeekOriginHistory.Begin] is not HistoryItemBar bar)
            {
                _lastScannedBarIndex = idx + 1;
                continue;
            }

            if (bar.VolumeAnalysisData?.PriceLevels == null)
                continue; // retried on next tick without blocking the scan

            if (!_countedBars.Add(idx))
            {
                _lastScannedBarIndex = idx + 1;
                continue;
            }

            // Scan this historical bar for invalidation of traps created on earlier bars.
            UpdateBubbleLinesForBar(idx);
            ProcessBar(idx, false);
            _lastScannedBarIndex = idx + 1;
        }
    }

    private void ProcessBar(int index, bool allowRemoval)
    {
        if (HistoricalData[index, SeekOriginHistory.Begin] is not HistoryItemBar bar)
            return;

        if (bar.VolumeAnalysisData?.PriceLevels == null)
        {
            if (allowRemoval)
                RemoveBarBubbles(index);
            return;
        }

        var seen = new HashSet<string>();
        LevelBubble bestBuy = null;
        LevelBubble bestSell = null;

        foreach (var kv in bar.VolumeAnalysisData.PriceLevels)
        {
            if (!TryGetTrapInfo(kv.Value, out double trapLevel, out bool isBuyTrap, out bool isSellTrap))
                continue;

            if (isBuyTrap)
            {
                string key = GetKey(index, kv.Key, true);
                seen.Add(key);
                LevelBubble bubble = GetOrCreateBubble(key, index, kv.Key, true, kv.Value, trapLevel);
                if (TopTrapOnly && (bestBuy == null || Math.Abs(trapLevel) > Math.Abs(bestBuy.TrapLevel)))
                    bestBuy = bubble;
            }

            if (isSellTrap)
            {
                string key = GetKey(index, kv.Key, false);
                seen.Add(key);
                LevelBubble bubble = GetOrCreateBubble(key, index, kv.Key, false, kv.Value, trapLevel);
                if (TopTrapOnly && (bestSell == null || Math.Abs(trapLevel) > Math.Abs(bestSell.TrapLevel)))
                    bestSell = bubble;
            }
        }

        if (allowRemoval)
        {
            string prefix = $"{index}|";
            var toRemove = _bubbles.Keys
                .Where(k => k.StartsWith(prefix) && !seen.Contains(k))
                .ToList();
            foreach (var key in toRemove)
                _bubbles.Remove(key);
        }

        if (TopTrapOnly)
        {
            ApplyTopTrapOnly(index, bestBuy, true);
            ApplyTopTrapOnly(index, bestSell, false);
        }
    }

    private LevelBubble GetOrCreateBubble(string key, int index, double price, bool isBuy,
        VolumeAnalysisItem item, double trapLevel)
    {
        if (!_bubbles.TryGetValue(key, out var bubble))
        {
            bubble = new LevelBubble
            {
                Key = key,
                BarIndex = index,
                Price = price,
                IsBuy = isBuy,
                CreationBar = index,
                CreatedAt = ResolveBarTime(index)
            };
            _bubbles[key] = bubble;
        }

        bubble.BarIndex = index;
        bubble.TrapLevel = trapLevel;
        bubble.BuyVolume = item.BuyVolume;
        bubble.SellVolume = item.SellVolume;
        bubble.Volume = item.Volume;
        bubble.Delta = item.Delta;

        return bubble;
    }

    private void ApplyTopTrapOnly(int index, LevelBubble best, bool isBuy)
    {
        if (best == null) return;

        string suffix = isBuy ? "|B" : "|S";
        string prefix = $"{index}|";
        var toRemove = _bubbles.Keys
            .Where(k => k.StartsWith(prefix) && k.EndsWith(suffix) && _bubbles[k] != best)
            .ToList();

        foreach (var key in toRemove)
            _bubbles.Remove(key);
    }

    private void RemoveBarBubbles(int index)
    {
        string prefix = $"{index}|";
        var toRemove = _bubbles.Keys.Where(k => k.StartsWith(prefix)).ToList();
        foreach (var key in toRemove)
            _bubbles.Remove(key);
    }

    private static string GetKey(int index, double price, bool isBuy)
        => $"{index}|{price:F10}|{(isBuy ? "B" : "S")}";

    // Bar opening time (UTC), used to anchor age-based fade so a full historical
    // load does not make everything look like it just happened.
    private DateTime ResolveBarTime(int index)
    {
        var bar = HistoricalData[index, SeekOriginHistory.Begin] as HistoryItemBar;
        if (bar?.TimeLeft != null)
            return bar.TimeLeft.ToUniversalTime();
        return DateTime.UtcNow;
    }

    private bool TryGetTrapInfo(VolumeAnalysisItem lvl, out double trapLevel, out bool isBuyTrap, out bool isSellTrap)
    {
        trapLevel = 0;
        isBuyTrap = false;
        isSellTrap = false;

        if (lvl.Volume == 0)
            return false;

        double buyPct = lvl.BuyVolume / lvl.Volume * 100.0;
        double sellPct = lvl.SellVolume / lvl.Volume * 100.0;
        double deltaPct = Math.Abs(lvl.Delta) / lvl.Volume * 100.0;
        bool isStrongDelta = deltaPct > DeltaThreshold;

        if (buyPct > TrapThreshold || sellPct > TrapThreshold)
            trapLevel = lvl.BuyVolume - lvl.SellVolume;

        if (Math.Abs(trapLevel) < Math.Max(1, MinTrapLevel))
            return false;

        isBuyTrap = buyPct > TrapThreshold && isStrongDelta;
        isSellTrap = sellPct > TrapThreshold && isStrongDelta;

        return isBuyTrap || isSellTrap;
    }

    // =========================================================================
    //  Lifecycle: lines, invalid detection, fade
    // =========================================================================

    private void UpdateBubbleLines(int currentIndex)
    {
        // Historical bars up to the last one were already consumed by
        // ScanHistoricalBars -> UpdateBubbleLinesForBar; only the current bar remains.
        UpdateBubbleLinesForBar(currentIndex);

        foreach (var lb in _bubbles.Values)
        {
            // Step up forming traps to Active exactly once.
            if (Math.Min(lb.BarIndex, lb.CreationBar) < currentIndex && lb.State == TrapState.Trigger)
                lb.State = TrapState.Active;
        }
    }

    // Examines one bar against every active trap once and only once (guarded by ScanIndex).
    private void UpdateBubbleLinesForBar(int idx)
    {
        if (HistoricalData[idx, SeekOriginHistory.Begin] is not HistoryItemBar b)
            return;

        foreach (var lb in _bubbles.Values)
        {
            if (lb.State != TrapState.Active && lb.State != TrapState.Trigger)
                continue;
            if (lb.BarIndex >= idx || lb.ScanIndex >= idx)
                continue;

            bool closedThrough = lb.IsBuy ? b.Close > lb.Price : b.Close < lb.Price;
            if (closedThrough)
            {
                lb.State = TrapState.Invalid;
                lb.ScanIndex = idx;
                continue;
            }

            bool returned = lb.IsBuy ? b.High >= lb.Price : b.Low <= lb.Price;
            if (returned)
            {
                // A trap whose own bar high already crossed the level is just a
                // non-returning entry (line hidden), not a returned trap.
                if (lb.BarIndex == idx)
                {
                    lb.LineVisible = false;
                    lb.ScanIndex = idx;
                    continue;
                }

                lb.LineEndIndex = idx;
                lb.State = TrapState.Returned;
                lb.ScanIndex = idx;
                continue;
            }

            lb.ScanIndex = idx;
        }
    }

    private void UpdateFade(int currentIndex)
    {
        double nowUtc = (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
        double maxTrap = 0;
        LevelBubble best = null;
        var stale = new List<LevelBubble>();

        foreach (var lb in _bubbles.Values)
        {
            // A trap whose own bar's close has passed through the level is invalidated immediately
            // (its own bar is not covered by the forward scan).
            {
                var ownBar = HistoricalData[lb.BarIndex, SeekOriginHistory.Begin] as HistoryItemBar;
                if (ownBar != null && lb.BarIndex >= 0 && lb.BarIndex < HistoricalData.Count)
                {
                    bool closedThrough = lb.IsBuy ? ownBar.Close > lb.Price : ownBar.Close < lb.Price;
                    if (closedThrough && lb.State != TrapState.Invalid)
                        lb.State = TrapState.Invalid;
                }
            }

            if (lb.State == TrapState.Trigger && currentIndex > lb.BarIndex)
                lb.State = TrapState.Active;

            if (lb.State == TrapState.Active || lb.State == TrapState.Trigger ||
                lb.State == TrapState.Returned)
            {
                maxTrap = Math.Max(maxTrap, Math.Abs(lb.TrapLevel));
                if (best == null || Math.Abs(lb.TrapLevel) > Math.Abs(best.TrapLevel))
                    best = lb;
            }

            // Traps that already ended are removed once they have faded past their lifetime.
            // Active traps are removed once they exceed their lifetime as well (fresh-only chart).
            if (lb.State == TrapState.Active || lb.State == TrapState.Returned || lb.State == TrapState.Invalid)
            {
                double ageHours = (nowUtc - (lb.CreatedAt - new DateTime(1970, 1, 1)).TotalSeconds) / 3600.0;
                double limit = lb.State == TrapState.Invalid ? InvalidFadeTime : TrapsLifetime;
                if (ageHours > limit)
                    stale.Add(lb);
            }
        }

        foreach (var lb in stale)
            _bubbles.Remove(lb.Key);

        _maxTrapOnChart = Math.Max(1, maxTrap);

        if (best != null)
            _chartTitle = $"Max Trap: {(best.IsBuy ? "Bull" : "Bear")} {Math.Abs(best.TrapLevel):F0} @ {best.Price.ToString("F2")}";
        else
            _chartTitle = string.Empty;
    }

    // =========================================================================
    //  One-shot alerts (only on live/new updates, never during historical load)
    // =========================================================================

    private void SendAlerts(int currentIndex, bool allow)
    {
        if (!EnableAlerts || !allow)
            return;

        long nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        int sent = 0;

        foreach (var lb in GetCurrentBarTraps(currentIndex))
        {
            if (sent >= MaxAlertsPerUpdate)
                break;
            if (lb.State == TrapState.Invalid)
                continue;

            // One-shot per trap, plus a cooldown so a disappearing/reappearing
            // trap on the same bar cannot re-trigger immediately.
            string key = lb.Key;
            double last = _lastAlertAt.TryGetValue(key, out double v) ? v : double.MinValue;
            if (lb.Alerted || nowSec - last < AlertCooldownSeconds)
                continue;

            lb.Alerted = true;
            _lastAlertAt[key] = nowSec;

            string dir = lb.IsBuy ? "Bull" : "Bear";
            string text = $"New {dir} Trap @ {lb.Price.ToString("F2")}  [{Math.Abs(lb.TrapLevel):F0}]";

            Core.Alert(new Alert()
            {
                Name = Name,
                Text = text,
                PlaySound = PlayAlertSound,
                SymbolName = Symbol?.Name,
                ConnectionName = Symbol?.Connection?.Name
            });

            sent++;
        }
    }

    private List<LevelBubble> GetCurrentBarTraps(int currentIndex)
    {
        var list = new List<LevelBubble>();
        if (HistoricalData[currentIndex, SeekOriginHistory.Begin] is HistoryItemBar bar &&
            bar.VolumeAnalysisData?.PriceLevels != null)
        {
            double buyPct, sellPct, deltaPct;
            foreach (var kv in bar.VolumeAnalysisData.PriceLevels)
            {
                var item = kv.Value;
                if (item.Volume == 0)
                    continue;
                buyPct = item.BuyVolume / item.Volume * 100.0;
                sellPct = item.SellVolume / item.Volume * 100.0;
                deltaPct = Math.Abs(item.Delta) / item.Volume * 100.0;
                bool buyTrap = buyPct > TrapThreshold && deltaPct > DeltaThreshold;
                bool sellTrap = sellPct > TrapThreshold && deltaPct > DeltaThreshold;
                if (!buyTrap && !sellTrap)
                    continue;

                bool isBuy = buyTrap; // a level that qualifies as both resolves to buy
                if (_bubbles.TryGetValue(GetKey(currentIndex, kv.Key, isBuy), out var lb))
                    list.Add(lb);
            }

            // Strongest trap last so the loop fires the loudest alert first.
            list.Sort((a, b) => Math.Abs(b.TrapLevel).CompareTo(Math.Abs(a.TrapLevel)));
        }
        return list;
    }

    // =========================================================================
    //  Painting
    // =========================================================================

    public override void OnPaintChart(PaintChartEventArgs args)
    {
        if (CurrentChart?.Windows == null || args.WindowIndex < 0 || args.WindowIndex >= CurrentChart.Windows.Length)
            return;

        var g = args.Graphics;
        if (g == null)
            return;

        var conv = CurrentChart.Windows[args.WindowIndex]?.CoordinatesConverter;
        if (conv == null)
            return;

        // Recheck chart identity: bubbles keyed by bar indices are meaningless if the
        // chart has been swapped underneath the indicator.
        if (CurrentChart != _lastChart)
        {
            ResetState();
            _lastChart = CurrentChart;
        }

        // Stay out of the way while the chart is scrolling: expired/returned traps
        // are faded out and hidden inside the scrolled-out window.
        bool scrolled = false;
        if (HistoricalData != null)
        {
            int min = Math.Min(args.LeftVisibleBarIndex, args.RightVisibleBarIndex);
            int max = Math.Max(args.LeftVisibleBarIndex, args.RightVisibleBarIndex);
            scrolled = min > Math.Max(0, HistoricalData.Count - (int)Math.Max(50, CurrentChart.BarsCount));
        }
        if (scrolled)
        {
            lock (_sync)
            {
                _renderList.RemoveAll(lb => lb.State == TrapState.Returned || lb.State == TrapState.Invalid);
            }
        }

        bool ready;
        List<LevelBubble> bubbles;
        double maxTrap;
        string title;
        lock (_sync)
        {
            ready = _isReady;
            bubbles = new List<LevelBubble>(_renderList);
            maxTrap = _maxTrapOnChart;
            title = _chartTitle;
        }

        if (!ready && bubbles.Count == 0)
        {
            DrawLoading(g, args.Rectangle);
            return;
        }

        DrawTrapBubbles(g, args.Rectangle, args.WindowIndex, bubbles, maxTrap, title);
    }

    // Called when the indicator is removed — never leave painted traps behind.
    protected override void OnRemoved()
    {
        base.OnRemoved();
        RequestPaint();
    }

    private void DrawLoading(Graphics g, Rectangle clip)
    {
        string text = "Loading Data...";
        SizeF sz = g.MeasureString(text, LoadingFont);
        float lx = clip.Right - sz.Width - 20;
        float ly = clip.Top + 15;

        using var bg = new SolidBrush(Color.FromArgb(180, 8, 8, 8));
        g.FillRectangle(bg, lx - 10, ly - 5, sz.Width + 20, sz.Height + 10);

        using var brush = new SolidBrush(Color.Gray);
        g.DrawString(text, LoadingFont, brush, lx, ly);
    }

    private void RequestPaint()
    {
        try
        {
            var w = CurrentChart?.MainWindow;
            if (w == null) return;

            var m = w.GetType().GetMethod("MakePainting",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            m?.Invoke(w, null);
        }
        catch { }
    }

    private void DrawTrapBubbles(Graphics g, Rectangle clip, int windowIndex,
        List<LevelBubble> bubbles, double maxTrap, string title)
    {
        var conv = CurrentChart.Windows[windowIndex]?.CoordinatesConverter;
        if (conv == null)
            return;

        double nowUtc = (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;

        // --- horizontal lines for active traps ---
        if (ShowLines)
        {
            foreach (var lb in bubbles)
            {
                if (lb.BarIndex < 0 || lb.BarIndex >= HistoricalData.Count)
                    continue;
                if (!lb.LineVisible || lb.State != TrapState.Active)
                    continue;

                int y = (int)conv.GetChartY(lb.Price);
                if (y < clip.Top || y > clip.Bottom)
                    continue;

                var bar = HistoricalData[lb.BarIndex, SeekOriginHistory.Begin] as HistoryItemBar;
                if (bar == null)
                    continue;

                int barX = (int)conv.GetChartX(bar.TimeLeft) + CurrentChart.BarsWidth / 2;
                bool barVisible = barX >= clip.Left && barX <= clip.Right;

                int lineStartX = clip.Left;
                if (barVisible)
                {
                    if (lb.BarIndex + 1 < HistoricalData.Count)
                    {
                        var nextBar = HistoricalData[lb.BarIndex + 1, SeekOriginHistory.Begin] as HistoryItemBar;
                        if (nextBar != null)
                            lineStartX = (int)conv.GetChartX(nextBar.TimeLeft) + CurrentChart.BarsWidth / 2;
                    }
                    else
                    {
                        lineStartX = barX;
                    }
                }
                if (lineStartX < clip.Left)
                    lineStartX = clip.Left;

                int lineEndX = clip.Right - 10;
                if (lb.LineEndIndex >= 0)
                {
                    var endBar = HistoricalData[lb.LineEndIndex, SeekOriginHistory.Begin] as HistoryItemBar;
                    if (endBar != null)
                        lineEndX = (int)conv.GetChartX(endBar.TimeLeft) + CurrentChart.BarsWidth / 2;
                }

                if (lineEndX < lineStartX)
                    continue;

                Color lineColor = lb.TrapLevel > 0 ? BullColor : BearColor;
                // Alpha combined from volume strength and age: active/trigger full,
                // faded toward the lifetime end.
                int volAlpha = Math.Min(200, Math.Max(30, (int)Math.Abs(lb.TrapLevel) * 2));
                double life = (nowUtc - (lb.CreatedAt - new DateTime(1970, 1, 1)).TotalSeconds) / 3600.0 / TrapsLifetime;
                if (life > 1) life = 1;
                double lineFactor = (life < 0.7) ? 1.0 : (1.0 - (life - 0.7) / 0.3 * 0.5);
                int lineAlpha = (int)Math.Max(20, volAlpha * lineFactor);
                using var pen = new Pen(Color.FromArgb(lineAlpha, lineColor), 2);
                g.DrawLine(pen, lineStartX, y, lineEndX, y);
            }
        }

        // --- bubbles / highlight boxes ---
        foreach (var lb in bubbles)
        {
            if (lb.BarIndex < 0 || lb.BarIndex >= HistoricalData.Count)
                continue;

            var bar = HistoricalData[lb.BarIndex, SeekOriginHistory.Begin] as HistoryItemBar;
            if (bar == null)
                continue;

            int x = (int)conv.GetChartX(bar.TimeLeft) + CurrentChart.BarsWidth / 2;
            if (x < clip.Left || x > clip.Right)
                continue;

            int y = (int)conv.GetChartY(lb.Price);
            if (y < clip.Top || y > clip.Bottom)
                continue;

            double ageHours = (nowUtc - (lb.CreatedAt - new DateTime(1970, 1, 1)).TotalSeconds) / 3600.0;
            int alpha = ComputeStateAlpha(lb, ageHours, maxTrap);
            if (alpha <= 0)
                continue;

            Color baseColor = lb.TrapLevel > 0 ? BullColor : BearColor;
            string text = Math.Abs(lb.TrapLevel).ToString("F0");

            if (BubbleMode == BubbleMode.Bubble)
                DrawBubble(g, x, y, text, lb, alpha, baseColor);
            else
                DrawHighlight(g, x, y, text, alpha, baseColor);
        }

        // --- strongest trap in the chart title ---
        if (ShowChartTitle && !string.IsNullOrEmpty(title))
        {
            SizeF sz = g.MeasureString(title, TitleFont);
            float lx = clip.Left + 8;
            float ly = clip.Top + 6;
            using var bg = new SolidBrush(Color.FromArgb(140, 8, 8, 8));
            g.FillRectangle(bg, lx - 4, ly - 3, sz.Width + 8, sz.Height + 6);
            using var brush = new SolidBrush(Color.Gray);
            g.DrawString(title, TitleFont, brush, lx, ly);
        }
    }

    private int ComputeStateAlpha(LevelBubble lb, double ageHours, double maxTrap)
    {
        double strength = Math.Abs(lb.TrapLevel) / maxTrap;
        double baseAlpha = 45 + 130 * Math.Min(1, strength); // 45..175

        switch (lb.State)
        {
            case TrapState.Trigger:
            case TrapState.Active:
                double life = ageHours / TrapsLifetime;
                if (life >= 1)
                    return (int)Math.Max(10, baseAlpha * 0.3);
                if (life > 0.7)
                    baseAlpha *= 1 - (life - 0.7) / 0.3 * 0.5;
                return (int)Math.Max(30, Math.Min(230, baseAlpha));

            case TrapState.Returned:
                if (!ShowReturnedTraps)
                    return 0;
                if (ageHours / TrapsLifetime > 0.6)
                    return 0;
                return (int)Math.Max(25, Math.Min(140, baseAlpha * 0.55));

            case TrapState.Invalid:
                if (!ShowInvalidTraps)
                    return 0;
                double invRatio = ageHours / InvalidFadeTime;
                if (invRatio >= 1)
                    return 0;
                return (int)Math.Max(18, Math.Min(90, baseAlpha * 0.30 * (1 - invRatio)));

            default:
                return 0;
        }
    }

    private void DrawBubble(Graphics g, int x, int y, string text, LevelBubble lb,
        int alpha, Color baseColor)
    {
        // Diameter from volume: r = k * sqrt(vol), clamped; at least 3px.
        double r = Math.Sqrt(Math.Abs(lb.TrapLevel) * BubbleSizeScale);
        double minR = Math.Max(3, Math.Min(MinBubbleRadius, MaxBubbleRadius));
        double maxR = Math.Max(MinBubbleRadius, MaxBubbleRadius);
        r = Math.Min(maxR, Math.Max(minR, r));

        var rect = new RectangleF(x - (float)r, y - (float)r, (float)r * 2, (float)r * 2);

        using var fill = new SolidBrush(Color.FromArgb(alpha, baseColor));
        using var outline = new Pen(Color.FromArgb(Math.Min(255, alpha + 60), baseColor), 1.5f);
        using var textBrush = new SolidBrush(Color.FromArgb(Math.Min(255, alpha + 80), Color.White));

        g.FillEllipse(fill, rect);
        g.DrawEllipse(outline, rect);

        SizeF sz = g.MeasureString(text, BubbleFont);
        if (sz.Width < rect.Width - 4 && sz.Height < rect.Height - 2)
            g.DrawString(text, BubbleFont, textBrush, x - sz.Width / 2, y - sz.Height / 2);
    }

    private void DrawHighlight(Graphics g, int x, int y, string text, int alpha, Color baseColor)
    {
        SizeF sz = g.MeasureString(text, BubbleFont);
        int pad = 3;

        using var bg = new SolidBrush(Color.FromArgb(alpha, baseColor));
        g.FillRectangle(bg, x - sz.Width / 2 - pad, y - sz.Height / 2 + 1, sz.Width + pad * 2, sz.Height);

        using var textBrush = new SolidBrush(Color.FromArgb(Math.Min(255, alpha + 80), Color.White));
        g.DrawString(text, BubbleFont, textBrush, x - sz.Width / 2, y - sz.Height / 2 + 1);
    }

    private void Recalc()
    {
        if (HistoricalData == null || CurrentChart == null || HistoricalData.Count < 30)
            return;

        int currentIndex = HistoricalData.Count - 1;
        ScanHistoricalBars(currentIndex);
        ProcessBar(currentIndex, true);
        UpdateBubbleLines(currentIndex);
        UpdateFade(currentIndex);

        lock (_sync)
        {
            _renderList.Clear();
            _renderList.AddRange(_bubbles.Values);
            if (!_isReady && _renderList.Count > 0)
                _isReady = true;
        }

        RequestPaint();
    }
}
