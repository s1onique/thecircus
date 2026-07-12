module App exposing
    ( Effect(..)
    , Model
    , Msg(..)
    , init
    , initialModel
    , requestProduct
    , update
    , view
    )

import Api
import Html exposing (Html, button, div, h1, main_, p, text)
import Html.Attributes exposing (class)
import Html.Events exposing (onClick)
import Http
import Product exposing (Product)
import RemoteData exposing (RemoteData(..))


{-| The application model.
-}
type alias Model =
    { product : RemoteData Http.Error Product
    }


{-| Application messages.
-}
type Msg
    = ProductRequested
    | ProductReceived (Result Http.Error Product)


{-| Side effects produced by `update`. The `view` layer cannot observe these
directly; tests assert the effect value to confirm what the application
intends to do.
-}
type Effect
    = None
    | RequestProduct


{-| Initial empty model.
-}
initialModel : Model
initialModel =
    { product = NotAsked
    }


{-| Boot the application by issuing the initial product request.
-}
init : () -> ( Model, Effect )
init _ =
    ( { product = Loading }
    , RequestProduct
    )


{-| Pure transition function. Returns the next model and the effect that the
runtime must dispatch.
-}
update : Msg -> Model -> ( Model, Effect )
update msg model =
    case msg of
        ProductRequested ->
            ( { model | product = Loading }
            , RequestProduct
            )

        ProductReceived result ->
            ( { model | product = RemoteData.fromResult result }
            , None
            )


{-| Map an effect to the runtime command that performs it.
-}
requestProduct : Effect -> Cmd Msg
requestProduct effect =
    case effect of
        RequestProduct ->
            Api.getProduct ProductReceived

        None ->
            Cmd.none


{-| Render the model.
-}
view : Model -> Html Msg
view model =
    main_ [ class "app-container" ]
        [ case model.product of
            NotAsked ->
                viewNotAsked

            Loading ->
                viewLoading

            Failure error ->
                viewFailure error

            Success product ->
                viewSuccess product
        ]


viewNotAsked : Html Msg
viewNotAsked =
    div [ class "state state--initial" ]
        [ text "Press the button to load product information." ]


viewLoading : Html Msg
viewLoading =
    div [ class "state state--loading" ]
        [ p [] [ text "Loading product information..." ] ]


viewFailure : Http.Error -> Html Msg
viewFailure error =
    let
        description =
            case error of
                Http.BadUrl url ->
                    "Invalid URL: " ++ url

                Http.Timeout ->
                    "Request timed out."

                Http.NetworkError ->
                    "Network error."

                Http.BadStatus status ->
                    "Server returned status " ++ String.fromInt status ++ "."

                Http.BadBody message ->
                    "Invalid response body: " ++ message ++ "."
    in
    div [ class "state state--failure" ]
        [ p [ class "state__error" ] [ text description ]
        , button
            [ class "state__retry"
            , onClick ProductRequested
            ]
            [ text "Retry" ]
        ]


viewSuccess : Product -> Html Msg
viewSuccess product =
    div [ class "state state--success" ]
        [ h1 [ class "state__name" ] [ text product.name ]
        , p [ class "state__tagline" ] [ text product.tagline ]
        , p [ class "state__description" ] [ text product.description ]
        ]
