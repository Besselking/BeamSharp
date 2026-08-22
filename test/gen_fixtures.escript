#!/usr/bin/env escript
%%! -sname fixgen
%%
%% Regenerates test/fixtures.txt from a real Erlang runtime, so the C# codec is
%% checked against bytes the BEAM actually produces rather than against itself.
%%
%%   escript test/gen_fixtures.escript > test/fixtures.txt

main(_) ->
    Cases = [
        {"atom_ok", ok},
        {"atom_unicode", binary_to_atom(<<"héllo"/utf8>>, utf8)},
        {"int_0", 0}, {"int_255", 255}, {"int_256", 256}, {"int_neg1", -1},
        {"int_max32", 2147483647}, {"int_min32", -2147483648},
        {"bignum_pos", 123456789012345678901234567890},
        {"bignum_neg", -98765432109876543210},
        {"float", 3.14159},
        {"float_neg_zero", -0.0},
        {"binary", <<"hello world">>},
        {"binary_empty", <<>>},
        {"bitstring", <<5:3>>},
        {"nil", []},
        {"charlist", "abc"},
        {"list_mixed", [1, a, <<"b">>, 2.0]},
        {"improper", [a|b]},
        {"tuple0", {}},
        {"tuple3", {1, two, <<"three">>}},
        {"map", #{a => 1, <<"b">> => [2], {c} => #{}}},
        {"map_empty", #{}},
        {"nested", {ok, [#{k => [1,2,3]}, {nested, {deep, [<<"x">>]}}]}},
        {"export", fun lists:reverse/1},
        {"string_255", lists:seq(1, 255)}
    ],
    [emit(Name, term_to_binary(Term)) || {Name, Term} <- Cases],
    emit("digest_12345", erlang:md5("testcookie" ++ integer_to_list(12345))),
    emit("digest_4294967295", erlang:md5("secret" ++ integer_to_list(4294967295))).

emit(Name, Bin) ->
    io:format("~s|~s~n", [Name, [io_lib:format("~2.16.0B", [B]) || B <- binary_to_list(Bin)]]).
