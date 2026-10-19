using Ombra.App.Services.Speech;
using PortAudioSharp;

namespace Ombra.Tests;

public sealed class AudioLifecycleTests
{
    // PortAudio's documented native reference-counting/device-table semantics, with a mutable host device list.
    // The tests exercise ownership transitions, not a canned microphone result or an echoed transcript.
    private sealed class DeviceTable
    {
        internal string? HostInput = "built-in";
        internal string? CachedInput;
        internal int References, Enumerations;
        internal bool FailInitialize;

        internal void Initialize()
        {
            if (FailInitialize) throw new InvalidOperationException("audio host unavailable");
            if (References++ == 0)
            {
                CachedInput = HostInput;
                Enumerations++;
            }
        }

        internal void Terminate()
        {
            Assert.True(References > 0, "unmatched termination");
            if (--References == 0) CachedInput = null;
        }
    }

    [Fact]
    public void Device_changes_are_seen_after_the_last_capture_or_probe_releases_its_lease()
    {
        var native = new DeviceTable();
        var lifetime = new PortAudioLifetime(native.Initialize, native.Terminate);
        using (var firstCapture = lifetime.Acquire())
        {
            using var secondCapture = lifetime.Acquire();
            native.HostInput = "USB";
            using (lifetime.Acquire()) // self-test while both captures are live
                Assert.Equal("built-in", native.CachedInput);
            firstCapture.Dispose();
            firstCapture.Dispose(); // cannot consume the second capture's native reference
            Assert.Equal(1, native.References);
            Assert.Equal("built-in", native.CachedInput);
            Assert.Equal(1, native.Enumerations);
        }
        Assert.Equal(0, native.References);
        using (lifetime.Acquire())
            Assert.Equal("USB", native.CachedInput);
        native.HostInput = null; // unplugged while idle
        using (lifetime.Acquire())
            Assert.Null(native.CachedInput);
        native.HostInput = "built-in after wake";
        using (lifetime.Acquire())
            Assert.Equal("built-in after wake", native.CachedInput);
        Assert.Equal(4, native.Enumerations);
        Assert.Equal(0, native.References);
    }

    [Fact]
    public void A_failed_initialize_does_not_release_another_capture_and_a_later_attempt_can_recover()
    {
        var native = new DeviceTable();
        var lifetime = new PortAudioLifetime(native.Initialize, native.Terminate);
        using (lifetime.Acquire())
        {
            native.FailInitialize = true;
            var error = Assert.Throws<InvalidOperationException>(() => lifetime.Acquire());
            Assert.Equal("audio host unavailable", error.Message);
            Assert.Equal(1, native.References);
            Assert.Equal("built-in", native.CachedInput);
        }
        native.FailInitialize = false;
        native.HostInput = "reconnected";
        using (lifetime.Acquire())
            Assert.Equal("reconnected", native.CachedInput);
        Assert.Equal(0, native.References);
    }

    [Fact]
    public void Releasing_the_same_lease_concurrently_cannot_terminate_a_live_capture()
    {
        var native = new DeviceTable();
        var lifetime = new PortAudioLifetime(native.Initialize, native.Terminate);
        using var capture = lifetime.Acquire();
        var probe = lifetime.Acquire();
        Parallel.For(0, 32, _ => probe.Dispose());
        Assert.Equal(1, native.References);
        Assert.Equal("built-in", native.CachedInput);
    }

    [Fact]
    public async Task A_new_capture_cannot_initialize_during_the_previous_native_termination()
    {
        using var terminating = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var entering = new ManualResetEventSlim();
        var native = new DeviceTable();
        var lifetime = new PortAudioLifetime(native.Initialize, () =>
        {
            terminating.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            native.Terminate();
        });
        var first = lifetime.Acquire();
        var closing = Task.Run(first.Dispose);
        Task<IDisposable>? opening = null;
        try
        {
            Assert.True(terminating.Wait(TimeSpan.FromSeconds(10)));
            opening = Task.Run(() =>
            {
                entering.Set();
                return lifetime.Acquire();
            });
            Assert.True(entering.Wait(TimeSpan.FromSeconds(10)));
            var entered = Monitor.TryEnter(lifetime.Gate);
            if (entered) Monitor.Exit(lifetime.Gate);
            Assert.False(entered);
            Assert.Equal(1, native.Enumerations);
            native.HostInput = "reconnected during teardown";
        }
        finally { release.Set(); }
        await closing.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(opening);
        using var second = await opening.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("reconnected during teardown", native.CachedInput);
        Assert.Equal(2, native.Enumerations);
    }

    [Fact]
    public void A_termination_error_is_not_hidden_and_the_lease_remains_owned_until_closed()
    {
        var native = new DeviceTable();
        var fail = true;
        var lifetime = new PortAudioLifetime(native.Initialize, () =>
        {
            if (fail) throw new InvalidOperationException("audio host did not close");
            native.Terminate();
        });
        var capture = lifetime.Acquire();
        Assert.Equal("audio host did not close", Assert.Throws<InvalidOperationException>(capture.Dispose).Message);
        Assert.Equal(1, native.References);
        fail = false;
        capture.Dispose();
        capture.Dispose();
        Assert.Equal(0, native.References);
    }

    [Fact]
    public void Native_enumeration_survives_overlapping_probes_and_reinitializes_when_idle_without_recording()
    {
        if (Environment.GetEnvironmentVariable("OMBRA_AUDIO_NATIVE") != "1")
            Assert.Skip("Set OMBRA_AUDIO_NATIVE=1 for native enumeration; this test never opens an audio stream");
        lock (PortAudioLifetime.Shared.Gate)
        {
            for (var cycle = 0; cycle < 3; cycle++)
            {
                using (var owner = PortAudioLifetime.Shared.Acquire())
                {
                    var first = PortAudioLifetime.ProbeDevices(); // same path as --self-test
                    using (PortAudioLifetime.Shared.Acquire())
                    {
                        owner.Dispose();
                        owner.Dispose();
                        Assert.Equal(first.Count, PortAudio.DeviceCount);
                        Assert.Equal(first.DefaultInput, PortAudio.DefaultInputDevice);
                    }
                }
                Assert.Equal((int)ErrorCode.NotInitialized, PortAudio.DeviceCount);
                var refreshed = PortAudioLifetime.ProbeDevices();
                TestContext.Current.TestOutputHelper?.WriteLine($"cycle {cycle + 1}: devices={refreshed.Count}, defaultInput={refreshed.DefaultInput}; native ownership returned to zero");
                Assert.True(refreshed.Count >= 0);
                Assert.Equal((int)ErrorCode.NotInitialized, PortAudio.DeviceCount);
            }
        }
    }
}
