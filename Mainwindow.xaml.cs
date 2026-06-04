using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using Gigasoft.ProEssentials;
using Gigasoft.ProEssentials.Enums;

namespace WirelineVdl
{
    /// <summary>
    /// ProEssentials WPF Wireline VDL — Variable Density Wave Acoustic Log
    ///
    /// v1.1 changes vs v1.0:
    ///   - Custom monochromatic blue color ramp replaces the rainbow preset.
    ///     Eliminates the "rainbow target" artifact at zoom where each cell
    ///     rendered as concentric blue→cyan→green→yellow→orange rings.
    ///     Each amplitude cell now reads as a clean single-hue blue blob,
    ///     matching real wireline VDL aesthetics (white = low amplitude /
    ///     no signal, navy = high amplitude / strong arrival).
    ///   - Injection plateau ratio reduced from 0.95 → 0.85. Lower ratio
    ///     gives the contour-interpolation gaps slightly more visual weight,
    ///     filling the white stripes that appeared between plateaus at
    ///     mid-range zoom levels.
    ///
    /// Demonstrates a wireline VDL (Variable Density Wave) display — the
    /// standard cement bond logging visualization used to evaluate acoustic
    /// coupling between casing, cement, and formation. Time on X (~200–1200 µs),
    /// depth on Y (feet, sealed casing interval), color = waveform amplitude.
    ///
    /// Headline contribution: contour-with-injection technique, developed
    /// in a Gigasoft technical support thread (May 2026):
    ///
    ///   Stacked-bar plotting fails on large VDL datasets — once depth count
    ///   crosses a threshold the chart simply stops rendering. Naive contour
    ///   plotting works at scale but interpolates between sparse depth rows,
    ///   producing diagonal smearing artifacts at wide zoom levels.
    ///
    ///   The fix: replace each real depth row with two synthetic rows
    ///   located near the midpoints to its neighbors. Contour interpolation
    ///   is then confined to thin slivers between flat plateaus. Visually
    ///   reads as cell-discrete, technically remains a smooth contour PE
    ///   can render at full scale.
    ///
    /// Data: FORGE 16A(78)-32 CBL well log, channel VDL, Frame 15B
    ///   Source resolution: 0.125 ft depth steps, 6 µs time samples
    ///   Demo file: 4001 depths × 256 time samples = 1.02M cells (raw)
    ///                                              ≈ 8002 rows post-injection
    ///   Depth interval: 3000–3500 ft
    ///   Source: Utah FORGE Geothermal Data Repository (CC-BY 4.0)
    ///   DOI 10.15121/1814488 — McLennan 2021
    ///
    /// Same chart skeleton as the GigasoftInc HeatmapSpectrogram sample —
    /// PesgoWpf + ContourColors + DuplicateDataX/Y + ComputeShader +
    /// Direct3D Composite2D3D — adapted for wireline conventions.
    ///
    /// Controls:
    ///   Left-click drag   — zoom box
    ///   Mouse wheel       — horizontal + vertical zoom
    ///   Right-click       — context menu (export, print, customize)
    /// </summary>
    public partial class MainWindow : Window
    {
        // ===== Data file =====
        private const string DATA_FILE = "TestData_FORGE_VDL.txt";

        // ===== Contour-injection technique =====
        private const bool USE_CONTOUR_INJECTION = true;

        // Plateau ratio: fraction of each depth's "territory" filled by its
        // flat plateau. Remainder is the contour-interpolation gap.
        //   0.95  → 95% plateau, 5% interpolation (sharp cells, white gaps visible at mid zoom)
        //   0.85  → 85% plateau, 15% interpolation (v1.1 default — best mid-zoom balance)
        //   0.65  → 65% plateau, 35% interpolation (smooth fade, less discrete)
        private const float INJECTION_PLATEAU_RATIO = 0.85f;

        public MainWindow()
        {
            InitializeComponent();

            // Fill the screen vertically — VDL charts are tall by nature
            // (depth axis dominates). Width stays at the XAML default so the
            // window opens in a typical VDL track aspect ratio.
            var work = SystemParameters.WorkArea;
            Height = work.Height;
            Top    = work.Top;
            Left   = work.Left + (work.Width - Width) / 2;
        }

