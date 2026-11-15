using System.Diagnostics;
using System.Net;
using OpenNetLimit.Core;
using OpenNetLimit.Core.Models;
using OpenNetLimit.Engine.Interception;
using OpenNetLimit.Engine.Monitoring;
using OpenNetLimit.Engine.Rules;
using Xunit;

namespace OpenNetLimit.Tests;

/// <summary>A program copied to a Windows name (lsass.exe, svchost.exe) outside Windows' folders gets no protection.</summary>
public class ProtectedNameTests
{
    private static readonly string Windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static readonly string Copies = Path.Combine(Path.GetTempPath(), "copies");

    [Theory]
    [InlineData(@"System32\lsass.exe", true)]
    [InlineData(@"SYSTEM32\SVCHOST.EXE", true)]
    [InlineData(@"SysWOW64\svchost.exe", true)]
    [InlineData(@"System32\drivers\lsass.exe", false)]
    [InlineData(@"Temp\svchost.exe", false)]
    // A program can't run from Sysnative, so a real folder by that name is someone's own.
    [InlineData(@"Sysnative\lsass.exe", false)]
    [InlineData(@"lsass.exe", false)]
    public void WindowsFolders_HoldOnlyProgramsDirectlyInSystem32OrSysWOW64(string underWindows, bool contained)
    {
        var path = Path.Combine(Windows, underWindows);

        Assert.Equal(contained, WindowsFolders.Contain(path));
        Assert.Equal(contained, WindowsFolders.Contain($"\"{path}\""));
        Assert.Equal(contained, WindowsFolders.Contain(path.Replace('\\', '/')));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("lsass.exe")]
    [InlineData(@"Q:\Windows\System32\lsass.exe")]
    public void WindowsFolders_HoldNoPathWithoutOneOfThoseFolders(string? path)
    {
        // Q: stands for any other drive. Windows runs from one place.
        Assert.False(WindowsFolders.Contain(path));
    }

    [Fact]
    public void ABlockOnLsass_DropsACopyNamedLsass_ButNeverTheRealOne()
    {
        var rules = new RuleEngine();
        var hosts = new FakeServiceHosts();
        var block = new BandwidthRule { ProcessName = "lsass", Action = RuleAction.Block };
        rules.AddRule(block);

        bool Dropped(string? path, bool quotaBlocked = false)
        {
            var (rule, protection) = WinDivertInterceptor.Attribute(rules, hosts, 300, "lsass", path,
                IPAddress.Parse("93.184.216.34"), 443, "Udp", resolvedDomain: null);
            Assert.Equal(block.Id, rule?.Id);
            return WinDivertInterceptor.Drops(protection, rule, quotaBlocked);
        }

        Assert.True(Dropped(Path.Combine(Copies, "lsass.exe")));
        Assert.False(Dropped(Path.Combine(Windows, "System32", "lsass.exe")));
        // With no path to go on, the name is trusted as before.
        Assert.False(Dropped(null));
    }

    [Fact]
    public void ARuleOnAFolder_ReachesACopyNamedSvchostThere_ButNeverARealSvchost()
    {
        var rules = new RuleEngine();
        var hosts = new FakeServiceHosts();
        var copies = new BandwidthRule { ProcessPath = Path.Combine(Copies, "*"), Action = RuleAction.Block };
        rules.AddRule(copies);
        rules.AddRule(new BandwidthRule { ProcessPath = Path.Combine(Windows, "System32", "*"), Action = RuleAction.Block });
        hosts.Set(401, "BITS");

        (BandwidthRule? Rule, bool Dropped) Packet(uint processId, string? path)
        {
            var (rule, protection) = WinDivertInterceptor.Attribute(rules, hosts, processId, "svchost", path,
                IPAddress.Parse("93.184.216.34"), 443, "Tcp", resolvedDomain: null);
            return (rule, WinDivertInterceptor.Drops(protection, rule, quotaBlocked: false));
        }

        // The service manager never lists services in the copy, so it used to stay protected for good.
        var copy = Packet(400, Path.Combine(Copies, "svchost.exe"));
        Assert.Equal(copies.Id, copy.Rule?.Id);
        Assert.True(copy.Dropped);
        // A real svchost is reached only through a service in it, so a rule on its folder skips it.
        var real = Packet(401, Path.Combine(Windows, "System32", "svchost.exe"));
        Assert.Null(real.Rule);
        Assert.False(real.Dropped);
    }

