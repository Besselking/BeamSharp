using BeamSharp.Serialization;
using BeamSharp.Terms;

namespace BeamSharp.Serialization.Tests;

public record Person(string FirstName, int Age);

[ErlStruct("MyApp.Person")]
public record ElixirPerson(string FirstName, int Age, string? Email = null);

[ErlRecord("point")]
public record Point(int X, int Y);

public enum Status
{
    Active,
    InProgress,
    OnHold,
    [ErlProperty("done")] Completed
}

public class Mutable
{
    public string? Name { get; set; }
    public int Count { get; set; }
    public Status Status { get; set; }
}

public class Annotated
{
    [ErlProperty("id")] public Guid Identifier { get; set; }
    [ErlIgnore] public string Secret { get; set; } = "hidden";
    [ErlAsAtom] public string Level { get; set; } = "info";
    public string HTTPMethod { get; set; } = "GET";
}

public record Nested(Person Owner, List<Person> Friends, Dictionary<string, int> Scores);

public record WithFields
{
    public int Included;
    public string Name { get; init; } = "";
}

public record Money(decimal Amount, string Currency);

public sealed class MoneyAsTupleConverter : ErlConverter<Money>
{
    public override ErlTerm Write(Money value, ErlSerializerOptions options) =>
        Erl.Tuple(Erl.Atom("money"), Erl.Int((long)(value.Amount * 100)),
            Erl.Atom(value.Currency.ToLowerInvariant()));

    public override Money Read(ErlTerm term, ErlSerializerOptions options)
    {
        var t = (ErlTuple)term;
        return new Money(t[1].AsLong() / 100m, t[2].AsText().ToUpperInvariant());
    }
}

[ErlConvert(typeof(TemperatureConverter))]
public readonly record struct Temperature(double Celsius);

public sealed class TemperatureConverter : ErlConverter<Temperature>
{
    public override ErlTerm Write(Temperature value, ErlSerializerOptions options) =>
        Erl.Float(value.Celsius);

    public override Temperature Read(ErlTerm term, ErlSerializerOptions options) =>
        new(term.AsDouble());
}

// The one constructor parameter matches no member, so there is no way to build this from a map.
public class NoUsableConstructor(string unmatched)
{
    public string Other { get; set; } = unmatched;
}

public class DuplicateNames
{
    public string Value { get; set; } = "";
    [ErlProperty("value")] public string Another { get; set; } = "";
}