        // -----------------------------------------------------------------------
        // Pesgo1_Loaded — chart initialization
        // -----------------------------------------------------------------------
        void Pesgo1_Loaded(object sender, RoutedEventArgs e)
        {
            float[] depths;
            float[] times;
            float[,] amps;

            try
            {
                LoadVdlTestData(DATA_FILE, out depths, out times, out amps);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Failed to load {DATA_FILE}\n\n{ex.Message}\n\n" +
                    "Make sure the data file is in the same folder as the executable.",
                    "VDL data load error", MessageBoxButton.OK);
                Application.Current.Shutdown();
                return;
            }

            if (USE_CONTOUR_INJECTION)
            {
                (depths, amps) = InjectContourMidpoints(depths, amps, INJECTION_PLATEAU_RATIO);
            }

            ConfigurePesgoChart(depths, times, amps);
        }

        // =======================================================================
        // LoadVdlTestData — FORGE TestData format reader
        //
        // Format:
        //   UTF-16 LE with BOM, tab-separated, CRLF
        //   Column 0: time (µs) — empty when time == 0
        //   Column 1: depth (ft)
        //   Column 2: amplitude — empty/space when null
        //   Order: outer loop = depth, inner loop = time
        // =======================================================================
        private static void LoadVdlTestData(
            string path, out float[] depths, out float[] times, out float[,] amps)
        {
            string[] lines = File.ReadAllLines(path, Encoding.Unicode);
            var inv = CultureInfo.InvariantCulture;

            // Pass 1 — discover dimensions
            var uniqueDepths = new List<float>();
            var uniqueTimes = new List<float>();
            float lastDepth = float.NaN;
            bool onFirstDepth = true;

            foreach (var line in lines)
            {
                if (line.Length < 3) continue;
                var cols = line.Split('\t');
                if (cols.Length < 3) continue;

                float t = string.IsNullOrWhiteSpace(cols[0])
                        ? 0f
                        : float.Parse(cols[0], inv);
                float d = float.Parse(cols[1], inv);

                if (d != lastDepth)
                {
                    if (uniqueDepths.Count > 0) onFirstDepth = false;
                    uniqueDepths.Add(d);
                    lastDepth = d;
                }

                if (onFirstDepth)
                    uniqueTimes.Add(t);
            }

            int N = uniqueDepths.Count;
            int T = uniqueTimes.Count;

            depths = uniqueDepths.ToArray();
            times = uniqueTimes.ToArray();
            amps = new float[N, T];

            // Pass 2 — fill amplitudes
            int rowIdx = -1;
            int colIdx = 0;
            lastDepth = float.NaN;

            foreach (var line in lines)
            {
                if (line.Length < 3) continue;
                var cols = line.Split('\t');
                if (cols.Length < 3) continue;

                float d = float.Parse(cols[1], inv);
                string ampStr = cols[2];
                float a = string.IsNullOrWhiteSpace(ampStr)
                        ? 0f
                        : float.Parse(ampStr, inv);

                if (d != lastDepth)
                {
                    rowIdx++;
                    colIdx = 0;
                    lastDepth = d;
                }

                if (rowIdx < N && colIdx < T)
                {
                    amps[rowIdx, colIdx] = a;
                    colIdx++;
                }
            }
        }

