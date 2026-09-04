namespace Cfa835SystemMonitor.Tests;

public sealed class InstanceCoordinationTests
{
    [Theory]
    [InlineData(AppMode.Monitor)]
    [InlineData(AppMode.Diagnose)]
    [InlineData(AppMode.HardwareTest)]
    public void HardwareModesRequireExclusiveDeviceOwnership(AppMode mode)
    {
        Assert.True(InstanceCoordinator.UsesCfaDevice(mode));
    }

    [Theory]
    [InlineData(AppMode.LayoutPreview)]
    [InlineData(AppMode.ListSensors)]
    public void NonHardwareModesDoNotDisruptTheRunningMonitor(AppMode mode)
    {
        Assert.False(InstanceCoordinator.UsesCfaDevice(mode));
    }

    [Fact]
    public void ReplacementResultPreservesShutdownCancellationEvidence()
    {
        ReplacementPreparationResult result = ReplacementPreparationResult.Ready(
            shutdownWasPending: true,
            shutdownCancelled: true);

        Assert.True(result.Accepted);
        Assert.True(result.ShutdownWasPending);
        Assert.True(result.ShutdownCancelled);
        Assert.Null(result.Reason);
    }

    [Theory]
    [InlineData(InstanceRunContext.Foreground, InstanceRunContext.Foreground, true)]
    [InlineData(InstanceRunContext.Foreground, InstanceRunContext.Service, false)]
    [InlineData(InstanceRunContext.Service, InstanceRunContext.Foreground, false)]
    [InlineData(InstanceRunContext.Service, InstanceRunContext.Service, false)]
    public void OnlyForegroundInstancesReplaceOtherForegroundInstances(
        InstanceRunContext requester,
        InstanceRunContext owner,
        bool expected)
    {
        Assert.Equal(expected, InstanceCoordinator.MayReplace(requester, owner));
    }
}
