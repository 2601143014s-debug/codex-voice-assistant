using System.Text;
using CodexVoiceAssistant.Domain;

namespace CodexVoiceAssistant.Infrastructure.Logging;

public sealed class FileAppLogger : IAppLogger, IDisposable
{
    private readonly object _sync = new();
    private readonly StreamWriter _writer;

    public FileAppLogger(string? path = null)
    {
        var filePath = path ?? Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "CodexVoiceAssistant",
            "voice-assistant.log");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        if (File.Exists(filePath)
            && new FileInfo(filePath).Length > 10 * 1024 * 1024)
        {
            File.Move(
                filePath,
                $"{filePath}.1",
                overwrite: true);
        }
        _writer = new StreamWriter(
            new FileStream(
                filePath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite),
            new UTF8Encoding(false))
        {
            AutoFlush = true,
        };
    }

    public void Info(string message) => Write("INFO", message);

    public void Warning(string message) => Write("WARN", message);

    public void Error(string message, Exception? exception = null)
    {
        Write(
            "ERROR",
            exception is null
                ? message
                : $"{message} {exception}");
    }

    public void Stage(TimedStage stage)
    {
        Write(
            "STAGE",
            $"{stage.Name} duration_ms={stage.Duration.TotalMilliseconds:F0} "
            + $"ok={stage.Succeeded} error={stage.Error}");
    }

    private void Write(string level, string message)
    {
        lock (_sync)
        {
            _writer.WriteLine(
                $"{DateTimeOffset.Now:O} [{level}] {message}");
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _writer.Dispose();
        }
    }
}