        // =======================================================================
        // InjectContourMidpoints — anti-smearing technique
        //
        // For each original depth d[i], emit two synthetic rows at:
        //   d[i] - plateauHalf   (start of d[i]'s plateau)
        //   d[i] + plateauHalf   (end   of d[i]'s plateau)
        // =======================================================================
        private static (float[] depths, float[,] amps) InjectContourMidpoints(
            float[] origDepths, float[,] origAmps, float plateauRatio)
        {
            int N = origDepths.Length;
            int T = origAmps.GetLength(1);

            if (N < 2)
                return (origDepths, origAmps);

            float[] halfBack = new float[N];
            float[] halfForward = new float[N];

            for (int i = 0; i < N; i++)
            {
                halfBack[i] = (i > 0)
                    ? (origDepths[i - 1] + origDepths[i]) * 0.5f
                    : origDepths[0] - (origDepths[1] - origDepths[0]) * 0.5f;

                halfForward[i] = (i < N - 1)
                    ? (origDepths[i] + origDepths[i + 1]) * 0.5f
                    : origDepths[N - 1] + (origDepths[N - 1] - origDepths[N - 2]) * 0.5f;
            }

            int outN = 2 * N;
            float[] outDepths = new float[outN];
            float[,] outAmps = new float[outN, T];

            for (int i = 0; i < N; i++)
            {
                float center = origDepths[i];
                float territoryWidth = halfForward[i] - halfBack[i];
                float plateauHalf = territoryWidth * plateauRatio * 0.5f;

                outDepths[2 * i] = center - plateauHalf;
                outDepths[2 * i + 1] = center + plateauHalf;

                for (int t = 0; t < T; t++)
                {
                    outAmps[2 * i, t] = origAmps[i, t];
                    outAmps[2 * i + 1, t] = origAmps[i, t];
                }
            }

            return (outDepths, outAmps);
        }

