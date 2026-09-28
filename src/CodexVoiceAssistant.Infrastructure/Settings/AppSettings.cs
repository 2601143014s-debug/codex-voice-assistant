namespace CodexVoiceAssistant.Infrastructure.Settings;

public sealed class AppSettings
{
    public string CaptureDeviceId { get; set; } = "";
    public string OutputDeviceId { get; set; } = "";
    public string Voice { get; set; } = "zm_098";
    public string TtsProvider { get; set; } = "kokoro";
    public double KokoroSpeed { get; set; } = 1.08;
    public int VoiceRatePercent { get; set; } = 35;
    public int VoicePitchPercent { get; set; } = -12;
    public string EdgeTtsRate { get; set; } = "+35%";
    public string EdgeTtsPitch { get; set; } = "-10Hz";
    public string ModelProvider { get; set; } = "DeepSeek";
    public string Model { get; set; } = "deepseek-flash";
    public string Effort { get; set; } = "low";
    public string TranscriptionProvider { get; set; } = "sensevoice";
    public string Workspace { get; set; } =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Codex");
    public double VadThreshold { get; set; } = 0.005;
    public int SilenceMilliseconds { get; set; } = 650;
    public int MaxUtteranceSeconds { get; set; } = 20;
    public int LargeModelThresholdSeconds { get; set; } = 4;
    public bool StartWithWindows { get; set; } = true;
}
