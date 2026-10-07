using MultiSeat.Service;
using Xunit;

namespace MultiSeat.Tests.Sessions;

public class WindowHideHelperTests
{
    [Fact]
    public void AdoptsTheFirstEligibleProcess()
    {
        var selected = WindowHideHelper.SelectAdoptedPid(
            null,
            [101, 202, 303],
            pid => pid >= 200);

        Assert.Equal(202, selected);
    }

    [Fact]
    public void KeepsTheOriginalProcessAfterAdoption()
    {
        var selected = WindowHideHelper.SelectAdoptedPid(
            202,
            [202, 303, 404],
            _ => true);

        Assert.Equal(202, selected);
    }

    [Fact]
    public void DoesNotSwitchToALaterProcessWhenTheOriginalIsGone()
    {
        // This is the regression case: the seat's mstsc was adopted first, then the user
        // launched a normal RDP client. The watcher must terminate when its original process
        // disappears rather than adopting/hiding the user's later mstsc.
        var selected = WindowHideHelper.SelectAdoptedPid(
            202,
            [303],
            _ => true);

        Assert.Equal(202, selected);
    }

    [Fact]
    public void ReturnsNullUntilAnEligibleProcessAppears()
    {
        var selected = WindowHideHelper.SelectAdoptedPid(
            null,
            [101, 102],
            _ => false);

        Assert.Null(selected);
    }
}
