using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PRN232_GradingSystem_Worker_Services.Implementations
{
    /// <summary>
    /// Service để track và quản lý các process đang chạy bởi grading system
    /// </summary>
    public sealed class ProcessTrackerService : IDisposable
    {
        private readonly ConcurrentDictionary<int, ProcessInfo> _trackedProcesses = new();
        private readonly object _lock = new();
        private bool _disposed = false;

        /// <summary>
        /// Đăng ký một process để track
        /// </summary>
        public void TrackProcess(int processId, string submissionId, string description)
        {
            if (_disposed) return;

            try
            {
                var process = Process.GetProcessById(processId);
                if (!process.HasExited)
                {
                    _trackedProcesses.TryAdd(processId, new ProcessInfo
                    {
                        ProcessId = processId,
                        SubmissionId = submissionId,
                        Description = description,
                        StartTime = DateTime.UtcNow
                    });
                }
            }
            catch (ArgumentException)
            {
                // Process không tồn tại, ignore
            }
            catch (Exception)
            {
                // Ignore errors
            }
        }

        /// <summary>
        /// Bỏ track một process
        /// </summary>
        public void UntrackProcess(int processId)
        {
            _trackedProcesses.TryRemove(processId, out _);
        }

        /// <summary>
        /// Kill tất cả processes đang được track
        /// </summary>
        public async Task KillAllTrackedProcessesAsync(int timeoutMs = 5000)
        {
            if (_trackedProcesses.IsEmpty) return;

            var processesToKill = _trackedProcesses.Values.ToList();
            var tasks = new List<Task>();

            foreach (var processInfo in processesToKill)
            {
                tasks.Add(KillProcessAsync(processInfo.ProcessId, timeoutMs));
            }

            await Task.WhenAll(tasks);
            _trackedProcesses.Clear();
        }

        /// <summary>
        /// Kill tất cả processes liên quan đến một submission
        /// </summary>
        public async Task KillProcessesForSubmissionAsync(string submissionId, int timeoutMs = 5000)
        {
            var processesToKill = _trackedProcesses.Values
                .Where(p => p.SubmissionId == submissionId)
                .ToList();

            var tasks = processesToKill.Select(p => KillProcessAsync(p.ProcessId, timeoutMs));
            await Task.WhenAll(tasks);

            foreach (var processInfo in processesToKill)
            {
                _trackedProcesses.TryRemove(processInfo.ProcessId, out _);
            }
        }

        /// <summary>
        /// Kill tất cả dotnet và browser processes trong working directory
        /// </summary>
        public void KillAllProcessesInWorkingDirectory(string workingDirectory)
        {
            try
            {
                var normalizedWorkingDir = Path.GetFullPath(workingDirectory).ToLowerInvariant();

                // Kill dotnet processes
                KillProcessesByName("dotnet", normalizedWorkingDir);
                KillProcessesByName("dotnet.exe", normalizedWorkingDir);

                // Kill browser processes
                KillProcessesByName("chrome", normalizedWorkingDir);
                KillProcessesByName("msedge", normalizedWorkingDir);
                KillProcessesByName("firefox", normalizedWorkingDir);
                KillProcessesByName("chromium", normalizedWorkingDir);

                // Force kill using taskkill
                ForceKillProcessesInDirectory(workingDirectory);
            }
            catch (Exception)
            {
                // Ignore errors
            }
        }

        private async Task KillProcessAsync(int processId, int timeoutMs)
        {
            try
            {
                var process = Process.GetProcessById(processId);
                if (process.HasExited) return;

                // Try graceful kill first
                try
                {
                    process.Kill();
                    await process.WaitForExitAsync(new CancellationTokenSource(timeoutMs).Token);
                }
                catch
                {
                    // If graceful kill fails, try force kill
                    try
                    {
                        var startInfo = new ProcessStartInfo
                        {
                            FileName = "taskkill",
                            Arguments = $"/F /PID {processId} /T",
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };

                        using var killProcess = Process.Start(startInfo);
                        killProcess?.WaitForExit(timeoutMs);
                    }
                    catch
                    {
                        // Ignore
                    }
                }
                finally
                {
                    process?.Dispose();
                }
            }
            catch (ArgumentException)
            {
                // Process không tồn tại, ignore
            }
            catch (Exception)
            {
                // Ignore errors
            }
        }

        private void KillProcessesByName(string processName, string workingDirectory)
        {
            try
            {
                var processes = Process.GetProcessesByName(processName.Replace(".exe", ""));
                foreach (var proc in processes)
                {
                    try
                    {
                        if (proc.HasExited)
                        {
                            proc.Dispose();
                            continue;
                        }

                        bool shouldKill = false;
                        try
                        {
                            var processPath = proc.MainModule?.FileName;
                            if (!string.IsNullOrEmpty(processPath))
                            {
                                var normalizedPath = Path.GetFullPath(processPath).ToLowerInvariant();
                                if (normalizedPath.Contains(workingDirectory))
                                {
                                    shouldKill = true;
                                }
                            }
                        }
                        catch (System.ComponentModel.Win32Exception)
                        {
                            // Access denied, try command line check
                            try
                            {
                                var startInfo = new ProcessStartInfo
                                {
                                    FileName = "wmic",
                                    Arguments = $"process where ProcessId={proc.Id} get CommandLine",
                                    RedirectStandardOutput = true,
                                    UseShellExecute = false,
                                    CreateNoWindow = true
                                };

                                using var wmicProcess = Process.Start(startInfo);
                                if (wmicProcess != null)
                                {
                                    wmicProcess.WaitForExit(1000);
                                    var output = wmicProcess.StandardOutput.ReadToEnd();
                                    if (output.ToLowerInvariant().Contains(workingDirectory))
                                    {
                                        shouldKill = true;
                                    }
                                }
                            }
                            catch
                            {
                                // If can't check, and it's in temp/gradingworker, be aggressive
                                if (workingDirectory.Contains("temp") || workingDirectory.Contains("gradingworker"))
                                {
                                    shouldKill = true;
                                }
                            }
                        }

                        if (shouldKill)
                        {
                            try
                            {
                                proc.Kill();
                                proc.WaitForExit(2000);
                            }
                            catch
                            {
                                // Ignore
                            }
                        }

                        proc.Dispose();
                    }
                    catch
                    {
                        proc?.Dispose();
                    }
                }
            }
            catch
            {
                // Ignore
            }
        }

        private void ForceKillProcessesInDirectory(string workingDirectory)
        {
            try
            {
                var normalizedWorkingDir = Path.GetFullPath(workingDirectory).ToLowerInvariant();
                var dotnetProcesses = Process.GetProcessesByName("dotnet")
                    .Concat(Process.GetProcessesByName("dotnet.exe"))
                    .Distinct()
                    .ToList();

                var pidsToKill = new List<int>();

                foreach (var proc in dotnetProcesses)
                {
                    try
                    {
                        if (proc.HasExited)
                        {
                            proc.Dispose();
                            continue;
                        }

                        try
                        {
                            var startInfo = new ProcessStartInfo
                            {
                                FileName = "wmic",
                                Arguments = $"process where ProcessId={proc.Id} get CommandLine",
                                RedirectStandardOutput = true,
                                UseShellExecute = false,
                                CreateNoWindow = true
                            };

                            using var wmicProcess = Process.Start(startInfo);
                            if (wmicProcess != null)
                            {
                                wmicProcess.WaitForExit(1000);
                                var output = wmicProcess.StandardOutput.ReadToEnd();
                                if (output.ToLowerInvariant().Contains(normalizedWorkingDir))
                                {
                                    pidsToKill.Add(proc.Id);
                                }
                            }
                        }
                        catch
                        {
                            try
                            {
                                var processPath = proc.MainModule?.FileName;
                                if (!string.IsNullOrEmpty(processPath))
                                {
                                    var normalizedPath = Path.GetFullPath(processPath).ToLowerInvariant();
                                    if (normalizedPath.Contains(normalizedWorkingDir))
                                    {
                                        pidsToKill.Add(proc.Id);
                                    }
                                }
                            }
                            catch { /* ignore */ }
                        }

                        proc.Dispose();
                    }
                    catch { proc?.Dispose(); }
                }

                // Force kill identified PIDs
                foreach (var pid in pidsToKill)
                {
                    try
                    {
                        var startInfo = new ProcessStartInfo
                        {
                            FileName = "taskkill",
                            Arguments = $"/F /PID {pid} /T",
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };

                        using var process = Process.Start(startInfo);
                        process?.WaitForExit(2000);
                    }
                    catch { /* ignore */ }
                }
            }
            catch
            {
                // Ignore
            }
        }

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;
            
            // Kill all tracked processes synchronously on dispose
            try
            {
                KillAllTrackedProcessesAsync(3000).Wait(5000);
            }
            catch
            {
                // Ignore
            }
        }

        private sealed class ProcessInfo
        {
            public int ProcessId { get; set; }
            public string SubmissionId { get; set; } = string.Empty;
            public string Description { get; set; } = string.Empty;
            public DateTime StartTime { get; set; }
        }
    }
}
