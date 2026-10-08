module Summa.Contracts.Tests.JsonTests

open Xunit
open Summa.Contracts

[<Fact>]
let ``canonical form sorts members by UTF-16 code unit and has no whitespace`` () =
    let value =
        Json.Object [ "b", Json.Integer 1L; "a", Json.Array [ Json.Null; Json.Bool true ]; "B", Json.String "x"; "é", Json.Bool false ]

    Assert.Equal("""{"B":"x","a":[null,true],"b":1,"é":false}""", Json.serialize value)

[<Fact>]
let ``canonical strings escape only quotes, backslashes and control characters`` () =
    Assert.Equal("\"q\\\" b\\\\ \\n\\t\\u0001 é ✓ /\"", Json.serialize (Json.String "q\" b\\ \n\t\u0001 é ✓ /"))

[<Fact>]
let ``parsing then serializing canonical text gives the same text`` () =
    let text = """{"a":[1,-2,{"c":null}],"b":"é\n"}"""
    let parsed = Json.parse text
    Assert.Equal(Ok """{"a":[1,-2,{"c":null}],"b":"é\n"}""", parsed |> Result.map Json.serialize)

[<Theory>]
[<InlineData("""{"a":1,"a":2}""")>]
[<InlineData("""{"a":1.5}""")>]
[<InlineData("""{"a":1,}""")>]
[<InlineData("""{"a":1} // comment""")>]
[<InlineData("""{"a":"\ud800"}""")>]
[<InlineData("not json")>]
let ``strict parsing refuses duplicates, fractions, trailing commas, comments and lone surrogates`` (text: string) =
    Assert.True(Result.isError (Json.parse text))

[<Fact>]
let ``parsing refuses more than 32 levels of nesting`` () =
    let deep = String.replicate 40 "[" + String.replicate 40 "]"
    Assert.True(Result.isError (Json.parse deep))
