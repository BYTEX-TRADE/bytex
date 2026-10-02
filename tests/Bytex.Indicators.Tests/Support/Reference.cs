namespace Bytex.Indicators.Tests.Support;

/// <summary>
/// Naive textbook implementations, written from the published formulas and recomputed from scratch for every
/// index (O(n * period)). They share no code with the engine. A <c>null</c> entry means "not defined yet"
/// under the textbook definition.
/// </summary>
internal static class Reference
{
    /// <summary>Arithmetic mean of the last <paramref name="period"/> values.</summary>
    public static decimal?[] Sma(IReadOnlyList<decimal> x, int period)
    {
        decimal?[] result = new decimal?[x.Count];
        for (int i = period - 1; i < x.Count; i++)
        {
            decimal sum = 0m;
            for (int j = i - period + 1; j <= i; j++)
            {
                sum += x[j];
            }

            result[i] = sum / period;
        }

        return result;
    }

    /// <summary>
    /// EMA with alpha = 2 / (period + 1), seeded with the first observation (the convention of pandas
    /// <c>ewm(adjust=False)</c>), which is the one the engine follows.
    /// </summary>
    public static decimal[] EmaSeededWithFirstValue(IReadOnlyList<decimal> x, int period)
    {
        decimal alpha = 2m / (period + 1);
        decimal[] result = new decimal[x.Count];
        for (int i = 0; i < x.Count; i++)
        {
            result[i] = i == 0 ? x[0] : alpha * x[i] + (1m - alpha) * result[i - 1];
        }

        return result;
    }

    /// <summary>Linearly weighted mean: the newest value has weight <paramref name="period"/>, the oldest weight 1.</summary>
    public static decimal?[] Wma(IReadOnlyList<decimal> x, int period)
    {
        decimal?[] result = new decimal?[x.Count];
        decimal weights = period * (period + 1) / 2m;
        for (int i = period - 1; i < x.Count; i++)
        {
            decimal sum = 0m;
            for (int w = 1; w <= period; w++)
            {
                sum += x[i - period + w] * w;
            }

            result[i] = sum / weights;
        }

        return result;
    }

    /// <summary>
    /// Hull: WMA(2 * WMA(n / 2) - WMA(n), round(sqrt(n))), defined once the inner full-length WMA has produced
    /// round(sqrt(n)) values, that is from input n + round(sqrt(n)) - 1.
    /// </summary>
    public static decimal?[] Hma(IReadOnlyList<decimal> x, int period)
    {
        int half = Math.Max(1, period / 2);
        int root = Math.Max(1, (int)Math.Round(Math.Sqrt(period)));
        decimal?[] fast = Wma(x, half);
        decimal?[] slow = Wma(x, period);
        List<decimal> raw = [];
        for (int i = period - 1; i < x.Count; i++)
        {
            raw.Add(2m * fast[i]!.Value - slow[i]!.Value);
        }

        decimal?[] smoothed = Wma(raw, root);
        decimal?[] result = new decimal?[x.Count];
        for (int i = 0; i < smoothed.Length; i++)
        {
            result[i + period - 1] = smoothed[i];
        }

        return result;
    }

    /// <summary>Mulloy's DEMA: 2 * EMA - EMA(EMA), both EMAs seeded with their first observation.</summary>
    public static decimal[] Dema(IReadOnlyList<decimal> x, int period)
    {
        decimal[] first = EmaSeededWithFirstValue(x, period);
        decimal[] second = EmaSeededWithFirstValue(first, period);
        return first.Select((v, i) => 2m * v - second[i]).ToArray();
    }

    /// <summary>
    /// Wilder RSI: the first average gain/loss is the simple mean of the first N changes, later ones are
    /// smoothed as (previous * (N - 1) + current) / N. No losses at all gives 100.
    /// </summary>
    public static decimal?[] RsiWilder(IReadOnlyList<decimal> x, int period)
    {
        decimal?[] result = new decimal?[x.Count];
        decimal averageGain = 0m;
        decimal averageLoss = 0m;
        for (int i = 1; i < x.Count; i++)
        {
            decimal change = x[i] - x[i - 1];
            decimal gain = Math.Max(change, 0m);
            decimal loss = Math.Max(-change, 0m);
            if (i < period)
            {
                averageGain += gain;
                averageLoss += loss;
                continue;
            }

            if (i == period)
            {
                averageGain = (averageGain + gain) / period;
                averageLoss = (averageLoss + loss) / period;
            }
            else
            {
                averageGain = (averageGain * (period - 1) + gain) / period;
                averageLoss = (averageLoss * (period - 1) + loss) / period;
            }

            result[i] = averageLoss == 0m ? 100m : 100m - 100m / (1m + averageGain / averageLoss);
        }

        return result;
    }

