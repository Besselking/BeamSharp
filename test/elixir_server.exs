# An ordinary Elixir GenServer for the C# node to call into.
#
#   elixir --sname exserver --cookie testcookie test/elixir_server.exs

defmodule EchoServer do
  use GenServer

  def start_link(_), do: GenServer.start_link(__MODULE__, nil, name: :echo_server)

  @impl true
  def init(_), do: {:ok, %{casts: []}}

  @impl true
  def handle_call({:add, a, b}, _from, state), do: {:reply, a + b, state}
  def handle_call({:echo, term}, _from, state), do: {:reply, term, state}
  def handle_call(:whoami, _from, state), do: {:reply, {:elixir, node()}, state}
  def handle_call(:casts, _from, state), do: {:reply, Enum.reverse(state.casts), state}
  def handle_call(:boom, _from, _state), do: raise("deliberate crash")

  @impl true
  def handle_cast(msg, state), do: {:noreply, %{state | casts: [msg | state.casts]}}

  @impl true
  def handle_info({:ping, pid}, state) do
    send(pid, {:pong, node()})
    {:noreply, state}
  end

  def handle_info(_, state), do: {:noreply, state}
end

defmodule Maths do
  def double(n), do: n * 2
  def concat(a, b), do: a <> b
end

# Supervised so the deliberate-crash test does not take the whole node with it.
{:ok, _} = Supervisor.start_link([{EchoServer, nil}], strategy: :one_for_one)
IO.puts("ready: #{node()}")
Process.sleep(:infinity)
