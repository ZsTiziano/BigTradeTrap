using TradingPlatform.BusinessLayer;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;

public class BigTradeTrap : Indicator, IVolumeAnalysisIndicator
{
    [InputParameter("Min Trap Level")]
    public double MinTrapLevel = 100;

    [InputParameter("Trap Threshold %", 5)]
    public double TrapThreshold = 80;

    [InputParameter("Delta Threshold %", 6)]
    public double DeltaThreshold = 70;

    [InputParameter("Top Trap Only", 7)]
    public bool TopTrapOnly = false;

    [InputParameter("Bull Color", 10)]
    public Color BullColor = Color.DarkCyan;

    [InputParameter("Bear Color", 20)]
    public Color BearColor = Color.DarkRed;

    public bool IsRequirePriceLevelsCalculation => true;

    private class LevelBubble
    {
        public int BarIndex;
        public double Price;
        public bool IsBuy;
        public double TrapLevel;
        public bool LineVisible;
        public int LineEndIndex = -1;
    }

    // Strutture dati ottimizzate
    private readonly Dictionary<string, LevelBubble> _bubbles = new();
    private readonly List<LevelBubble> _renderList = new();
    private readonly object _sync = new object();
    private readonly HashSet<int> _countedBars = new();

    private double _prevMinTrapLevel;
    private double _prevTrapThreshold;
    private double _prevDeltaThreshold;
    private bool _prevTopTrapOnly;
    private int _lastScannedBarIndex;
    private bool _isReady;
    private DateTime _loadStart;
    private MethodInfo _makePaintingMethod;

    public BigTradeTrap()
    {
        Name = "BigTradeTrap V1.1";
        Description = "Identifies trap levels on the chart";
        SeparateWindow = false;
        _loadStart = DateTime.UtcNow;
        _lastScannedBarIndex = 0;

        _prevMinTrapLevel = MinTrapLevel;
        _prevTrapThreshold = TrapThreshold;
        _prevDeltaThreshold = DeltaThreshold;
        _prevTopTrapOnly = TopTrapOnly;
    }

    public void VolumeAnalysisData_Loaded()
    {
        ResetState();
        _loadStart = DateTime.UtcNow;
        Recalc();
    }

    private void ResetState()
    {
        lock (_sync)
        {
            _bubbles.Clear();
            _renderList.Clear();
        }
        _countedBars.Clear();
        _isReady = false;
        _loadStart = DateTime.UtcNow;
        _lastScannedBarIndex = 0;
    }

    protected override void OnUpdate(UpdateArgs args)
    {
        if (HistoricalData == null || CurrentChart == null)
            return;

        if (HistoricalData.Count < 30)
            return;

        // Reset se cambiano i parametri
        if (MinTrapLevel != _prevMinTrapLevel || TrapThreshold != _prevTrapThreshold ||
            DeltaThreshold != _prevDeltaThreshold || TopTrapOnly != _prevTopTrapOnly)
        {
            ResetState();
            _prevMinTrapLevel = MinTrapLevel;
            _prevTrapThreshold = TrapThreshold;
            _prevDeltaThreshold = DeltaThreshold;
            _prevTopTrapOnly = TopTrapOnly;
        }

        int currentIndex = HistoricalData.Count - 1;

        // 1. Processa barre storiche (chiuse) - esclude l'ultima
        ScanHistoricalBars(currentIndex);
        // 2. Processa barra corrente (può cambiare ad ogni tick)
        ProcessBar(currentIndex, true);
        // 3. Aggiorna visibilità linee
        UpdateBubbleLines(currentIndex);

        // Snapshot thread-safe per il rendering
        lock (_sync)
        {
            _renderList.Clear();
            _renderList.AddRange(_bubbles.Values);
        }

        if (!_isReady && _bubbles.Count > 0)
            _isReady = true;

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
                continue; // riproverà al prossimo tick senza bloccare lo scan

            if (!_countedBars.Add(idx))
            {
                _lastScannedBarIndex = idx + 1;
                continue;
            }

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

                if (!_bubbles.TryGetValue(key, out var bubble))
                {
                    bubble = new LevelBubble { BarIndex = index, Price = kv.Key, IsBuy = true };
                    _bubbles[key] = bubble;
                }
                bubble.TrapLevel = trapLevel;

                if (TopTrapOnly && (bestBuy == null || Math.Abs(trapLevel) > Math.Abs(bestBuy.TrapLevel)))
                    bestBuy = bubble;
            }

