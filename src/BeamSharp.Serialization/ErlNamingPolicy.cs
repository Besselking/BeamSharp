using System.Text;

namespace BeamSharp.Serialization;

/// <summary>
/// Translates CLR member names into Erlang names. The default is snake_case, because
/// <c>FirstName</c> arriving in Elixir as <c>:first_name</c> is what an Elixir developer expects to
/// see — the point of the whole library is that the other side cannot tell.
/// </summary>
public abstract class ErlNamingPolicy
{
    /// <summary><c>FirstName</c> becomes <c>first_name</c>. The default.</summary>
    public static ErlNamingPolicy SnakeCase { get; } = new SnakeCasePolicy();

    /// <summary><c>FirstName</c> becomes <c>firstName</c>.</summary>
    public static ErlNamingPolicy CamelCase { get; } = new CamelCasePolicy();

    /// <summary>Names are used exactly as they are declared in C#.</summary>
    public static ErlNamingPolicy Unchanged { get; } = new UnchangedPolicy();

    /// <summary>Converts a single name.</summary>
    public abstract string ConvertName(string name);

    private sealed class UnchangedPolicy : ErlNamingPolicy
    {
        public override string ConvertName(string name) => name;
    }

    private sealed class CamelCasePolicy : ErlNamingPolicy
    {
        public override string ConvertName(string name) =>
            name.Length == 0 || char.IsLower(name[0]) ? name : char.ToLowerInvariant(name[0]) + name[1..];
    }

    private sealed class SnakeCasePolicy : ErlNamingPolicy
    {
        public override string ConvertName(string name)
        {
            if (name.Length == 0) return name;

            var sb = new StringBuilder(name.Length + 6);

            for (var i = 0; i < name.Length; i++)
            {
                var c = name[i];

                if (c == '_' || c == '-')
                {
                    Append(sb, '_');
                    continue;
                }

                if (char.IsUpper(c))
                {
                    // Break before an uppercase run that starts a word (aB), and before the last
                    // letter of a run that begins one (HTTPServer -> http_server, not http_s_erver).
                    var startsWord = i > 0 &&
                                     (!char.IsUpper(name[i - 1]) ||
                                      (i + 1 < name.Length && char.IsLower(name[i + 1])));
                    if (startsWord) Append(sb, '_');
                    sb.Append(char.ToLowerInvariant(c));
                    continue;
                }

                // A digit directly after a letter stays attached: Utf8 -> utf8, Base64Url -> base64_url.
                sb.Append(c);
            }

            return sb.ToString();
        }

        private static void Append(StringBuilder sb, char c)
        {
            if (sb.Length > 0 && sb[^1] != '_') sb.Append(c);
        }
    }
}
