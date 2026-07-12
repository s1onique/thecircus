module AppTest exposing (suite)

import App
import Expect
import Html
import Html.Attributes as Attr
import Http
import Product
import RemoteData exposing (RemoteData(..))
import Test exposing (Test)
import Test.Html.Event as Event
import Test.Html.Query as Query
import Test.Html.Selector as Selector exposing (Selector)


sampleProduct : Product.Product
sampleProduct =
    { name = "The Circus"
    , tagline = "Team-scale Leamas"
    , description = "The team-scale coordination, evidence, and governance platform for Leamas."
    }


loadingModel : App.Model
loadingModel =
    { product = Loading }


successModel : App.Model
successModel =
    { product = Success sampleProduct }


failureModel : App.Model
failureModel =
    { product = Failure Http.NetworkError }


{-| Pure transition tests prove that the right next-state is produced without
needing to inspect Cmd values.
-}
transitionTests : Test
transitionTests =
    Test.describe "App.update transitions"
        [ Test.test "init starts in Loading" <|
            \() ->
                App.init ()
                    |> Tuple.first
                    |> .product
                    |> Expect.equal Loading
        , Test.test "init requests the product" <|
            \() ->
                App.init ()
                    |> Tuple.second
                    |> Expect.equal App.RequestProduct
        , Test.test "ProductReceived Ok replaces Loading with Success" <|
            \() ->
                App.update
                    (App.ProductReceived (Ok sampleProduct))
                    loadingModel
                    |> Tuple.first
                    |> .product
                    |> Expect.equal (Success sampleProduct)
        , Test.test "ProductReceived Err replaces Loading with Failure" <|
            \() ->
                App.update
                    (App.ProductReceived (Err Http.NetworkError))
                    loadingModel
                    |> Tuple.first
                    |> .product
                    |> Expect.equal (Failure Http.NetworkError)
        , Test.test "ProductRequested on failure produces RequestProduct effect" <|
            \() ->
                App.update App.ProductRequested failureModel
                    |> Tuple.second
                    |> Expect.equal App.RequestProduct
        , Test.test "ProductReceived has no effect" <|
            \() ->
                App.update
                    (App.ProductReceived (Ok sampleProduct))
                    loadingModel
                    |> Tuple.second
                    |> Expect.equal App.None
        ]


{-| View tests use Test.Html.Query to assert rendered content semantically.
-}
viewTests : Test
viewTests =
    Test.describe "App.view rendering"
        [ Test.test "loading state renders loading text" <|
            \() ->
                App.view loadingModel
                    |> Query.fromHtml
                    |> Query.find [ Selector.class "state--loading" ]
                    |> Query.contains [ Html.text "Loading product information..." ]
        , Test.test "successful state renders name, tagline, and description" <|
            \() ->
                App.view successModel
                    |> Query.fromHtml
                    |> Query.find [ Selector.class "state--success" ]
                    |> Query.contains
                        [ Html.text "The Circus"
                        , Html.text "Team-scale Leamas"
                        , Html.text "The team-scale coordination, evidence, and governance platform for Leamas."
                        ]
        , Test.test "successful state uses an h1 for the product name" <|
            \() ->
                App.view successModel
                    |> Query.fromHtml
                    |> Query.find
                        [ Selector.tag "h1" ]
                    |> Query.contains [ Html.text "The Circus" ]
        , Test.test "failure state renders an error description" <|
            \() ->
                App.view failureModel
                    |> Query.fromHtml
                    |> Query.find [ Selector.class "state--failure" ]
                    |> Query.find [ Selector.class "state__error" ]
                    |> Query.has [ Selector.tag "p" ]
        , Test.test "failure state renders a retry button" <|
            \() ->
                App.view failureModel
                    |> Query.fromHtml
                    |> Query.find
                        [ Selector.tag "button", Selector.class "state__retry" ]
                    |> Query.has [ Selector.text "Retry" ]
        , Test.test "retry click triggers a ProductRequested event" <|
            \() ->
                App.view failureModel
                    |> Query.fromHtml
                    |> Query.find [ Selector.class "state__retry" ]
                    |> Event.simulate Event.click
                    |> Event.expect App.ProductRequested
        ]


{-| Discovered by elm-test from the exposed `suite` value.
-}
suite : Test
suite =
    Test.describe "App"
        [ transitionTests
        , viewTests
        ]
