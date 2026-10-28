using Marketgrade.Backend.History;

namespace Marketgrade.Backend;

/// <summary>History settings as the API and the settings database see them.</summary>
/// <param name="KeepDays">Minute statistics, gaps and reconnects older than this are deleted.</param>
public sealed record HistorySettingsDto(int KeepDays)
{
    public static HistorySettingsDto Default { get; } = new(HistoryWriter.DefaultKeepDays);

    /// <summary>Errors keyed by the API's field names, as for the recording settings.</summary>
    public Dictionary<string, string[]> Validate()
    {
        var errors = new Dictionary<string, string[]>();
        if (KeepDays is < 7 or > 3650)
        {
            errors["keepDays"] = ["Must be between 7 and 3,650 days."];
        }
        return errors;
    }
}

/// <summary>The history section of the settings.</summary>
public sealed class HistorySettingsStore(SettingsStore store)
{
    private const string Key = "history";

    public HistorySettingsDto Load() => store.Get<HistorySettingsDto>(Key) ?? HistorySettingsDto.Default;

    public void Save(HistorySettingsDto settings) => store.Set(Key, settings);
}