            if (isSellTrap)
            {
                string key = GetKey(index, kv.Key, false);
                seen.Add(key);

                if (!_bubbles.TryGetValue(key, out var bubble))
                {
                    bubble = new LevelBubble { BarIndex = index, Price = kv.Key, IsBuy = false };
                    _bubbles[key] = bubble;
                }
                bubble.TrapLevel = trapLevel;

                if (TopTrapOnly && (bestSell == null || Math.Abs(trapLevel) > Math.Abs(bestSell.TrapLevel)))
                    bestSell = bubble;
            }
        }

        // Per la barra in formazione: rimuovi bolle non più presenti nei dati
        if (allowRemoval)
        {
            string prefix = $"{index}|";
            var toRemove = _bubbles.Keys
                .Where(k => k.StartsWith(prefix) && !seen.Contains(k))
                .ToList();

            foreach (var key in toRemove)
                _bubbles.Remove(key);
        }

        // Mantieni solo il top trap per direzione
        if (TopTrapOnly)
        {
            ApplyTopTrapOnly(index, bestBuy, true);
            ApplyTopTrapOnly(index, bestSell, false);
        }
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

        if (Math.Abs(trapLevel) < MinTrapLevel)
            return false;

        isBuyTrap = buyPct > TrapThreshold && isStrongDelta;
        isSellTrap = sellPct > TrapThreshold && isStrongDelta;

        return isBuyTrap || isSellTrap;
    }

    private void UpdateBubbleLines(int currentIndex)
    {
        foreach (var lb in _bubbles.Values)
        {
            if (!lb.LineVisible)
            {
                if (lb.BarIndex >= currentIndex)
                    continue;

                if (HistoricalData[lb.BarIndex, SeekOriginHistory.Begin] is HistoryItemBar bar)
                    lb.LineVisible = lb.IsBuy ? bar.Close < lb.Price : bar.Close > lb.Price;
            }

            if (!lb.LineVisible || lb.LineEndIndex >= 0)
                continue;

            for (int i = lb.BarIndex + 1; i <= currentIndex; i++)
            {
                if (HistoricalData[i, SeekOriginHistory.Begin] is not HistoryItemBar b)
                    continue;

                bool returned = lb.IsBuy ? b.High >= lb.Price : b.Low <= lb.Price;
                if (returned)
                {
                    lb.LineEndIndex = i;
                    break;
                }
            }
        }
    }

    private void Recalc()
    {
        if (HistoricalData == null || CurrentChart == null || HistoricalData.Count < 30)
            return;

        int currentIndex = HistoricalData.Count - 1;
        ScanHistoricalBars(currentIndex);
        ProcessBar(currentIndex, true);
        UpdateBubbleLines(currentIndex);

        lock (_sync)
        {
            _renderList.Clear();
            _renderList.AddRange(_bubbles.Values);
        }

        if (_bubbles.Count > 0)
            _isReady = true;

        RequestPaint();
    }

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

        bool ready;
        List<LevelBubble> bubbles;
        lock (_sync)
        {
            ready = _isReady;
            bubbles = new List<LevelBubble>(_renderList);
        }

        if (!ready && bubbles.Count == 0)
        {
            DrawLoading(g, args.Rectangle);
            return;
        }

        DrawLevelBubbles(g, args.Rectangle, args.WindowIndex, bubbles);
    }

    private void DrawLoading(Graphics g, Rectangle clip)
    {
        using var font = new Font("Consolas", 14, FontStyle.Bold);
        string text = "Loading Data...";
        SizeF sz = g.MeasureString(text, font);
        float lx = clip.Right - sz.Width - 20;
        float ly = clip.Top + 15;

        using var bg = new SolidBrush(Color.FromArgb(180, 8, 8, 8));
        g.FillRectangle(bg, lx - 10, ly - 5, sz.Width + 20, sz.Height + 10);

        using var brush = new SolidBrush(Color.Gray);
        g.DrawString(text, font, brush, lx, ly);
    }

    private void RequestPaint()
    {
        try
        {
            var w = CurrentChart?.MainWindow;
            if (w == null) return;

            _makePaintingMethod ??= w.GetType().GetMethod("MakePainting",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            _makePaintingMethod?.Invoke(w, null);
        }
        catch { }
    }

    private void DrawLevelBubbles(Graphics g, Rectangle clip, int windowIndex, List<LevelBubble> bubbles)
    {
        var conv = CurrentChart.Windows[windowIndex]?.CoordinatesConverter;
        if (conv == null)
            return;

        using var font = new Font("Consolas", 8, FontStyle.Bold);

        // --- Linee orizzontali ---
        foreach (var lb in bubbles)
        {
            if (lb.BarIndex < 0 || lb.BarIndex >= HistoricalData.Count)
                continue;
            if (!lb.LineVisible)
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
            int baseAlpha = Math.Min(200, Math.Max(30, (int)Math.Abs(lb.TrapLevel) * 2));
            using var pen = new Pen(Color.FromArgb(baseAlpha, lineColor), 2);
            g.DrawLine(pen, lineStartX, y, lineEndX, y);
        }

        // --- Etichette ---
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

            string text = lb.TrapLevel.ToString("F0");
            SizeF sz = g.MeasureString(text, font);

            int pad = 3;
            Color lineColor = lb.TrapLevel > 0 ? BullColor : BearColor;
            int alpha = Math.Min(200, Math.Max(30, (int)Math.Abs(lb.TrapLevel) * 3));

            using var bgBrush = new SolidBrush(Color.FromArgb(alpha, lineColor));
            g.FillRectangle(bgBrush, x - sz.Width / 2 - pad, y - sz.Height / 2 + 1, sz.Width + pad * 2, sz.Height);

            using var textBrush = new SolidBrush(Color.FromArgb(alpha, Color.White));
            g.DrawString(text, font, textBrush, x - sz.Width / 2, y - sz.Height / 2 + 1);
        }
    }
}