    /// <summary>MACD line = EMA(fast) - EMA(slow); signal = EMA(signal) of the MACD line; first-value seeding throughout.</summary>
    public static (decimal[] Macd, decimal[] Signal) Macd(IReadOnlyList<decimal> x, int fast, int slow, int signal)
    {
        decimal[] fastEma = EmaSeededWithFirstValue(x, fast);
        decimal[] slowEma = EmaSeededWithFirstValue(x, slow);
        decimal[] macd = fastEma.Select((v, i) => v - slowEma[i]).ToArray();
        return (macd, EmaSeededWithFirstValue(macd, signal));
    }

    /// <summary>Percentage change against the value <paramref name="period"/> inputs ago.</summary>
    public static decimal?[] Roc(IReadOnlyList<decimal> x, int period)
    {
        decimal?[] result = new decimal?[x.Count];
        for (int i = period; i < x.Count; i++)
        {
            result[i] = (x[i] - x[i - period]) / x[i - period] * 100m;
        }

        return result;
    }

    /// <summary>
    /// Lane's stochastic: %K = (close - lowest low) / (highest high - lowest low) * 100 over kPeriod bars,
    /// %D = SMA(dPeriod) of %K. A zero range is reported as the 50 midpoint.
    /// </summary>
    public static (decimal?[] K, decimal?[] D) Stochastics(IReadOnlyList<Ohlcv> bars, int kPeriod, int dPeriod)
    {
        decimal?[] k = new decimal?[bars.Count];
        decimal?[] d = new decimal?[bars.Count];
        for (int i = kPeriod - 1; i < bars.Count; i++)
        {
            decimal highest = decimal.MinValue;
            decimal lowest = decimal.MaxValue;
            for (int j = i - kPeriod + 1; j <= i; j++)
            {
                highest = Math.Max(highest, bars[j].High);
                lowest = Math.Min(lowest, bars[j].Low);
            }

            k[i] = highest == lowest ? 50m : (bars[i].Close - lowest) / (highest - lowest) * 100m;
        }

        for (int i = kPeriod + dPeriod - 2; i < bars.Count; i++)
        {
            decimal sum = 0m;
            for (int j = i - dPeriod + 1; j <= i; j++)
            {
                sum += k[j]!.Value;
            }

            d[i] = sum / dPeriod;
        }

        return (k, d);
    }

    /// <summary>Granville's OBV: add the volume on an up close, subtract it on a down close, starting from zero.</summary>
    public static decimal[] Obv(IReadOnlyList<Ohlcv> bars)
    {
        decimal[] result = new decimal[bars.Count];
        for (int i = 1; i < bars.Count; i++)
        {
            int direction = Math.Sign(bars[i].Close - bars[i - 1].Close);
            result[i] = result[i - 1] + direction * bars[i].Volume;
        }

        return result;
    }

    /// <summary>True range; the first bar has no previous close, so it is simply high - low.</summary>
    public static decimal[] TrueRange(IReadOnlyList<Ohlcv> bars)
    {
        decimal[] result = new decimal[bars.Count];
        for (int i = 0; i < bars.Count; i++)
        {
            decimal range = bars[i].High - bars[i].Low;
            if (i > 0)
            {
                decimal previousClose = bars[i - 1].Close;
                range = Math.Max(range, Math.Max(Math.Abs(bars[i].High - previousClose), Math.Abs(bars[i].Low - previousClose)));
            }

            result[i] = range;
        }

        return result;
    }

    /// <summary>Wilder ATR: simple mean of the first N true ranges, then (previous * (N - 1) + TR) / N.</summary>
    public static decimal?[] AtrWilder(IReadOnlyList<Ohlcv> bars, int period)
    {
        decimal[] tr = TrueRange(bars);
        decimal?[] result = new decimal?[bars.Count];
        for (int i = period - 1; i < bars.Count; i++)
        {
            result[i] = i == period - 1
                ? tr.Take(period).Sum() / period
                : (result[i - 1]!.Value * (period - 1) + tr[i]) / period;
        }

        return result;
    }

    /// <summary>Bollinger: SMA +/- k population standard deviations (divisor N, as Bollinger defines it).</summary>
    public static (decimal?[] Middle, decimal?[] Upper, decimal?[] Lower) Bollinger(IReadOnlyList<decimal> x, int period, decimal k)
    {
        decimal?[] middle = Sma(x, period);
        decimal?[] upper = new decimal?[x.Count];
        decimal?[] lower = new decimal?[x.Count];
        for (int i = period - 1; i < x.Count; i++)
        {
            decimal mean = middle[i]!.Value;
            decimal squares = 0m;
            for (int j = i - period + 1; j <= i; j++)
            {
                squares += (x[j] - mean) * (x[j] - mean);
            }

            decimal deviation = SqrtByBisection(squares / period);
            upper[i] = mean + k * deviation;
            lower[i] = mean - k * deviation;
        }

        return (middle, upper, lower);
    }

