using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Flowbit.BrowserTests.Infrastructure;

/// <summary>
/// Launches one owned application process (the published API or UI), captures
/// its stdout/stderr into the run's artifact directory, discovers a validated
/// loopback listening address, and enforces bounded readiness.
/// Only processes started by this instance are ever stopped by it.
/// </summary>
public sealed partial class HostedProcess : IAsyncDisposable
{
    private static readonly Regex ListeningLine = ListeningLineRegex();

    private readonly ProcessStartInfo startInfo;
    private readonly string name;
    private readonly TimeSpan readyTimeout;
    private readonly TimeSpan stopTimeout;
    private readonly TimeSpan perProbeTimeout;
    private readonly HttpClient http;
    private readonly List<string> lines = [];
    private readonly object gate = new();
    private readonly Queue<string> tail = new();
    private Process? process;
    private string? listeningAddress;
    private Task stdoutPump = Task.CompletedTask;
    private Task stderrPump = Task.CompletedTask;

    private HostedProcess(
        string name,
        ProcessStartInfo startInfo,
        HttpClient http,
        TimeSpan readyTimeout,
        TimeSpan perProbeTimeout,
        TimeSpan stopTimeout)
    {
        this.name = name;
        this.startInfo = startInfo;
        this.http = http;
        this.readyTimeout = readyTimeout;
        this.perProbeTimeout = perProbeTimeout;
        this.stopTimeout = stopTimeout;
    }

    /// <summary>The validated loopback base address the process reported.</summary>
    public string BaseAddress =>
        listeningAddress
        ?? throw new InvalidOperationException(
            $"{name} has not reported a loopback listening address yet.");

    /// <summary>Captured stdout/stderr lines.</summary>
    public IReadOnlyList<string> OutputLines
    {
        get { lock (gate) return [.. lines]; }
    }

    public bool HasExited => process?.HasExited ?? true;

    /// <summary>
    /// Starts <paramref name="dllPath"/> with the given environment overrides.
    /// Inherited application configuration variables are stripped first so a
    /// developer's local settings cannot change the database, JWT, Kestrel
    /// endpoints, or the UI's API URL. The runner's global environment is never
    /// mutated.
    /// </summary>
    public static HostedProcess Start(
        string name,
        string dllPath,
        string contentRoot,
        string runDirectory,
        IReadOnlyDictionary<string, string?> environment,
        HttpClient http,
        TimeSpan? readyTimeout = null,
        TimeSpan? perProbeTimeout = null,
        TimeSpan? stopTimeout = null)
    {
        if (!File.Exists(dllPath))
        {
            throw new InvalidOperationException(
                $"Published host '{name}' not found: {dllPath}. Publish it first, for example:\n" +
                $"  dotnet publish <project>.csproj -c Release -o artifacts/browser/hosts/{name.ToLowerInvariant()} /p:UseAppHost=false");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = contentRoot,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add(dllPath);
        startInfo.ArgumentList.Add("--contentRoot");
        startInfo.ArgumentList.Add(contentRoot);
        startInfo.ArgumentList.Add("--environment");
        startInfo.ArgumentList.Add("Development");
        startInfo.ArgumentList.Add("--urls");
        startInfo.ArgumentList.Add("http://127.0.0.1:0");

        // Explicit launch arguments override launch profiles and ambient URL
        // settings; direct DLL launch never reads launchSettings.json. Strip
        // inherited application configuration that could redirect the database,
        // JWT, endpoints, or the UI's API base URL before applying test values.
        SanitizeEnvironment(startInfo.Environment);
        foreach (var (key, value) in environment)
        {
            if (key is not null)
            {
                startInfo.Environment[key] = value ?? string.Empty;
            }
        }

        var process = new HostedProcess(
            name,
            startInfo,
            http,
            readyTimeout ?? TimeSpan.FromSeconds(120),
            perProbeTimeout ?? TimeSpan.FromSeconds(5),
            stopTimeout ?? TimeSpan.FromSeconds(30));

        process.Start(runDirectory);
        return process;
    }

    private void Start(string runDirectory)
    {
        var stdoutWriter = new StreamWriter(
            File.Create(Path.Combine(runDirectory, $"{name}-stdout.log")), Encoding.UTF8) { AutoFlush = true };
        var stderrWriter = new StreamWriter(
            File.Create(Path.Combine(runDirectory, $"{name}-stderr.log")), Encoding.UTF8) { AutoFlush = true };

        var proc = new Process { StartInfo = startInfo };

        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            stdoutWriter.Dispose();
            stderrWriter.Dispose();
            throw new InvalidOperationException($"{name} could not be launched: {ex.Message}", ex);
        }

        process = proc;
        stdoutPump = PumpAsync(proc.StandardOutput, stdoutWriter);
        stderrPump = PumpAsync(proc.StandardError, stderrWriter);
    }

