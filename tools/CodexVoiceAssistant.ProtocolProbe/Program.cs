using CodexVoiceAssistant.Protocol;

await using var client = new CodexAppServerClient(
    new CodexAppServerOptions
    {
        Executable = "codex.exe",
        WorkingDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Codex"),
        TurnTimeout = TimeSpan.FromSeconds(60),
        FirstDeltaTimeout = TimeSpan.FromSeconds(30),
    });

var started = DateTimeOffset.UtcNow;
client.DiagnosticReceived += line => Console.Error.WriteLine($"RAW {line}");
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(75));
var prompt = args.Length == 0
    ? "只回复 PROTOCOL_OK，不要执行任何操作。"
    : string.Join(' ', args);
var result = await client.RunTurnAsync(
    prompt,
    update =>
    {
        if (update.Delta.Length > 0)
        {
            Console.Write(update.Delta);
        }
    },
    timeout.Token);

Console.WriteLine();
Console.WriteLine($"THREAD={result.ThreadId}");
Console.WriteLine($"TURN={result.TurnId}");
Console.WriteLine($"TEXT={result.Text}");
Console.WriteLine($"ELAPSED_MS={(DateTimeOffset.UtcNow - started).TotalMilliseconds:F0}");
