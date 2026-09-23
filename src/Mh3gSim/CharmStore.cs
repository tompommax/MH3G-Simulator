using System.Text.Encodings.Web;
using System.Text.Json;
using Mh3gSim.Core;

namespace Mh3gSim;

/// <summary>装備するお守りを %APPDATA%\MH3GSkillSim\charms.json に保存する (1 つだけ。なしなら空の配列)。</summary>
internal static class CharmStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MH3GSkillSim", "charms.json");

    private static readonly JsonSerializerOptions SaveOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // 日本語をそのまま書く
    };

    public static List<Charm> Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<List<Charm>>(File.ReadAllText(FilePath)) ?? []
                : [];
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return [];
        }
    }

    public static void Save(IEnumerable<Charm> charms)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(charms, SaveOptions));
    }
}
