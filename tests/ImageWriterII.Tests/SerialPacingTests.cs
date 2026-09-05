using ImageWriterII.Core.Ports;
using Xunit;

namespace ImageWriterII.Tests;

/// <summary>
/// The serial write path's two decisions that do not need a real COM port: how long to pause to hold the
/// configured byte rate, and how the ready-line gate reacts to cancellation and to the port closing.
/// </summary>
public class SerialPacingTests
{
    [Fact]
    public void PacingIsDisabledWhenNoRateIsConfigured()
    {
        var d = SerialPrinterPort.Pacing.Next(pacedBytes: 100_000, bytesPerSecond: 0, elapsed: TimeSpan.Zero);
        Assert.Equal(TimeSpan.Zero, d.Delay);
        Assert.False(d.Reset);
    }

    [Fact]
    public void SendingFasterThanTheRateSleepsOffTheDifference()
    {
        // 960 bytes at 480 B/s is two seconds of output; only half a second has passed, so wait 1.5 s.
        var d = SerialPrinterPort.Pacing.Next(960, 480, TimeSpan.FromSeconds(0.5));
        Assert.False(d.Reset);
        Assert.InRange(d.Delay.TotalSeconds, 1.49, 1.51);
    }

    [Fact]
    public void SendingAtTheRateNeitherSleepsNorResets()
    {
        var d = SerialPrinterPort.Pacing.Next(480, 480, TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.Zero, d.Delay);
        Assert.False(d.Reset);
    }

    /// <summary>
    /// Regression: an idle period used to bank unbounded burst credit, so the job after a pause went out at
    /// full line rate with no pacing at all - the exact thing pacing exists to prevent.
    /// </summary>
    [Fact]
    public void LongIdlePeriodsDoNotBankBurstCredit()
    {
        // 480 bytes sent, but an hour has gone by: the deficit must be discarded, not carried forward.
        var d = SerialPrinterPort.Pacing.Next(480, 480, TimeSpan.FromHours(1));
        Assert.Equal(TimeSpan.Zero, d.Delay);
        Assert.True(d.Reset);
    }

    [Fact]
    public void SmallDeficitsAreToleratedWithoutResetting()
    {
        // Half a second behind is normal jitter, well inside the one-second allowance.
        var d = SerialPrinterPort.Pacing.Next(480, 480, TimeSpan.FromSeconds(1.5));
        Assert.Equal(TimeSpan.Zero, d.Delay);
        Assert.False(d.Reset);
    }

    [Fact]
    public void ReadyGateReturnsImmediatelyWhenThePrinterIsReady()
    {
        int polls = 0;
        SerialPrinterPort.WaitUntilReady(() => { polls++; return true; }, () => true, CancellationToken.None);
        Assert.Equal(1, polls);
    }

    [Fact]
    public void ReadyGateWaitsUntilTheLineComesUp()
    {
        int polls = 0;
        SerialPrinterPort.WaitUntilReady(() => ++polls >= 3, () => true, CancellationToken.None);
        Assert.True(polls >= 3);
    }

    /// <summary>
    /// Regression: a write parked on the ready line used to ignore cancellation, so Cancel-Job and service
    /// shutdown hung for as long as the printer stayed off-line.
    /// </summary>
    [Fact]
    public void ReadyGateIsCancellable()
    {
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));
        Assert.Throws<OperationCanceledException>(() =>
            SerialPrinterPort.WaitUntilReady(() => false, () => true, cts.Token));
    }

    [Fact]
    public void ReadyGateGivesUpWhenThePortCloses()
    {
        bool open = true;
        int polls = 0;
        Assert.Throws<IOException>(() =>
            SerialPrinterPort.WaitUntilReady(
                isReady: () => false,
                isOpen: () => { if (++polls >= 3) open = false; return open; },
                ct: CancellationToken.None));
    }
}
