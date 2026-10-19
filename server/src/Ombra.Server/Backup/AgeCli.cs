using System.Diagnostics;

namespace Ombra.Server.Backup;

/// <summary>
/// Thin wrapper over the <c>age</c> binary (https://age-encryption.org). Plaintext only flows through pipes:
/// encryption reads the archive from our stdin pipe, decryption hands us age's stdout.
/// </summary>
public sealed class AgeCli(string agePath)
{
    public async Task EncryptAsync(string outPath, IReadOnlyList<string> recipientArgs, Func<Stream, Task> writePlaintext, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(agePath) { RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("--encrypt");
        foreach (var a in recipientArgs) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("--output");
        psi.ArgumentList.Add(outPath);
        using var p = Start(psi);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        try
        {
            await using (var stdin = p.StandardInput.BaseStream)
                await writePlaintext(stdin);
        }
        catch
        {
            if (!p.HasExited) p.Kill();
            throw;
        }
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0) throw new InvalidOperationException($"age --encrypt failed ({p.ExitCode}): {(await stderr).Trim()}");
    }

    public async Task DecryptAsync(string inPath, string identityPath, Func<Stream, Task> readPlaintext, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(agePath) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("--decrypt");
        psi.ArgumentList.Add("--identity");
        psi.ArgumentList.Add(identityPath);
        psi.ArgumentList.Add(inPath);
        using var p = Start(psi);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        try
        {
            await readPlaintext(p.StandardOutput.BaseStream);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or FormatException)
        {
            // A truncated plaintext stream usually means age refused the ciphertext (authentication failure):
            // report age's own error rather than the reader's symptom.
            using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await p.WaitForExitAsync(grace.Token); } catch (OperationCanceledException) { p.Kill(); }
            if (p.HasExited && p.ExitCode != 0)
                throw new InvalidOperationException($"age --decrypt failed ({p.ExitCode}): {(await stderr).Trim()}", e);
            throw;
        }
        catch
        {
            if (!p.HasExited) p.Kill();
            throw;
        }
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0) throw new InvalidOperationException($"age --decrypt failed ({p.ExitCode}): {(await stderr).Trim()}");
    }

    private Process Start(ProcessStartInfo psi)
    {
        try
        {
            return Process.Start(psi) ?? throw new InvalidOperationException("cannot start " + agePath);
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new InvalidOperationException($"age binary not found ({agePath}): {e.Message}");
        }
    }
}
