using Marketgrade.Engine.Recording;

namespace Marketgrade.Backend;

/// <summary>The recording section of the settings, with the defaults for a fresh install.</summary>
public sealed class RecordingSettingsStore(SettingsStore store, RecordingSettings defaults)
{
    private const string Key = "recording";

    public RecordingSettings Defaults => defaults;

    public RecordingSettings Load() => store.Get<RecordingSettingsDto>(Key)?.ToSettings() ?? defaults;

    public void Save(RecordingSettings settings) => store.Set(Key, RecordingSettingsDto.From(settings));
}
