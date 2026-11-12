using System.Globalization;
using System.Text;
using IslandSpike.Motion;

namespace IslandSpike;

/// <summary>
/// Compares what the screen showed, frame by frame, with what the motion engine says should have
/// been shown.
///
/// A change made by the app does not reach the screen at once, and not always after the same delay.
/// So the comparison is done in two steps: first one delay is fitted for the whole run, then each
/// change gets its own delay, within a frame or two of that. What is left after the second step is
/// real disagreement. The fitted model also says how far the position jumped on screen at each
/// change, which is the number that matters for whether an interruption looks seamless.
/// </summary>
static class Analysis
{
    public sealed record Result(string Text, double DelayMs, double RmsPx, double P95Px, double MaxPx,
                                double DelaySpreadMs, double JumpP95Px, double JumpMaxPx, int Frames, int Dropped);

    public static Result Run(string title, List<ScreenProbe.Frame> frames, MotionValue w, MotionValue h, double scale,
                             List<double> commitTimes, HashSet<int> releases, string? csvPath)
    {
        var inv = CultureInfo.InvariantCulture;
        var text = new StringBuilder();
        text.AppendLine(title);

        var seen = frames.Where(f => f.WidthPx > 0 && f.HeightPx > 0).OrderBy(f => f.T).ToList();
        if (seen.Count < 50 || commitTimes.Count == 0)
        {
            text.AppendLine($"  not enough frames to analyse ({seen.Count} with the body visible, {frames.Count} in total)");
            return new Result(text.ToString(), 0, 0, 0, 0, 0, 0, 0, seen.Count, 0);
        }

        // Frame times and our own clock should share a time base. If they do not, shift onto ours.
        var arrivalLag = seen.Select(f => f.Arrival - f.T).OrderBy(x => x).ToList();
        double medianLag = arrivalLag[arrivalLag.Count / 2];
        double shift = Math.Abs(medianLag) > 0.5 ? medianLag : 0;

        double first = commitTimes[0] - 0.2, last = commitTimes[^1] + 1.2;
        var active = seen.Where(f => f.T + shift >= first && f.T + shift <= last).ToList();
        int n = active.Count;
        var t = new double[n];
        var ow = new double[n];
        var oh = new double[n];
        for (int i = 0; i < n; i++) { t[i] = active[i].T + shift; ow[i] = active[i].WidthPx; oh[i] = active[i].HeightPx; }

        double Sse(Func<double, double> pw, Func<double, double> ph, int from, int to)
        {
            double sum = 0;
            for (int i = from; i < to; i++)
            {
                double dw = ow[i] - pw(t[i]) * scale, dh = oh[i] - ph(t[i]) * scale;
                sum += dw * dw + dh * dh;
            }
            return sum;
        }

        // Step 1: one delay for everything.
        double global = 0, best = double.MaxValue;
        for (double d = -0.25; d <= 0.25; d += 0.002)
        {
            double s = Sse(w.Predictor(_ => d), h.Predictor(_ => d), 0, n);
            if (s < best) { best = s; global = d; }
        }
        double coarse = global;
        for (double d = coarse - 0.003; d <= coarse + 0.003; d += 0.0002)
        {
            double s = Sse(w.Predictor(_ => d), h.Predictor(_ => d), 0, n);
            if (s < best) { best = s; global = d; }
        }
        double uniformRms = Math.Sqrt(best / (2.0 * n));

        // Step 2: each change gets its own delay.
        var delay = Enumerable.Repeat(global, commitTimes.Count).ToArray();
        var fitted = new bool[commitTimes.Count];
        Func<int, double> latency = c => c >= 0 && c < delay.Length ? delay[c] : global;
        int LowerBound(double x) { int i = Array.BinarySearch(t, x); return i < 0 ? ~i : i; }

        for (int pass = 0; pass < 2; pass++)
            for (int c = 0; c < commitTimes.Count; c++)
            {
                int from = LowerBound(commitTimes[c] + global - 0.03), to = LowerBound(commitTimes[c] + global + 0.15);
                if (to - from < 3) continue;
                double keep = delay[c], bestHere = double.MaxValue;
                for (double candidate = global - 0.024; candidate <= global + 0.0361; candidate += 0.0005)
                {
                    delay[c] = candidate;
                    double s = Sse(w.Predictor(latency), h.Predictor(latency), from, to);
                    if (s < bestHere) { bestHere = s; keep = candidate; }
                }
                delay[c] = keep;
                fitted[c] = true;
            }

        var predictW = w.Predictor(latency);
        var predictH = h.Predictor(latency);

        // What is left over.
        var all = new List<double>(n * 2);
        var csv = csvPath == null ? null : new StringBuilder("t,width_px,height_px,predicted_width_px,predicted_height_px\n");
        for (int i = 0; i < n; i++)
        {
            double pw = predictW(t[i]) * scale, ph = predictH(t[i]) * scale;
            all.Add(Math.Abs(ow[i] - pw));
            all.Add(Math.Abs(oh[i] - ph));
            csv?.Append(string.Create(inv, $"{t[i]:F4},{ow[i]},{oh[i]},{pw:F2},{ph:F2}\n"));
        }
        if (csv != null && csvPath != null) File.WriteAllText(csvPath, csv.ToString());

        // How far the position stepped on screen at the instant each change took effect, beyond the
        // distance it would have covered anyway.
        var jumps = new List<double>();
        var releaseJumps = new List<double>();
        const double eps = 5e-5;
        for (int c = 0; c < commitTimes.Count; c++)
        {
            if (!fitted[c]) continue;
            double at = commitTimes[c] + delay[c];
            double Jump(Func<double, double> p)
            {
                double across = p(at + eps) - p(at - eps);
                double before = p(at - eps) - p(at - 3 * eps);
                return Math.Abs(across - before) * scale;
            }
            double j = Math.Max(Jump(predictW), Jump(predictH));
            jumps.Add(j);
            if (releases.Contains(c)) releaseJumps.Add(j);
        }

        // A dropped frame is a gap of more than one and a half frame times while the body was moving.
        var gaps = new List<double>();
        for (int i = 1; i < n; i++) gaps.Add(t[i] - t[i - 1]);
        gaps.Sort();
        double frameTime = gaps[gaps.Count / 2];
        int dropped = 0;
        for (int i = 1; i < n; i++)
        {
            double gap = t[i] - t[i - 1];
            if (gap <= frameTime * 1.5) continue;
            double mid = t[i - 1] + frameTime;
            double speed = Math.Abs(predictW(mid + 0.004) - predictW(mid - 0.004)) / 0.008 +
                           Math.Abs(predictH(mid + 0.004) - predictH(mid - 0.004)) / 0.008;
            if (speed * scale > 60) dropped += (int)Math.Round(gap / frameTime) - 1;
        }

        all.Sort();
        jumps.Sort();
        var fittedDelays = delay.Where((_, c) => fitted[c]).ToList();
        double meanDelay = fittedDelays.Count > 0 ? fittedDelays.Average() : global;
        double spread = fittedDelays.Count > 0 ? Math.Sqrt(fittedDelays.Sum(d => (d - meanDelay) * (d - meanDelay)) / fittedDelays.Count) : 0;
        double Rms(List<double> v) => v.Count == 0 ? 0 : Math.Sqrt(v.Sum(x => x * x) / v.Count);
        double Pct(List<double> v, double p) => v.Count == 0 ? 0 : v[Math.Min(v.Count - 1, (int)(v.Count * p))];
        double Max(List<double> v) => v.Count == 0 ? 0 : v.Max();

        text.AppendLine(string.Create(inv, $"  frames analysed              {n}  (frame time {frameTime * 1000:F2} ms = {1 / frameTime:F1} Hz)"));
        text.AppendLine(string.Create(inv, $"  changes of target            {commitTimes.Count}, of which {releases.Count} were drag releases"));
        text.AppendLine(string.Create(inv, $"  delay from call to screen    mean {meanDelay * 1000:F1} ms, varying by {spread * 1000:F1} ms (std), range {(fittedDelays.Count > 0 ? fittedDelays.Min() * 1000 : 0):F0}-{(fittedDelays.Count > 0 ? fittedDelays.Max() * 1000 : 0):F0} ms"));
        text.AppendLine(string.Create(inv, $"  screen vs model, one delay   rms {uniformRms:F2} px"));
        text.AppendLine(string.Create(inv, $"  screen vs model, own delays  rms {Rms(all):F2} px   median {Pct(all, 0.5):F2}   95% {Pct(all, 0.95):F2}   99% {Pct(all, 0.99):F2}   max {all[^1]:F2}"));
        text.AppendLine(string.Create(inv, $"  position jump at a change    median {Pct(jumps, 0.5):F2} px   95% {Pct(jumps, 0.95):F2}   max {Max(jumps):F2}"));
        text.AppendLine(string.Create(inv, $"  position jump at a release   max {Max(releaseJumps):F2} px   ({releaseJumps.Count} releases)"));
        text.AppendLine(string.Create(inv, $"  dropped frames while moving  {dropped}"));

        return new Result(text.ToString(), meanDelay * 1000, Rms(all), Pct(all, 0.95), all[^1], spread * 1000,
                          Pct(jumps, 0.95), Max(jumps), n, dropped);
    }
}
