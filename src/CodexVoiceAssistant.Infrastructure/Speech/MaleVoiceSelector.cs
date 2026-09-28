using System.Speech.Synthesis;

namespace CodexVoiceAssistant.Infrastructure.Speech;

public static class MaleVoiceSelector
{
    public const string DefaultSapiVoice = "Microsoft Kangkang";
    public const string DefaultEdgeVoice = "zh-CN-YunyangNeural";
    public const string DefaultKokoroVoice = "zm_098";

    private static readonly HashSet<string> AllowedEdgeMaleVoices =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "zh-CN-YunyangNeural",
            "zh-CN-YunjianNeural",
            "zh-CN-YunxiNeural",
            "zh-CN-YunxiaNeural",
        };

    public static string SelectEdgeVoice(string? requestedVoice)
    {
        return !string.IsNullOrWhiteSpace(requestedVoice)
               && AllowedEdgeMaleVoices.Contains(requestedVoice)
            ? requestedVoice
            : DefaultEdgeVoice;
    }

    public static string SelectKokoroVoice(string? requestedVoice)
    {
        return !string.IsNullOrWhiteSpace(requestedVoice)
               && requestedVoice.StartsWith(
                   "zm_",
                   StringComparison.OrdinalIgnoreCase)
            ? requestedVoice
            : DefaultKokoroVoice;
    }

    public static void SelectSapiVoice(
        SpeechSynthesizer synthesizer,
        string? requestedVoice = null)
    {
        if (!string.IsNullOrWhiteSpace(requestedVoice))
        {
            try
            {
                synthesizer.SelectVoice(requestedVoice);
                return;
            }
            catch (ArgumentException)
            {
            }
        }
        try
        {
            synthesizer.SelectVoice(DefaultSapiVoice);
            return;
        }
        catch (ArgumentException)
        {
        }

        var installedVoices = synthesizer
            .GetInstalledVoices()
            .Where(voice => voice.Enabled)
            .Select(voice => voice.VoiceInfo)
            .ToArray();
        var selected = installedVoices.FirstOrDefault(voice =>
                           string.Equals(
                               voice.Name,
                               requestedVoice,
                               StringComparison.OrdinalIgnoreCase))
                       ?? installedVoices.FirstOrDefault(voice =>
                           string.Equals(
                               voice.Name,
                               DefaultSapiVoice,
                               StringComparison.OrdinalIgnoreCase))
                       ?? installedVoices.FirstOrDefault(voice =>
                           voice.Gender == VoiceGender.Male);
        if (selected is null)
        {
            throw new InvalidOperationException(
                $"没有安装可用的男性语音。"
                + $" 当前声音：{synthesizer.Voice?.Name ?? "unknown"}；"
                + $" 枚举数量：{installedVoices.Length}。");
        }
        synthesizer.SelectVoice(selected.Name);
    }

    public static bool HasInstalledSapiMaleVoice()
    {
        using var synthesizer = new SpeechSynthesizer();
        try
        {
            synthesizer.SelectVoice(DefaultSapiVoice);
            return true;
        }
        catch (ArgumentException)
        {
        }
        return synthesizer
            .GetInstalledVoices()
            .Any(voice =>
                voice.Enabled
                && (voice.VoiceInfo.Gender == VoiceGender.Male
                    || string.Equals(
                        voice.VoiceInfo.Name,
                        DefaultSapiVoice,
                        StringComparison.OrdinalIgnoreCase)));
    }
}
