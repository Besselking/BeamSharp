# Exercises the C# node from a real Elixir node.
#
#   elixir --sname tester --cookie testcookie \
#     -r test/elixir_structs.exs test/elixir_client.exs
#
# The structs load with -r so they are compiled before this file, which is what lets it use
# struct literals and patterns.

target = :"csharp@#{:inet.gethostname() |> elem(1)}"

results = :ets.new(:results, [:public, :ordered_set])
counter = :counters.new(1, [])

check = fn name, fun ->
  n = :counters.get(counter, 1)
  :counters.add(counter, 1, 1)

  {status, detail} =
    try do
      case fun.() do
        {:ok, detail} -> {:pass, detail}
        {:error, detail} -> {:fail, detail}
      end
    rescue
      e -> {:fail, "raised #{inspect(e)}"}
    catch
      kind, reason -> {:fail, "#{kind} #{inspect(reason)}"}
    end

  :ets.insert(results, {n, status, name, detail})
  icon = if status == :pass, do: "PASS", else: "FAIL"
  IO.puts("#{icon}  #{name}  #{detail}")
end

expect = fn name, actual, expected ->
  check.(name, fn ->
    if actual.() == expected,
      do: {:ok, inspect(expected)},
      else: {:error, "expected #{inspect(expected)}, got #{inspect(actual.())}"}
  end)
end

IO.puts("\n=== talking to #{target} from #{node()} ===\n")

expect.("Node.connect/1", fn -> Node.connect(target) end, true)
expect.("Node.ping/1", fn -> Node.ping(target) end, :pong)

check.("Node.list(:hidden) contains the node", fn ->
  if target in Node.list(:hidden) or target in Node.list(:connected),
    do: {:ok, inspect(Node.list(:connected))},
    else: {:error, inspect(Node.list(:connected))}
end)

expect.("GenServer.call {:add, 40, 2}", fn ->
  GenServer.call({:calculator, target}, {:add, 40, 2})
end, 42)

expect.("GenServer.call echo of a nested term", fn ->
  GenServer.call({:calculator, target}, {:echo, %{list: [1, 2.5, :three], bin: "vier", tup: {5, 6}}})
end, %{list: [1, 2.5, :three], bin: "vier", tup: {5, 6}})

expect.("GenServer.call echo of a big integer", fn ->
  GenServer.call({:calculator, target}, {:echo, 123_456_789_012_345_678_901_234_567_890})
end, 123_456_789_012_345_678_901_234_567_890)

expect.("GenServer.call echo of a negative big integer", fn ->
  GenServer.call({:calculator, target}, {:echo, -98_765_432_109_876_543_210})
end, -98_765_432_109_876_543_210)

expect.("GenServer.call echo of a pid and ref", fn ->
  ref = make_ref()
  GenServer.call({:calculator, target}, {:echo, {self(), ref}}) == {self(), ref}
end, true)

expect.("GenServer.call echo of a charlist", fn ->
  GenServer.call({:calculator, target}, {:echo, ~c"charlist"})
end, ~c"charlist")

expect.("GenServer.call echo of a bitstring", fn ->
  GenServer.call({:calculator, target}, {:echo, <<1::size(3)>>})
end, <<1::size(3)>>)

expect.("GenServer.call :who", fn ->
  GenServer.call({:calculator, target}, :who)
end, {:csharp, Atom.to_string(target)})

expect.("deferred reply (GenServer.reply/2 equivalent)", fn ->
  GenServer.call({:calculator, target}, :slow, 5_000)
end, :worth_the_wait)

check.("GenServer.cast/2", fn ->
  :ok = GenServer.cast({:calculator, target}, {:log, "cast from #{node()}"})
  {:ok, "sent"}
end)

check.("send/2 to a registered mailbox", fn ->
  send({:printer, target}, {:hello, self(), "plain send"})
  {:ok, "sent"}
end)

check.("call to an unregistered name exits with :noproc", fn ->
  try do
    GenServer.call({:nope, target}, :anything, 2_000)
    {:error, "expected an exit"}
  catch
    :exit, {:noproc, _} -> {:ok, ":noproc"}
    :exit, other -> {:error, "exited with #{inspect(other)}"}
  end
end)

check.("Process.monitor of a missing name fires :noproc", fn ->
  ref = Process.monitor({:nope, target})

  receive do
    {:DOWN, ^ref, :process, _, :noproc} -> {:ok, ":noproc"}
    {:DOWN, ^ref, :process, _, other} -> {:error, inspect(other)}
  after
    2_000 -> {:error, "no DOWN within 2s"}
  end
end)

check.("Process.monitor of a live mailbox stays quiet", fn ->
  ref = Process.monitor({:calculator, target})

  receive do
    {:DOWN, ^ref, :process, _, reason} -> {:error, "unexpected DOWN #{inspect(reason)}"}
  after
    500 ->
      Process.demonitor(ref, [:flush])
      {:ok, "no DOWN, as expected"}
  end
end)

check.("send/2 straight to a pid", fn ->
  {:ok, pid} = GenServer.call({:calculator, target}, {:spawn, "pid_target"})
  send(pid, {:direct, self()})
  GenServer.call({:calculator, target}, {:kill, "pid_target", :normal})
  {:ok, inspect(pid)}
end)

check.("monitor fires when the C# mailbox goes away", fn ->
  {:ok, pid} = GenServer.call({:calculator, target}, {:spawn, "monitored"})
  ref = Process.monitor(pid)
  :ok = GenServer.call({:calculator, target}, {:kill, "monitored", :shutdown})

  receive do
    {:DOWN, ^ref, :process, ^pid, :shutdown} -> {:ok, ":shutdown"}
    {:DOWN, ^ref, :process, _, other} -> {:error, "reason was #{inspect(other)}"}
  after
    3_000 -> {:error, "no DOWN within 3s"}
  end
end)

