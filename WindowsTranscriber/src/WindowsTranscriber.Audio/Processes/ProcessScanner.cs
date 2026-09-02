using System.Diagnostics;
using WindowsTranscriber.Core.Models;

namespace WindowsTranscriber.Audio.Processes;

public sealed class ProcessScanner
{
    public IReadOnlyList<ApplicationProcess> GetRunningApplications()
    {
        var currentProcessId = Environment.ProcessId;
        var applications = new List<ApplicationProcess>();

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id == currentProcessId ||
                        process.MainWindowHandle == IntPtr.Zero ||
                        string.IsNullOrWhiteSpace(process.MainWindowTitle))
                    {
                        continue;
                    }

                    var processName = process.ProcessName;
                    var windowTitle = process.MainWindowTitle.Trim();

                    applications.Add(new ApplicationProcess(
                        process.Id,
                        processName,
                        CreateDisplayName(processName, windowTitle),
                        windowTitle));
                }
                catch (InvalidOperationException)
                {
                    // The process exited while it was being inspected.
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    // Windows denied access to this process.
                }
                catch (NotSupportedException)
                {
                    // Ignore processes that do not expose desktop-window details.
                }
            }
        }

        return applications
            .OrderBy(application => application.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(application => application.ProcessId)
            .ToArray();
    }

    private static string CreateDisplayName(string processName, string windowTitle)
    {
        if (!string.IsNullOrWhiteSpace(windowTitle))
        {
            return windowTitle;
        }

        return processName.Length == 0
            ? "Unknown application"
            : char.ToUpperInvariant(processName[0]) + processName[1..];
    }
}
