using BeamSharp.Serialization;
using BeamSharp.Terms;

// Published with PublishAot=true. Everything here has to work with no runtime code generation and
// no metadata the trimmer could have removed, so a green run is the actual evidence that the
// generated path is AOT-safe.
//
// This project does not reference BeamSharp.Serialization.Reflection at all, which is what makes
// the evidence airtight: there is no reflection fallback present to quietly take over, so an
// undeclared type can only fail.

var failures = 0;

void Check(string name, Func<bool> body)
{
    try
    {
        var ok = body();
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}");
        if (!ok) failures++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAIL  {name}  threw {ex.GetType().Name}: {ex.Message}");
        failures++;
    }
}

var context = AotContext.Default;

Check("a record round trips", () =>
{
    var term = ErlSerializer.Serialize(new Person("Ada", 36), context);
    return ErlSerializer.Deserialize<Person>(term, context) == new Person("Ada", 36);
});

Check("an Elixir struct shape carries __struct__", () =>
{
    var map = (ErlMap)ErlSerializer.Serialize(new Employee("Grace", Role.OnLeave, null), context);
    return map.Get("__struct__")!.IsAtom("Elixir.BeamSharp.Employee");
});

Check("an enum becomes an atom", () =>
{
    var map = (ErlMap)ErlSerializer.Serialize(new Employee("Grace", Role.OnLeave, null), context);
    return map.Get("role")!.IsAtom("on_leave");
});

Check("a null becomes nil", () =>
{
    var map = (ErlMap)ErlSerializer.Serialize(new Employee("Grace", Role.OnLeave, null), context);
    return map.Get("manager")!.IsAtom("nil");
});

// The interesting ones: these go through generic converter factories, which is where an AOT
// runtime would fail if the instantiations were only reachable through MakeGenericType.
Check("a list of records round trips", () =>
{
    var team = new Team("core", [new Person("Ada", 36), new Person("Alan", 41)], new Dictionary<string, int>
    {
        ["ada"] = 1,
        ["alan"] = 2
    });

    var back = ErlSerializer.Deserialize<Team>(ErlSerializer.Serialize(team, context), context);
    return back.Members.Count == 2 && back.Members[1].FirstName == "Alan" && back.Scores["ada"] == 1;
});

Check("a nullable value type round trips", () =>
{
    var term = ErlSerializer.Serialize(new Measurement(21.5, null), context);
    var back = ErlSerializer.Deserialize<Measurement>(term, context);
    return back.Value == 21.5 && back.Previous is null;
});

Check("a value-type collection round trips", () =>
{
    var term = ErlSerializer.Serialize(new Readings([1, 2, 3]), context);
    return ErlSerializer.Deserialize<Readings>(term, context).Samples.Count == 3;
});

Check("the whole thing survives the external term format", () =>
{
    var encoded = TermEncoder.Encode(ErlSerializer.Serialize(new Person("Ada", 36), context));
    return ErlSerializer.Deserialize<Person>(TermDecoder.Decode(encoded), context) == new Person("Ada", 36);
});

Check("a bare collection declared at the top level round trips", () =>
{
    List<Person> people = [new Person("Ada", 36), new Person("Alan", 41)];
    var back = ErlSerializer.Deserialize<List<Person>>(ErlSerializer.Serialize(people, context), context);
    return back.Count == 2 && back[0].FirstName == "Ada";
});

Check("an undeclared type fails at the call site", () =>
{
    try
    {
        ErlSerializer.Serialize(new Undeclared("x"), context);
        return false;
    }
    catch (ErlSerializationException ex)
    {
        return ex.Message.Contains("AddReflectionFallback") && ex.Message.Contains("Undeclared");
    }
});

Console.WriteLine(failures == 0 ? "\nAOT probe: all checks passed" : $"\nAOT probe: {failures} failed");
return failures;

internal record Person(string FirstName, int Age);

internal enum Role
{
    Active,
    OnLeave
}

[ErlStruct("BeamSharp.Employee")]
internal record Employee(string Name, Role Role, string? Manager);

internal record Team(string Name, List<Person> Members, Dictionary<string, int> Scores);

internal record Measurement(double Value, double? Previous);

internal record Readings(List<int> Samples);

internal record Undeclared(string Name);

[ErlSerializable(typeof(Person))]
[ErlSerializable(typeof(Employee))]
[ErlSerializable(typeof(Team))]
[ErlSerializable(typeof(Measurement))]
[ErlSerializable(typeof(Readings))]
[ErlSerializable(typeof(List<Person>))]
[ErlSerializable(typeof(Role))]
internal partial class AotContext : ErlSerializerContext;