        // =======================================================================
        // ConfigurePesgoChart — same recipe as HeatmapSpectrogram, adapted
        // for wireline conventions
        // =======================================================================
        private void ConfigurePesgoChart(float[] depths, float[] times, float[,] amps)
        {
            int nDepths = depths.Length;
            int nTimes = times.Length;

            // Step 1 — Data dimensions
            Pesgo1.PeData.Subsets = nDepths;
            Pesgo1.PeData.Points = nTimes;

            Pesgo1.PeData.DuplicateDataX = DuplicateData.PointIncrement;
            Pesgo1.PeData.DuplicateDataY = DuplicateData.SubsetIncrement;

            Pesgo1.PeData.X[0, nTimes - 1] = 0;
            Pesgo1.PeData.Y[0, nDepths - 1] = 0;
            Pesgo1.PeData.Z[nDepths - 1, nTimes - 1] = 0;

            // Step 2 — Load X (time, µs), Y (depth, ft), Z (amplitude)
            for (int t = 0; t < nTimes; t++)
                Pesgo1.PeData.X[0, t] = times[t];

            for (int d = 0; d < nDepths; d++)
                Pesgo1.PeData.Y[0, d] = -depths[d];   // negate Y data as we use InvertedYAxis = True set below

            for (int d = 0; d < nDepths; d++)
                for (int t = 0; t < nTimes; t++)
                    Pesgo1.PeData.Z[d, t] = amps[d, t];

            Pesgo1.PeGrid.Option.InvertedYAxis = true; // PE flips axis labels back to positive

            Pesgo1.PeString.XAxisLabel = "Time ( \u00B5s )";
            Pesgo1.PeString.YAxisLabel = "Depth ( ft )";

            Pesgo1.PeConfigure.ImageAdjustLeft = 50;    // add half character padding
            Pesgo1.PeConfigure.ImageAdjustBottom = 50;  // add half character padding

            // Step 3 — Linear Y axis (PE default)

            // Step 4 — Visual styling — dark theme
            Pesgo1.PeColor.BitmapGradientMode = true;
            Pesgo1.PeColor.QuickStyle = QuickStyle.LightNoBorder;
            Pesgo1.PeColor.GraphGradientStyle = GradientStyle.Horizontal;
            Pesgo1.PeColor.GraphGradientStart = System.Windows.Media.Color.FromArgb(255, 245, 235, 200);
            Pesgo1.PeColor.GraphGradientEnd = System.Windows.Media.Color.FromArgb(255, 245, 235, 200);

            Pesgo1.PeGrid.Configure.ManualScaleControlX = ManualScaleControl.Min;
            Pesgo1.PeGrid.Configure.ManualMinX = 200;

            Pesgo1.PeColor.GridBold = true;

            // Step 5 — Contour color plotting method with custom VDL blue ramp
            //
            // v1.1 change: replaced the BlueCyanGreenYellowBrownWhite preset
            // with an explicit 8-stop monochromatic blue ramp matching real
            // wireline VDL aesthetics. Each amplitude cell renders as a single
            // hue blue blob (luminance encodes amplitude) instead of the
            // multi-color "rainbow target" the preset produced.
            //
            // Ramp stops, low to high amplitude:
            //   white → ice → pale sky → sky → medium → strong → dark → navy
            //
            // ContourColorBlends = 10 still applies — controls how many
            // interpolation steps PE inserts between adjacent stops. With 8
            // stops × 10 blends = 70 actual rendered colors. Smooth gradient.
            //
            // NOTE: if ContourColorSet.Custom isn't the right enum value for
            // your PE version, the alternatives to try are .UserDefined or
            // simply omitting the line entirely (some PE versions auto-detect
            // custom mode when ContourColors[] is assigned). If neither works,
            // pe_query.py will give the exact property path.
            Pesgo1.PePlot.Allow.ContourColors = true;
            Pesgo1.PePlot.Allow.ContourColorsShadows = true;
            Pesgo1.PePlot.Allow.RectHeatmap = true;   // new v10.0.0.26 Rectilinear Heatmap 
            
            Pesgo1.PeColor.ContourColorBlends = 10;

            // Switch to user-defined colors mode (replaces .ContourColorSet preset)

            // 8-stop blue ramp for VDL: white at low amplitude, navy at high
            Pesgo1.PeColor.ContourColors.Clear();
            Pesgo1.PeColor.ContourColors[0] = System.Windows.Media.Color.FromRgb(255, 255, 255); // white
            Pesgo1.PeColor.ContourColors[1] = System.Windows.Media.Color.FromRgb(220, 235, 255); // ice
            Pesgo1.PeColor.ContourColors[2] = System.Windows.Media.Color.FromRgb(180, 210, 250); // pale sky
            Pesgo1.PeColor.ContourColors[3] = System.Windows.Media.Color.FromRgb(120, 175, 230); // sky
            Pesgo1.PeColor.ContourColors[4] = System.Windows.Media.Color.FromRgb( 70, 130, 210); // medium
            Pesgo1.PeColor.ContourColors[5] = System.Windows.Media.Color.FromRgb( 35,  85, 175); // strong
            Pesgo1.PeColor.ContourColors[6] = System.Windows.Media.Color.FromRgb( 15,  40, 130); // dark
            Pesgo1.PeColor.ContourColors[7] = System.Windows.Media.Color.FromRgb(  0,  10,  80); // navy

            Pesgo1.PeColor.ContourColorSet = ContourColorSet.ContourColors;

            Pesgo1.PeLegend.ContourLegendPrecision = ContourLegendPrecision.ZeroDecimals;
            Pesgo1.PeLegend.ContourStyle = true;

            Pesgo1.PeLegend.Location = LegendLocation.Top;
            Pesgo1.PeString.ContourLabels[0] = "0";
            Pesgo1.PeString.ContourLabels[35] = "50";
            Pesgo1.PeString.ContourLabels[70] = "100";


            Pesgo1.PePlot.Method = SGraphPlottingMethod.ContourColors;

            Pesgo1.PeUserInterface.Menu.DataShadow = MenuControl.Hide;

            // Step 6 — Zoom and interaction
            Pesgo1.PeUserInterface.Scrollbar.MouseWheelZoomFactor = 1.4F;
            Pesgo1.PeUserInterface.Scrollbar.MouseWheelZoomSmoothness = 2;
            Pesgo1.PeGrid.GridBands = false;

            Pesgo1.PeUserInterface.Allow.ZoomStyle = ZoomStyle.Ro2Not;
            Pesgo1.PeUserInterface.Allow.Zooming = AllowZooming.HorzAndVert;
            Pesgo1.PeUserInterface.Scrollbar.MouseWheelFunction = MouseWheelFunction.HorizontalVerticalZoom;

            Pesgo1.PeUserInterface.Scrollbar.ScrollingVertZoom = true;
            Pesgo1.PeUserInterface.Scrollbar.ScrollingHorzZoom = true;

            // Step 7 — Legend and grid
            Pesgo1.PeGrid.InFront = true;
            Pesgo1.PeGrid.LineControl = GridLineControl.Both;
            Pesgo1.PeGrid.Style = GridStyle.Dot;

            // Step 8 — Disable plot methods irrelevant to a heatmap-only chart
            Pesgo1.PePlot.Allow.Line = false;
            Pesgo1.PePlot.Allow.Point = false;
            Pesgo1.PePlot.Allow.Bar = false;
            Pesgo1.PePlot.Allow.Area = false;
            Pesgo1.PePlot.Allow.Spline = false;
            Pesgo1.PePlot.Allow.SplineArea = false;
            Pesgo1.PePlot.Allow.PointsPlusLine = false;
            Pesgo1.PePlot.Allow.PointsPlusSpline = false;
            Pesgo1.PePlot.Allow.BestFitCurve = false;
            Pesgo1.PePlot.Allow.BestFitLine = false;
            Pesgo1.PePlot.Allow.Stick = false;

            // Step 9 — Titles and fonts
            Pesgo1.PeString.MainTitle = "Wireline VDL — FORGE 16A(78)-32 CBL Acoustic Waveform";
            Pesgo1.PeString.SubTitle = USE_CONTOUR_INJECTION
                ? $"Contour-with-injection (plateau = {INJECTION_PLATEAU_RATIO:F2}, custom blue ramp) — {nDepths} synthetic rows × {nTimes} time samples"
                : $"Contour without injection (raw {nDepths} depth rows × {nTimes} time samples)";

            Pesgo1.PeGrid.Configure.AutoMinMaxPadding = 0;

            Pesgo1.PeFont.FontSize = Gigasoft.ProEssentials.Enums.FontSize.Medium;
            Pesgo1.PeFont.Fixed = true;

            Pesgo1.PeUserInterface.Dialog.Axis = false;
            Pesgo1.PeUserInterface.Dialog.Style = false;
            Pesgo1.PeUserInterface.Dialog.Subsets = false;

            Pesgo1.PeConfigure.TextShadows = TextShadows.BoldText;
            Pesgo1.PeFont.MainTitle.Bold = true;
            Pesgo1.PeFont.SubTitle.Bold = true;
            Pesgo1.PeFont.Label.Bold = true;

            // Step 10 — Export defaults
            Pesgo1.PeSpecial.DpiX = 600;
            Pesgo1.PeSpecial.DpiY = 600;
            Pesgo1.PeUserInterface.Dialog.AllowEmfExport = false;
            Pesgo1.PeUserInterface.Dialog.AllowWmfExport = false;
            Pesgo1.PeUserInterface.Dialog.ExportSizeDef = ExportSizeDef.NoSizeOrPixel;
            Pesgo1.PeUserInterface.Dialog.ExportTypeDef = ExportTypeDef.Png;
            Pesgo1.PeUserInterface.Dialog.ExportDestDef = ExportDestDef.Clipboard;
            Pesgo1.PeUserInterface.Dialog.ExportUnitXDef = "1280";
            Pesgo1.PeUserInterface.Dialog.ExportUnitYDef = "768";
            Pesgo1.PeUserInterface.Dialog.ExportImageDpi = 300;

            // Step 11 — GPU rendering pipeline
            Pesgo1.PeConfigure.Composite2D3D = Composite2D3D.Foreground;
            Pesgo1.PeConfigure.RenderEngine = RenderEngine.Direct3D;
            Pesgo1.PeData.ComputeShader = true;

            // Step 12 — XYZ cursor prompt
            Pesgo1.PeUserInterface.Cursor.PromptLocation = CursorPromptLocation.ToolTip;
            Pesgo1.PeUserInterface.Cursor.PromptTracking = true;
            Pesgo1.PeUserInterface.Cursor.PromptStyle = CursorPromptStyle.XYValues;
            Pesgo1.PeUserInterface.Cursor.HourGlassThreshold = 9999999;

            Pesgo1.PeFunction.Force3dxNewColors = true;
            Pesgo1.PeFunction.Force3dxVerticeRebuild = true;

            // Apply all properties and render
            Pesgo1.PeFunction.ReinitializeResetImage();
            Pesgo1.Invalidate();
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
        }
    }
}
