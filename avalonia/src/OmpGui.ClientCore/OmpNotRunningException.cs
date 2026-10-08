namespace OmpGui.ClientCore;

/// <summary>A command reached the session while omp was not running (stopped, crashed, or mid-restart).</summary>
public sealed class OmpNotRunningException() : InvalidOperationException("omp is not running");
