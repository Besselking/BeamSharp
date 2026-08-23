# The Elixir side of the C# records the serializer maps onto. Kept in its own file because a
# struct literal or pattern cannot reference a module defined in the same compilation unit.

defmodule BeamSharp.Person do
  @moduledoc """
  Mirrors the C# `Person` record in samples/BeamSharp.Server. The C# side carries
  `[ErlStruct("BeamSharp.Person")]`, so its `__struct__` key names this module and values arrive
  here as ordinary structs.
  """
  defstruct [:first_name, :age, :email, :status]
end
