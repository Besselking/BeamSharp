using System.Globalization;

namespace BeamSharp.Tests;

/// <summary>
/// Byte vectors captured from a real Erlang runtime by <c>test/gen_fixtures.escript</c>. Testing
/// against these rather than against our own encoder is what makes the codec tests meaningful.
/// </summary>
internal static class Fixtures
{
    private static readonly Dictionary<string, byte[]> Data = Load();

    public static byte[] Get(string name) =>
        Data.TryGetValue(name, out var bytes) ? bytes : throw new KeyNotFoundException($"no fixture '{name}'");

    private static Dictionary<string, byte[]> Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures.txt");
        var result = new Dictionary<string, byte[]>();

        foreach (var line in File.ReadAllLines(path))
        {
            if (line.Length == 0) continue;
            var parts = line.Split('|');
            var hex = parts[1];
            var bytes = new byte[hex.Length / 2];
            for (var i = 0; i < bytes.Length; i++)
                bytes[i] = byte.Parse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            result[parts[0]] = bytes;
        }

        return result;
    }
}