    /// <summary>Highest high / lowest low of the last <paramref name="period"/> bars.</summary>
    public static (decimal?[] Upper, decimal?[] Lower) Donchian(IReadOnlyList<Ohlcv> bars, int period)
    {
        decimal?[] upper = new decimal?[bars.Count];
        decimal?[] lower = new decimal?[bars.Count];
        for (int i = period - 1; i < bars.Count; i++)
        {
            upper[i] = Enumerable.Range(i - period + 1, period).Max(j => bars[j].High);
            lower[i] = Enumerable.Range(i - period + 1, period).Min(j => bars[j].Low);
        }

        return (upper, lower);
    }

    /// <summary>
    /// Wilder's directional movement system ("New Concepts in Technical Trading Systems", 1978):
    /// TR, +DM and -DM are summed over the first N bar-to-bar moves and then smoothed as S - S / N + current;
    /// DI = 100 * DM_N / TR_N; DX = 100 * |+DI - -DI| / (+DI + -DI); the first ADX is the simple mean of the
    /// first N DX values (which exist from move N on), later ones are (ADX * (N - 1) + DX) / N.
    /// </summary>
    public static (decimal?[] PlusDi, decimal?[] MinusDi, decimal?[] Adx) AdxWilder(IReadOnlyList<Ohlcv> bars, int period)
    {
        decimal?[] plusDi = new decimal?[bars.Count];
        decimal?[] minusDi = new decimal?[bars.Count];
        decimal?[] adx = new decimal?[bars.Count];
        decimal[] tr = TrueRange(bars);
        List<decimal> dx = [];
        decimal sumTr = 0m;
        decimal sumPlus = 0m;
        decimal sumMinus = 0m;
        for (int i = 1; i < bars.Count; i++)
        {
            decimal up = bars[i].High - bars[i - 1].High;
            decimal down = bars[i - 1].Low - bars[i].Low;
            decimal plusDm = up > down && up > 0m ? up : 0m;
            decimal minusDm = down > up && down > 0m ? down : 0m;
            if (i <= period)
            {
                sumTr += tr[i];
                sumPlus += plusDm;
                sumMinus += minusDm;
            }
            else
            {
                sumTr = sumTr - sumTr / period + tr[i];
                sumPlus = sumPlus - sumPlus / period + plusDm;
                sumMinus = sumMinus - sumMinus / period + minusDm;
            }

            if (i < period)
            {
                continue;
            }

            decimal plus = sumPlus / sumTr * 100m;
            decimal minus = sumMinus / sumTr * 100m;
            plusDi[i] = plus;
            minusDi[i] = minus;
            dx.Add(plus + minus == 0m ? 0m : Math.Abs(plus - minus) / (plus + minus) * 100m);
            if (dx.Count == period)
            {
                adx[i] = dx.Sum() / period;
            }
            else if (dx.Count > period)
            {
                adx[i] = (adx[i - 1]!.Value * (period - 1) + dx[^1]) / period;
            }
        }

        return (plusDi, minusDi, adx);
    }

    /// <summary>
    /// Chande's Aroon over the last period + 1 bars: 100 * (period - bars since the extreme) / period.
    /// When the extreme occurs more than once the most recent occurrence counts (the TA-Lib rule).
    /// </summary>
    public static (decimal?[] Up, decimal?[] Down) Aroon(IReadOnlyList<Ohlcv> bars, int period)
    {
        decimal?[] upValues = new decimal?[bars.Count];
        decimal?[] downValues = new decimal?[bars.Count];
        for (int i = period; i < bars.Count; i++)
        {
            int sinceHigh = 0;
            int sinceLow = 0;
            for (int back = 1; back <= period; back++)
            {
                if (bars[i - back].High > bars[i - sinceHigh].High)
                {
                    sinceHigh = back;
                }

                if (bars[i - back].Low < bars[i - sinceLow].Low)
                {
                    sinceLow = back;
                }
            }

            upValues[i] = 100m * (period - sinceHigh) / period;
            downValues[i] = 100m * (period - sinceLow) / period;
        }

        return (upValues, downValues);
    }

    /// <summary>Square root by interval halving; slow, but obviously correct and independent of the engine's Newton iteration.</summary>
    public static decimal SqrtByBisection(decimal value)
    {
        decimal low = 0m;
        decimal high = Math.Max(1m, value);
        for (int i = 0; i < 200; i++)
        {
            decimal mid = (low + high) / 2m;
            if (mid * mid > value)
            {
                high = mid;
            }
            else
            {
                low = mid;
            }
        }

        return (low + high) / 2m;
    }
}
