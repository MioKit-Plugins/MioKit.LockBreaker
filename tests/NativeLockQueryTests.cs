using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using MioKit.LockBreaker.Models;
using MioKit.LockBreaker.Services;
using Xunit;

namespace MioKit.LockBreaker.Tests;

public sealed class NativeLockQueryTests
{
    [Fact]
    [Trait("Category", "WindowsNative")]
    public async Task FindsTwoFilesAndDirectoryHeldByOnlyOurHelperProcess()
    {
        await using var helper = await HeldTargets.StartAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var result = await Task.Run(() => FileLockQuery.FindLockOwners(
            [helper.FirstFile, helper.SecondFile, helper.DirectoryPath, helper.RootPath], cancellation.Token));

        Assert.True(result.Owners.Any(candidate => candidate.ProcessId == helper.ProcessId),
            $"Expected lock holder PID {helper.ProcessId}; completed={result.CompletedTargetCount}/{result.TotalTargetCount}; "
            + JsonSerializer.Serialize(result.Diagnostics.Where(diagnostic => diagnostic.IsFailure
                || diagnostic.ProcessId == helper.ProcessId)));
        var owner = Assert.IsType<FileLockOwnerItem>(
            Assert.Single(result.Owners, candidate => candidate.ProcessId == helper.ProcessId));
        Assert.Contains(helper.FirstFile, owner.TargetPaths);
        Assert.Contains(helper.SecondFile, owner.TargetPaths);
        Assert.Contains(helper.DirectoryPath, owner.TargetPaths);
        Assert.Contains(helper.RootPath, owner.TargetPaths);
        Assert.Equal(4, result.TotalTargetCount);
        Assert.Equal(4, result.CompletedTargetCount);
        Assert.False(result.HasFailures);
        Assert.All(result.Diagnostics.Where(diagnostic => diagnostic.Kind == LockDiagnosticKind.CoverageLimitation),
            diagnostic => Assert.Null(diagnostic.TargetPath));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [Trait("Category", "WindowsNative")]
    public void FindsThisProcessesTcpAndUdpListenersInOnePidGroup(string addressText)
    {
        var address = IPAddress.Parse(addressText);
        var listener = new TcpListener(address, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var udp = new UdpClient(new IPEndPoint(address, port));

            var result = PortLockQuery.FindLockOwners(port, CancellationToken.None);

            var owner = Assert.IsType<PortLockOwnerItem>(
                Assert.Single(result.Owners, item => item.ProcessId == Environment.ProcessId));
            Assert.Contains(owner.Connections, connection => connection.Protocol == "TCP"
                && connection.State == "Listen" && connection.LocalEndPoint.EndsWith($":{port}"));
            Assert.Contains(owner.Connections, connection => connection.Protocol == "UDP"
                && connection.LocalEndPoint.EndsWith($":{port}"));
            Assert.Equal(1, result.CompletedTargetCount);
            Assert.False(result.HasFailures);
        }
        finally { listener.Stop(); }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(65536)]
    public void InvalidPortHasADiagnosticAndNoOwners(int port)
    {
        var result = PortLockQuery.FindLockOwners(port, CancellationToken.None);

        Assert.Empty(result.Owners);
        Assert.NotEmpty(result.Diagnostics);
        Assert.Equal(0, result.CompletedTargetCount);
        Assert.Equal(LockDiagnosticKind.TargetFailure, Assert.Single(result.Diagnostics).Kind);
    }

    [Fact]
    public void PreCanceledPortQueryStopsBeforeReadingTables()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => PortLockQuery.FindLockOwners(54321, cancellation.Token));
    }

    private sealed class HeldTargets : IAsyncDisposable
    {
        private const string HolderScript = """
            $ErrorActionPreference = 'Stop'
            Add-Type -TypeDefinition @'
            using System;
            using System.Runtime.InteropServices;
            using Microsoft.Win32.SafeHandles;
            public static class LockBreakerTestHandle {
                [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
                public static extern SafeFileHandle CreateFile(string path, uint access, uint share,
                    IntPtr security, uint creation, uint flags, IntPtr template);
            }
            '@
            $targets = ConvertFrom-Json $env:LOCKBREAKER_TEST_TARGETS
            $first = $null
            $second = $null
            $directory = $null
            try {
                $first = [System.IO.File]::Open($targets.FirstFile, 'Open', 'Read', 'ReadWrite')
                $second = [System.IO.File]::Open($targets.SecondFile, 'Open', 'Read', 'ReadWrite')
                $directory = [LockBreakerTestHandle]::CreateFile($targets.Directory, 0, 7,
                    [IntPtr]::Zero, 3, 0x02000000, [IntPtr]::Zero)
                if ($directory.IsInvalid) { throw 'Unable to open test directory handle' }
                [Console]::Out.WriteLine('READY')
                [Console]::Out.Flush()
                [Console]::In.ReadLine() | Out-Null
            } finally {
                if ($directory) { $directory.Dispose() }
                if ($second) { $second.Dispose() }
                if ($first) { $first.Dispose() }
            }
            """;

        private readonly Process process;
        private bool started;
        public string RootPath { get; }
        public string FirstFile { get; }
        public string SecondFile { get; }
        public string DirectoryPath { get; }
        public int ProcessId => process.Id;

        private HeldTargets()
        {
            RootPath = Path.Combine(Path.GetTempPath(), "MioKit.LockBreaker.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RootPath);
            FirstFile = Path.Combine(RootPath, "first.txt");
            SecondFile = Path.Combine(RootPath, "second.txt");
            DirectoryPath = Path.Combine(RootPath, "folder");
            File.WriteAllText(FirstFile, "first");
            File.WriteAllText(SecondFile, "second");
            Directory.CreateDirectory(DirectoryPath);

            var start = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "WindowsPowerShell", "v1.0", "powershell.exe"),
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", HolderScript })
                start.ArgumentList.Add(argument);
            start.Environment["LOCKBREAKER_TEST_TARGETS"] = JsonSerializer.Serialize(new
            {
                FirstFile, SecondFile, Directory = DirectoryPath
            });
            process = new Process { StartInfo = start };
        }

        public static async Task<HeldTargets> StartAsync()
        {
            var holder = new HeldTargets();
            try
            {
                holder.started = holder.process.Start();
                Assert.True(holder.started);
                var ready = await holder.process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
                if (ready != "READY")
                {
                    var error = await holder.process.StandardError.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    throw new InvalidOperationException($"Test lock holder did not become ready: {ready}; {error}");
                }
                return holder;
            }
            catch
            {
                await holder.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (started && !process.HasExited)
                {
                    try
                    {
                        await process.StandardInput.WriteLineAsync("STOP");
                        await process.StandardInput.FlushAsync();
                    }
                    catch (IOException) { /* The helper may have already closed its input. */ }
                    try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                    catch (TimeoutException)
                    {
                        // This Process instance is the helper created above, never a user process.
                        process.Kill();
                        await process.WaitForExitAsync();
                    }
                }
            }
            finally
            {
                process.Dispose();
                File.Delete(FirstFile);
                File.Delete(SecondFile);
                Directory.Delete(DirectoryPath);
                Directory.Delete(RootPath);
            }
        }
    }
}