    [Theory]
    [InlineData(@"C:\Tools\svchost.exe", false)]
    [InlineData(@"svchost.exe", true)]
    [InlineData(@"C:\*\svchost.exe", true)]
    [InlineData(@"C:\Tools\svc*.exe", false)]
    public void TargetsServiceHost_TakesACopyInAFolderOfItsOwnForAnotherProgram(string path, bool onSvchost)
    {
        Assert.Equal(onSvchost, new BandwidthRule { ProcessPath = path }.TargetsServiceHost());
        Assert.True(new BandwidthRule { ProcessPath = Path.Combine(Windows, "System32", "svchost.exe") }.TargetsServiceHost());
    }

    [Theory]
    [InlineData("svchost", @"C:\Tools\svchost.exe", false)]
    [InlineData("svchost.exe", @"C:\Tools\SVCHOST.EXE", false)]
    [InlineData("steam", @"C:\Tools\svchost.exe", false)]
    [InlineData("svchost", @"\\server\share\svchost.exe", false)]
    // A folder's name can end in a dot, and the kernel can name a program by its volume.
    [InlineData("svchost", @"C:\Tools.\svchost.exe", false)]
    [InlineData("svchost", @"\Device\HarddiskVolume5\Tools\svchost.exe", false)]
    // Run from a share, even Windows' own file is a copy: no service runs in it, and only it has that path.
    [InlineData("svchost", @"\\localhost\C$\Windows\System32\svchost.exe", false)]
    // Written so it never equals a running program's path, which leaves the name to decide.
    [InlineData("svchost", @"C:\Tools\\svchost.exe", true)]
    [InlineData("svchost", @"C:\Tools\..\Other\svchost.exe", true)]
    [InlineData("svchost.exe", @"""C:\Tools\SVCHOST.EXE""", true)]
    [InlineData("steam", @"""C:\Tools\svchost.exe""", false)]
    [InlineData("svchost", @" C:\Tools\svchost.exe", true)]
    [InlineData("svchost", @"%SystemRoot%\System32\svchost.exe", true)]
    [InlineData("svchost", @"C:\Tools\..\Windows\System32\svchost.exe", true)]
    [InlineData("svchost", @"C:\Tools\svchost.exe ", true)]
    [InlineData("svchost", "C:\\Tools\0\\svchost.exe", true)]
    [InlineData("svchost", @"\\?\C:\Tools\svchost.exe", true)]
    [InlineData("svchost", @"\\.\C:\Tools\svchost.exe", true)]
    [InlineData("svchost", @"C:/Tools/svchost.exe", true)]
    [InlineData("svchost", @"Tools\svchost.exe", true)]
    [InlineData("svchost", @"C:Tools\svchost.exe", true)]
    [InlineData("svchost", null, true)]
    [InlineData("svchost", @"C:\Tools\*", true)]
    [InlineData("svchost", @"C:\*\svchost.exe", true)]
    [InlineData("svchost", @"C:\Tools\steam.exe", true)]
    public void TargetsServiceHost_APathToASvchostDecides_WhateverTheName(string name, string? path, bool onSvchost)
    {
        // Set limit on a copy in the app list names the program and its path.
        Assert.Equal(onSvchost, new BandwidthRule { ProcessName = name, ProcessPath = path }.TargetsServiceHost());
        Assert.True(new BandwidthRule { ProcessName = name, ProcessPath = Path.Combine(Windows, "System32", "svchost.exe") }.TargetsServiceHost());
    }