check.("a link to the C# mailbox delivers EXIT", fn ->
  {:ok, pid} = GenServer.call({:calculator, target}, {:spawn, "linked"})
  parent = self()

  spawn(fn ->
    Process.flag(:trap_exit, true)
    Process.link(pid)
    send(parent, :linked)

    receive do
      {:EXIT, ^pid, reason} -> send(parent, {:got_exit, reason})
    after
      3_000 -> send(parent, {:got_exit, :timeout})
    end
  end)

  receive do
    :linked -> :ok
  after
    2_000 -> throw(:link_setup_timeout)
  end

  :ok = GenServer.call({:calculator, target}, {:kill, "linked", :boom})

  receive do
    {:got_exit, :boom} -> {:ok, ":boom"}
    {:got_exit, other} -> {:error, "reason was #{inspect(other)}"}
  after
    4_000 -> {:error, "no EXIT within 4s"}
  end
end)

check.("monitoring by name, then killing it", fn ->
  {:ok, _pid} = GenServer.call({:calculator, target}, {:spawn, "named_monitored"})
  ref = Process.monitor({:named_monitored, target})
  :ok = GenServer.call({:calculator, target}, {:kill, "named_monitored", :done})

  receive do
    {:DOWN, ^ref, :process, _, :done} -> {:ok, ":done"}
    {:DOWN, ^ref, :process, _, other} -> {:error, "reason was #{inspect(other)}"}
  after
    3_000 -> {:error, "no DOWN within 3s"}
  end
end)

# --- serialization: C# objects arriving as Elixir structs -------------------

check.("a C# record arrives as a real %BeamSharp.Person{}", fn ->
  {:ok, person} = GenServer.call({:directory, target}, {:find, "ada"})

  cond do
    not is_struct(person, BeamSharp.Person) -> {:error, "not a struct: #{inspect(person)}"}
    person.first_name != "Ada" -> {:error, "wrong name: #{inspect(person)}"}
    person.age != 36 -> {:error, "wrong age: #{inspect(person)}"}
    true -> {:ok, inspect(person)}
  end
end)

check.("it pattern matches like any other struct", fn ->
  {:ok, %BeamSharp.Person{first_name: name, email: email}} =
    GenServer.call({:directory, target}, {:find, "alan"})

  if name == "Alan" and email == "alan@example.com",
    do: {:ok, "#{name} <#{email}>"},
    else: {:error, "got #{inspect({name, email})}"}
end)

expect.("PascalCase became snake_case atom keys", fn ->
  {:ok, person} = GenServer.call({:directory, target}, {:find, "ada"})
  person |> Map.from_struct() |> Map.keys() |> Enum.sort()
end, [:age, :email, :first_name, :status])

expect.("a C# enum arrives as an atom", fn ->
  {:ok, person} = GenServer.call({:directory, target}, {:find, "grace"})
  person.status
end, :on_leave)

expect.("a C# null arrives as nil", fn ->
  {:ok, person} = GenServer.call({:directory, target}, {:find, "grace"})
  person.email
end, nil)

expect.("a list of records arrives as a list of structs", fn ->
  target |> then(&GenServer.call({:directory, &1}, :all)) |> Enum.map(& &1.first_name)
end, ["Ada", "Grace", "Alan"])

expect.("a struct built in Elixir survives a round trip through a C# object", fn ->
  sent = %BeamSharp.Person{first_name: "Hedy", age: 30, email: "hedy@example.com", status: :active}
  GenServer.call({:directory, target}, {:echo, sent})
end, %BeamSharp.Person{first_name: "Hedy", age: 30, email: "hedy@example.com", status: :active})

expect.("C# can deserialize, modify and return the struct", fn ->
  sent = %BeamSharp.Person{first_name: "Hedy", age: 30, email: nil, status: :active}
  GenServer.call({:directory, target}, {:birthday, sent}).age
end, 31)

expect.(":rpc.call/4", fn -> :rpc.call(target, CSharp, :add, [2, 3]) end, 5)
expect.(":erpc.call/4", fn -> :erpc.call(target, :csharp, :reverse, ["stressed"]) end, "desserts")

check.(":erpc.call/4 of a missing function raises undef", fn ->
  try do
    :erpc.call(target, :csharp, :nope, [])
    {:error, "expected a raise"}
  rescue
    e in ErlangError ->
      case e.original do
        {:exception, {:undef, _}, _} -> {:ok, ":undef"}
        other -> {:error, inspect(other)}
      end

    e -> {:error, inspect(e)}
  end
end)

check.(":rpc.call/4 returning a map", fn ->
  case :rpc.call(target, CSharp, :info, []) do
    %{runtime: rt} -> {:ok, rt}
    other -> {:error, inspect(other)}
  end
end)

check.("a burst of 200 concurrent calls", fn ->
  results =
    1..200
    |> Task.async_stream(fn i -> GenServer.call({:calculator, target}, {:add, i, i}) end,
      max_concurrency: 32,
      timeout: 10_000
    )
    |> Enum.map(fn {:ok, v} -> v end)

  if results == Enum.map(1..200, &(&1 * 2)),
    do: {:ok, "200/200 correct"},
    else: {:error, "mismatched results"}
end)

all = :ets.tab2list(results)
failed = Enum.filter(all, fn {_, status, _, _} -> status == :fail end)

IO.puts("\n=== #{length(all) - length(failed)}/#{length(all)} passed ===")

if failed != [] do
  Enum.each(failed, fn {_, _, name, detail} -> IO.puts("  FAILED: #{name} — #{detail}") end)
  System.halt(1)
end

System.halt(0)