    private async Task PumpAsync(StreamReader reader, StreamWriter writer)
    {
        // Process exit does not mean the redirected pipes have reached EOF.
        // Each pump owns its writer until all buffered output has been drained.
        await using (writer)
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                OnOutput(line, writer);
            }
        }
    }

    private Task DrainOutputAsync() => Task.WhenAll(stdoutPump, stderrPump).WaitAsync(stopTimeout);

    private void OnOutput(string? line, StreamWriter writer)
    {
        if (line is null)
        {
            return;
        }
        lock (gate)
        {
            lines.Add(line);
            tail.Enqueue(line);
            while (tail.Count > 400)
            {
                _ = tail.Dequeue();
            }
        }
        try
        {
            writer.WriteLine(line);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Diagnostics only; never fail the host because a log write failed.
        }

        var match = ListeningLine.Match(line);
        if (match.Success)
        {
            TryAcceptListeningAddress(match.Groups["url"].Value);
        }
    }

    private void TryAcceptListeningAddress(string candidate)
    {
        var trimmed = candidate.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return;
        }
        if (!string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase))
        {
            return; // Only exact loopback IPv4 addresses are accepted.
        }
        if (uri.Scheme != Uri.UriSchemeHttp)
        {
            return;
        }
        lock (gate)
        {
            listeningAddress = trimmed;
        }
    }

    /// <summary>
    /// Waits until the process reported a loopback address and the readiness
    /// endpoint returns HTTP success. A process that exits (even with code 0)
    /// before readiness fails setup: application entry points catch and log
    /// startup failures, so a silent exit is never a success.
    /// </summary>
    public async Task<string> WaitForReadyAsync(string readyPath, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + readyTimeout;
        while (true)
        {
            if (process is null || process.HasExited)
            {
                await DrainOutputAsync();
                throw new InvalidOperationException(
                    $"{name} exited (code {(process?.ExitCode.ToString() ?? "unknown")}) before becoming ready.{Environment.NewLine}{TailText()}");
            }

            var address = listeningAddress;
            if (address is not null)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(perProbeTimeout);
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken, timeout.Token);
                    using var request = new HttpRequestMessage(
                        HttpMethod.Get,
                        new Uri(new Uri(address), readyPath));
                    using var response = await http.SendAsync(request, linked.Token);
                    if (response.IsSuccessStatusCode)
                    {
                        return address;
                    }
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    // Not ready yet; keep probing until the deadline.
                }
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException(
                    $"{name} did not become ready within {readyTimeout.TotalSeconds:0}s.{Environment.NewLine}{TailText()}");
            }
            await Task.Delay(250, cancellationToken);
        }
    }

    public async Task StopAsync()
    {
        if (process is null || process.HasExited)
        {
            await DrainOutputAsync();
            return;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                // Kill the whole tree so no child host ever survives teardown.
                using var killer = Process.Start(
                    new ProcessStartInfo("taskkill", $"/PID {process.Id} /T /F")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    });
                if (killer is not null)
                {
                    await killer.WaitForExitAsync().WaitAsync(stopTimeout);
                }
            }
            else
            {
                using var killer = Process.Start(new ProcessStartInfo("kill", $"-TERM {process.Id}")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                if (killer is not null) await killer.WaitForExitAsync().WaitAsync(stopTimeout);
            }

            await process.WaitForExitAsync().WaitAsync(stopTimeout);
        }
        catch (Exception)
        {
            try
            {
                if (process is { HasExited: false })
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(stopTimeout);
                }
            }
            catch (Exception killFailure)
            {
                throw new InvalidOperationException(
                    $"{name} could not be stopped and may still be running (pid {process?.Id}).{Environment.NewLine}{TailText()}",
                    killFailure);
            }
        }
        await DrainOutputAsync();
    }

    public string TailText()
    {
        List<string> captured;
        lock (gate)
        {
            captured = [.. tail];
        }
        var recent = captured.Count > 60 ? captured[^60..] : captured;
        return "---- latest output ----" + Environment.NewLine + string.Join(Environment.NewLine, recent);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync();
        }
        finally
        {
            process?.Dispose();
            http.Dispose();
        }
    }

    private static void SanitizeEnvironment(IDictionary<string, string?> environment)
    {
        string[] stripPrefixes =
        [
            "ASPNETCORE_",
            "KESTREL_",
            "SERILOG_",
            "JWT__",
            "CONNECTIONSTRINGS__",
            "WORKFLOWAPI__",
            "WORKFLOWDURABLEPROCESSING__",
            "WORKFLOWCONTEXT__",
            "WORKFLOWAUDIT__",
            "FLOWBITWORKER__",
        ];
        string[] stripExact =
        [
            "DOTNET_ENVIRONMENT",
            "DOTNET_URLS",
            "DOTNET_HTTP_PORTS",
            "DOTNET_HTTPS_PORTS",
        ];

        foreach (var key in environment.Keys.ToArray())
        {
            if (stripPrefixes.Any(prefix => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) ||
                stripExact.Any(exact => string.Equals(key, exact, StringComparison.OrdinalIgnoreCase)))
            {
                environment.Remove(key);
            }
        }
    }

    [GeneratedRegex(@"Now listening on: (?<url>https?://\S+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ListeningLineRegex();
}