    [Theory]
    [InlineData("svchost", @"sysWOW64\SVCHOST.EXE")]
    [InlineData("steam", @"SysWOW64\svchost.exe")]
    [InlineData("svchost", @"Sysnative\svchost.exe")]
    [InlineData("steam", @"Sysnative\svchost.exe")]
    [InlineData(null, @"Sysnative\svchost.exe")]
    [InlineData(null, @"System32\svchost.exe")]
    public void TargetsServiceHost_WindowsOwnSvchost_HoweverTheFolderIsSpelled(string? name, string underWindows)
    {
        var path = Path.Combine(Windows, underWindows);
        // The same file by its volume's kernel name, which the service never gives a program on a drive.
        var onVolume = @"\Device\HarddiskVolume3" + path[Path.GetPathRoot(path)!.TrimEnd('\\').Length..];

        Assert.True(new BandwidthRule { ProcessName = name, ProcessPath = path }.TargetsServiceHost());
        Assert.True(new BandwidthRule { ProcessName = name, ProcessPath = path.ToUpperInvariant() }.TargetsServiceHost());
        Assert.True(new BandwidthRule { ProcessName = name, ProcessPath = onVolume }.TargetsServiceHost());
    }

    [Fact]
    public void TargetsServiceHost_AVeryLongPath_IsReadWithoutThrowing()
    {
        // Long paths are fine for a running program.
        var folder = @"C:\" + new string('a', 32760);

        Assert.False(new BandwidthRule { ProcessName = "svchost", ProcessPath = folder + @"\svchost.exe" }.TargetsServiceHost());
        Assert.False(new BandwidthRule { ProcessName = "svchost", ProcessPath = @"\Device\HarddiskVolume3" + folder[2..] + @"\svchost.exe" }.TargetsServiceHost());
        Assert.False(new BandwidthRule { ProcessName = "steam", ProcessPath = folder + @"\steam.exe" }.TargetsServiceHost());
        Assert.True(new BandwidthRule { ProcessName = "svchost", ProcessPath = folder + @"\*" }.TargetsServiceHost());
    }

    [Fact]
    public void ProtectedProcesses_ACopyInAFolderNamedSysnative_IsntWindows()
    {
        var path = Path.Combine(Windows, "Sysnative", "lsass.exe");

        Assert.Equal(ProcessProtection.None, ProtectedProcesses.Of("lsass", null, path));
        Assert.False(WindowsFolders.Contain(path));
    }

    [Fact]
    public void PathOf_ReadsARunningProgramsPath_TheSameWayBothWays()
    {
        using var child = Process.Start(new ProcessStartInfo("ping.exe", "-n 30 127.0.0.1")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true
        })!;
        try
        {
            uint processId = (uint)child.Id;
            var expected = Path.Combine(Environment.SystemDirectory, "PING.EXE");

            Assert.Equal(expected, ProcessImage.PathOf(processId), ignoreCase: true);
            Assert.Equal(expected, ProcessImage.Queried(processId), ignoreCase: true);
            // The kernel's device path comes back with the drive letter.
            Assert.Equal(expected, ProcessImage.FromKernel(processId), ignoreCase: true);
        }
        finally
        {
            child.Kill();
        }
    }

    [Fact]
    public void PathOf_ReadsLsassToo_WhichKeepsItsMemoryFromEveryone()
    {
        var lsass = Process.GetProcessesByName("lsass");
        try
        {
            var processId = (uint)Assert.Single(lsass).Id;

            // Unelevated, opening it fails, and the kernel still names its program.
            var path = ProcessImage.PathOf(processId);
            Assert.Equal(Path.Combine(Environment.SystemDirectory, "lsass.exe"), path, ignoreCase: true);
            Assert.Equal(path, ProcessImage.FromKernel(processId), ignoreCase: true);
            Assert.Equal(ProcessProtection.Windows, ProtectedProcesses.Of("lsass", null, path));
        }
        finally
        {
            foreach (var process in lsass)
                process.Dispose();
        }
    }

    [Fact]
    public void PathOf_TheKernelAndIdleProcess_HaveNoProgram()
    {
        Assert.Null(ProcessImage.PathOf(0));
        Assert.Null(ProcessImage.PathOf(4));
    }

    [Theory]
    [InlineData(@"\Device\Mup\server\share\tool.exe", @"\\server\share\tool.exe")]
    [InlineData(@"\Device\NoSuchVolume\tool.exe", @"\Device\NoSuchVolume\tool.exe")]
    public void DrivePath_TurnsNetworkPathsIntoShares_AndKeepsWhatNoDriveHolds(string device, string expected)
    {
        Assert.Equal(expected, ProcessImage.DrivePath(device));
    }
}
