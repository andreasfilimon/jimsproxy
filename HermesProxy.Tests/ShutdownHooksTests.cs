using System;
using System.IO;
using Xunit;

namespace HermesProxy.Tests;

// The POSIX signal registrations must outlive a garbage collection. An unheld PosixSignalRegistration is
// finalized and unregisters its handler; SIGTERM then ends the process with 143, so the account-sync flush
// and the final JSONL event never ran when the launcher stopped the proxy (measured 2026-10-02).
public class ShutdownHooksTests
{
    [Fact]
    public void PosixHandlers_SurviveGarbageCollection()
    {
        if (OperatingSystem.IsWindows()) return;
        ShutdownHooks.Install();
        for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }

        Assert.True(ShutdownHooks.PosixRegistrationsHeld);
        if (!OperatingSystem.IsLinux()) return;
        // The kernel's own view: SIGHUP (bit 0) is caught only while our registration lives (the runtime
        // catches SIGTERM itself, so SIGHUP is the bit that tells).
        string status = File.ReadAllText("/proc/self/status");
        ulong caught = Convert.ToUInt64(status.Split("SigCgt:")[1].Trim().Split('\n')[0], 16);
        Assert.True((caught & 1) != 0, $"SIGHUP not caught after GC (SigCgt {caught:x})");
    }
}
