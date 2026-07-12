module ApiTest exposing (suite)

import Expect
import Json.Decode as Decode
import Product
import Test exposing (Test)


{-| Decoder contract tests. Each decoder rejection is a separate test so a
single failing field is easy to identify.
-}
suite : Test
suite =
    Test.describe "Product decoder"
        [ Test.test "valid product response decodes" <|
            \() ->
                let
                    json =
                        """{"name":"The Circus","tagline":"Team-scale Leamas","description":"The team-scale coordination, evidence, and governance platform for Leamas."}"""

                    expected =
                        { name = "The Circus"
                        , tagline = "Team-scale Leamas"
                        , description = "The team-scale coordination, evidence, and governance platform for Leamas."
                        }
                in
                Decode.decodeString Product.decoder json
                    |> Expect.equal (Ok expected)
        , Test.test "missing name fails decoding" <|
            \() ->
                Decode.decodeString Product.decoder """{"tagline":"x","description":"y"}"""
                    |> Expect.err
        , Test.test "missing tagline fails decoding" <|
            \() ->
                Decode.decodeString Product.decoder """{"name":"x","description":"y"}"""
                    |> Expect.err
        , Test.test "missing description fails decoding" <|
            \() ->
                Decode.decodeString Product.decoder """{"name":"x","tagline":"y"}"""
                    |> Expect.err
        , Test.test "invalid field types fail decoding" <|
            \() ->
                Decode.decodeString Product.decoder """{"name":1,"tagline":true,"description":[]}"""
                    |> Expect.err
        ]
