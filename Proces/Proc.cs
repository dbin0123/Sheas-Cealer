using System;
using System.Diagnostics;
using System.Threading;

namespace Sheas_Cealer_Nix.Proces;

internal abstract class Proc : IDisposable
{
    protected readonly string BinaryPath;
    protected Process? ProcessInstance;
    protected bool Disposed;

    protected event EventHandler? ProcessExited;

    internal Proc(string binaryPath)
    {
        BinaryPath = binaryPath ?? throw new ArgumentNullException(nameof(binaryPath));
    }

    internal virtual void Start(string? args = null)
    {
        if (ProcessInstance != null)
            return;

        var startInfo = new ProcessStartInfo
        {
            FileName = BinaryPath,
            Arguments = args ?? string.Empty,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        try
        {
            ProcessInstance = Process.Start(startInfo);
            if (ProcessInstance != null)
            {
                ProcessInstance.EnableRaisingEvents = true;
                ProcessInstance.Exited += OnProcessExited;
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to start process: {ex.Message}");
        }
    }

    internal virtual void Run(string? workingDirectory, string? args = null)
    {
        if (ProcessInstance != null)
            return;

        var startInfo = new ProcessStartInfo
        {
            FileName = BinaryPath,
            Arguments = args ?? string.Empty,
            WorkingDirectory = workingDirectory,
            UseShellExecute = true
        };

        try
        {
            ProcessInstance = Process.Start(startInfo);
            if (ProcessInstance != null)
            {
                ProcessInstance.EnableRaisingEvents = true;
                ProcessInstance.Exited += OnProcessExited;
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to start process: {ex.Message}");
        }
    }

    protected virtual void OnProcessExited(object? sender, EventArgs e)
    {
        ProcessExited?.Invoke(this, e);
        Process_Exited(sender, e);
    }

    protected abstract void Process_Exited(object? sender, EventArgs e);

    internal int? GetExitCode()
    {
        try
        {
            return ProcessInstance?.HasExited == true ? ProcessInstance.ExitCode : null;
        }
        catch
        {
            return null;
        }
    }

    internal bool IsRunning
    {
        get
        {
            try
            {
                return ProcessInstance != null && !ProcessInstance.HasExited;
            }
            catch
            {
                return false;
            }
        }
    }

    internal void Kill()
    {
        try
        {
            if (ProcessInstance != null && !ProcessInstance.HasExited)
            {
                ProcessInstance.Kill(entireProcessTree: true);
                ProcessInstance.WaitForExit(5000);
            }
        }
        catch
        {
            // Ignore kill errors
        }
    }

    public void Dispose()
    {
        if (!Disposed)
        {
            Kill();
            ProcessInstance?.Dispose();
            ProcessInstance = null;
            Disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
